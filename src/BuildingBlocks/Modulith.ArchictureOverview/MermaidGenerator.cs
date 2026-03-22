using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;

namespace Modulith.ArchitectureOverview;

/// <summary>
/// An incremental source generator that produces a C4 Level 3 Component Diagram using Mermaid syntax.
/// Optimized for extreme performance by separating the semantic extraction phase from the diagram generation phase.

/// </summary>
[Generator]
public class ComponentDiagramGenerator : IIncrementalGenerator
{
    /// <summary>
    /// Initializes the incremental generator pipelines. 
    /// Registers syntax providers that filter and cache symbol data to prevent unnecessary re-evaluations.
    /// </summary>
    /// <param name="context">The incremental generator context provided by the Roslyn compiler.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Provider for extracting classes and interfaces
        var typeDeclarations = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => node is ClassDeclarationSyntax or InterfaceDeclarationSyntax,
            transform: static (ctx, _) => ExtractTypeMetadata(ctx))
            .Where(static m => m is not null)!;

        // Provider for extracting API endpoint mappings (MapGet, MapPost, etc.)
        var endpointDeclarations = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => node is InvocationExpressionSyntax invocation &&
                                           invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                                           memberAccess.Name.Identifier.Text.StartsWith("Map"),
            transform: static (ctx, _) => ExtractEndpointMetadata(ctx))
            .Where(static m => m is not null)!;

        // Combine all extracted data with the compilation and analyzer options
        var pipeline = context.CompilationProvider
            .Combine(typeDeclarations.Collect())
            .Combine(endpointDeclarations.Collect())
            .Combine(context.AnalyzerConfigOptionsProvider);

        context.RegisterSourceOutput(pipeline, static (spc, source) =>
            Execute(spc, source.Left.Left.Right, source.Left.Right, source.Right));
    }

    /// <summary>
    /// The core execution pipeline. Rebuilds the relationship dictionaries from the cached incremental models
    /// and generates the final Mermaid diagram and C# wrapper.
    /// </summary>
    /// <param name="context">The source production context to output generated files.</param>
    /// <param name="types">The immutable array of extracted type metadata.</param>
    /// <param name="endpoints">The immutable array of extracted endpoint metadata.</param>
    /// <param name="options">The analyzer options containing MSBuild properties.</param>
    private static void Execute(
        SourceProductionContext context,
        ImmutableArray<TypeMetadata> types,
        ImmutableArray<EndpointMetadata> endpoints,
        AnalyzerConfigOptionsProvider options)
    {
        // Guard clause to ensure we only generate diagrams for .api projects
        if (options.GlobalOptions.TryGetValue("build_property.projectname", out string projectName) &&
            !projectName.EndsWith(".api", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (types.IsDefaultOrEmpty && endpoints.IsDefaultOrEmpty) return;

        var typeNodes = new Dictionary<string, TypeNode>(StringComparer.OrdinalIgnoreCase);
        var dependencies = new HashSet<DependencyEdge>();
        var finalEndpoints = new List<EndpointInfo>();

        var interfaceToImplementation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var messageHandlers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var handlerMessages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dispatchedMessages = new List<(string CallerId, string MessageType)>();

        // Reconstruct mappings from our highly-optimized immutable records
        foreach (var type in types)
        {
            if (type.TypeKind != TypeKind.Interface)
            {
                typeNodes[type.FullName] = new TypeNode(type.FullName, type.Module, type.Name, type.ArtifactType);
            }

            foreach (var iface in type.Interfaces)
            {
                interfaceToImplementation[iface] = type.FullName;
            }

            if (!string.IsNullOrEmpty(type.HandledMessage))
            {
                handlerMessages[type.FullName] = type.HandledMessage;
                if (!messageHandlers.ContainsKey(type.HandledMessage))
                {
                    messageHandlers[type.HandledMessage] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                messageHandlers[type.HandledMessage].Add(type.FullName);
            }

            foreach (var dep in type.Dependencies)
            {
                dependencies.Add(new DependencyEdge(type.FullName, dep, "Calls", "-->"));
            }

            foreach (var msg in type.DispatchedMessages)
            {
                dispatchedMessages.Add((type.FullName, msg));
            }
        }

        foreach (var ep in endpoints)
        {
            finalEndpoints.Add(new EndpointInfo(ep.Verb, ep.Route, ep.Id));

            foreach (var dep in ep.Dependencies)
            {
                dependencies.Add(new DependencyEdge(ep.Id, dep, "Calls", "==>"));
            }

            foreach (var msg in ep.DispatchedMessages)
            {
                dispatchedMessages.Add((ep.Id, msg));
            }
        }

        var resolvedDeps = ResolveConcreteDependencies(dependencies, interfaceToImplementation);

        BridgeDynamicDispatch(resolvedDeps, dispatchedMessages, messageHandlers, handlerMessages, interfaceToImplementation, typeNodes);

        string mermaidSyntax = GenerateMermaidSyntax(typeNodes.Values, resolvedDeps, finalEndpoints);
        string sourceCode = GenerateCSharpWrapper(mermaidSyntax);

        context.AddSource("ComponentDiagram.g.cs", SourceText.From(sourceCode, Encoding.UTF8));

        WriteDiagramToDisk(options, mermaidSyntax);
    }

    /// <summary>
    /// Transforms an AST node representing a Class or Interface into a lightweight, immutable <see cref="TypeMetadata"/> record.
    /// </summary>
    /// <param name="context">The syntax context providing access to the semantic model.</param>
    /// <returns>A populated metadata record, or null if the symbol cannot be resolved.</returns>
    private static TypeMetadata ExtractTypeMetadata(GeneratorSyntaxContext context)
    {
        if (context.SemanticModel.GetDeclaredSymbol(context.Node) is not INamedTypeSymbol typeSymbol)
            return null;

        string fullName = typeSymbol.ToDisplayString();
        string moduleName = ExtractModuleName(typeSymbol.ContainingNamespace?.ToDisplayString() ?? string.Empty);
        ArtifactType artifactType = ClassifyArtifact(typeSymbol);

        var interfaces = new List<string>();
        string handledMessage = null;

        if (typeSymbol.TypeKind == TypeKind.Class && !typeSymbol.IsAbstract)
        {
            foreach (var iface in typeSymbol.AllInterfaces)
            {
                string ifaceName = iface.ToDisplayString();
                interfaces.Add(ifaceName);

                if ((iface.Name.Contains("Handler") || iface.Name.Contains("IEventHandler")) && iface.TypeArguments.Length > 0)
                {
                    handledMessage = iface.TypeArguments[0].ToDisplayString();
                }
            }

            if (handledMessage == null && fullName.EndsWith("Handler"))
            {
                var handleMethod = typeSymbol.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == "Handle" && m.Parameters.Length > 0);
                if (handleMethod != null)
                {
                    handledMessage = handleMethod.Parameters[0].Type.ToDisplayString();
                }
            }
        }

        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var constructor in typeSymbol.Constructors)
        {
            foreach (var parameter in constructor.Parameters)
            {
                string targetType = parameter.Type.ToDisplayString();
                if (!string.Equals(fullName, targetType, StringComparison.OrdinalIgnoreCase))
                {
                    dependencies.Add(targetType);
                }
            }
        }

        var dispatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invocations = context.Node.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is IMethodSymbol methodSymbol && methodSymbol.ContainingType != null)
            {
                string targetType = methodSymbol.ContainingType.ToDisplayString();
                if (!string.Equals(fullName, targetType, StringComparison.OrdinalIgnoreCase))
                {
                    dependencies.Add(targetType);
                }
            }

            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                var argType = context.SemanticModel.GetTypeInfo(arg.Expression).Type;
                if (argType != null && (argType.Name.EndsWith("Event") || argType.Name.EndsWith("Command")))
                {
                    dispatched.Add(argType.ToDisplayString());
                }
            }
        }

        return new TypeMetadata(
            fullName,
            typeSymbol.Name,
            moduleName,
            artifactType,
            typeSymbol.TypeKind,
            interfaces.ToImmutableArray(),
            dependencies.ToImmutableArray(),
            dispatched.ToImmutableArray(),
            handledMessage);
    }

    /// <summary>
    /// Transforms an API mapping invocation into a lightweight <see cref="EndpointMetadata"/> record.
    /// </summary>
    /// <param name="context">The syntax context for analyzing lambda bodies.</param>
    /// <returns>A populated metadata record, or null if it's not a valid mapped endpoint.</returns>
    private static EndpointMetadata ExtractEndpointMetadata(GeneratorSyntaxContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol symbol || !symbol.Name.StartsWith("Map"))
            return null;

        var routeArg = invocation.ArgumentList.Arguments.FirstOrDefault();
        if (routeArg == null) return null;

        var route = routeArg.Expression.ToString().Trim('"');
        var verb = symbol.Name.Replace("Map", "").ToUpper();
        var epId = $"EP_{Sanitize(verb + route)}";

        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dispatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var lambda = invocation.ArgumentList.Arguments
            .Select(a => a.Expression)
            .FirstOrDefault(e => e is LambdaExpressionSyntax) as LambdaExpressionSyntax;

        if (lambda != null)
        {
            var bodyCalls = lambda.DescendantNodes().OfType<InvocationExpressionSyntax>();
            foreach (var bc in bodyCalls)
            {
                if (context.SemanticModel.GetSymbolInfo(bc).Symbol is IMethodSymbol bcSymbol && bcSymbol.ContainingType != null)
                {
                    dependencies.Add(bcSymbol.ContainingType.ToDisplayString());
                }

                foreach (var arg in bc.ArgumentList.Arguments)
                {
                    var argType = context.SemanticModel.GetTypeInfo(arg.Expression).Type;
                    if (argType != null && (argType.Name.EndsWith("Event") || argType.Name.EndsWith("Command")))
                    {
                        dispatched.Add(argType.ToDisplayString());
                    }
                }
            }
        }

        return new EndpointMetadata(verb, route, epId, dependencies.ToImmutableArray(), dispatched.ToImmutableArray());
    }

    /// <summary>
    /// Rewrites abstract interface dependencies into their resolved concrete implementations.
    /// </summary>
    /// <param name="abstractDeps">The collection of dependencies mapped to abstract interfaces.</param>
    /// <param name="interfaceMap">The dictionary mapping interfaces to concrete class full names.</param>
    /// <returns>A newly resolved HashSet of dependency edges.</returns>
    private static HashSet<DependencyEdge> ResolveConcreteDependencies(HashSet<DependencyEdge> abstractDeps, Dictionary<string, string> interfaceMap)
    {
        var resolved = new HashSet<DependencyEdge>();
        foreach (var dep in abstractDeps)
        {
            string source = interfaceMap.TryGetValue(dep.SourceFullName, out string concreteSource) ? concreteSource : dep.SourceFullName;
            string target = interfaceMap.TryGetValue(dep.TargetFullName, out string concreteTarget) ? concreteTarget : dep.TargetFullName;

            if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                resolved.Add(new DependencyEdge(source, target, dep.Label, dep.EdgeStyle));
            }
        }
        return resolved;
    }

    /// <summary>
    /// Intercepts direct dependencies and routes them dynamically through Message nodes (Events/Commands).
    /// </summary>
    /// <param name="dependencies">The current collection of structural edges.</param>
    /// <param name="dispatchedMessages">The tracked list of dispatched event/command messages.</param>
    /// <param name="messageHandlers">The dictionary connecting messages to handling classes.</param>
    /// <param name="handlerMessages">The dictionary connecting handling classes back to their expected message.</param>
    /// <param name="interfaceMap">The interface to implementation mapping dictionary.</param>
    /// <param name="typeNodes">The collection of tracked architectural types.</param>
    private static void BridgeDynamicDispatch(HashSet<DependencyEdge> dependencies, List<(string CallerId, string MessageType)> dispatchedMessages, Dictionary<string, HashSet<string>> messageHandlers, Dictionary<string, string> handlerMessages, Dictionary<string, string> interfaceMap, Dictionary<string, TypeNode> typeNodes)
    {
        var edgesToRemove = new List<DependencyEdge>();
        var edgesToAdd = new List<DependencyEdge>();

        foreach (var dep in dependencies)
        {
            if (handlerMessages.TryGetValue(dep.TargetFullName, out string messageType))
            {
                edgesToRemove.Add(dep);
                string messageShortName = messageType.Split('.').Last();
                if (!typeNodes.ContainsKey(messageType))
                {
                    typeNodes[messageType] = new TypeNode(messageType, ExtractModuleName(messageType), messageShortName, ArtifactType.Message);
                }

                bool isEvent = messageType.EndsWith("Event");
                string toMsgStyle = isEvent ? "-.->" : "==>";
                string toMsgLabel = isEvent ? "Publishes" : "Sends";

                edgesToAdd.Add(new DependencyEdge(dep.SourceFullName, messageType, toMsgLabel, toMsgStyle));
                edgesToAdd.Add(new DependencyEdge(messageType, dep.TargetFullName, "Handled by", toMsgStyle));
            }
        }

        foreach (var dm in dispatchedMessages)
        {
            string caller = interfaceMap.TryGetValue(dm.CallerId, out string concreteCaller) ? concreteCaller : dm.CallerId;
            string messageType = dm.MessageType;
            string messageShortName = messageType.Split('.').Last();

            if (!typeNodes.ContainsKey(messageType))
            {
                typeNodes[messageType] = new TypeNode(messageType, ExtractModuleName(messageType), messageShortName, ArtifactType.Message);
            }

            bool isEvent = messageType.EndsWith("Event");
            string toMsgStyle = isEvent ? "-.->" : "==>";
            string toMsgLabel = isEvent ? "Publishes" : "Sends";

            edgesToAdd.Add(new DependencyEdge(caller, messageType, toMsgLabel, toMsgStyle));

            if (messageHandlers.TryGetValue(messageType, out var handlers))
            {
                foreach (var handler in handlers)
                {
                    string resolvedHandler = interfaceMap.TryGetValue(handler, out string concreteHandler) ? concreteHandler : handler;
                    if (!string.Equals(caller, resolvedHandler, StringComparison.OrdinalIgnoreCase))
                    {
                        edgesToAdd.Add(new DependencyEdge(messageType, resolvedHandler, "Handled by", toMsgStyle));
                    }
                }
            }
        }

        foreach (var edge in edgesToRemove) dependencies.Remove(edge);
        foreach (var edge in edgesToAdd) dependencies.Add(edge);
    }

    /// <summary>
    /// Generates the raw text payload for the Mermaid Diagram representation.
    /// </summary>
    /// <param name="nodes">The system component nodes.</param>
    /// <param name="dependencies">The calculated edges between components.</param>
    /// <param name="endpoints">The mapped API endpoints acting as ingress points.</param>
    /// <returns>A formatted Mermaid graphical string.</returns>
    private static string GenerateMermaidSyntax(IEnumerable<TypeNode> nodes, HashSet<DependencyEdge> dependencies, List<EndpointInfo> endpoints)
    {
        var sb = new StringBuilder(8192); // Pre-allocate larger buffer
        sb.AppendLine("%%{init: {'flowchart': {'defaultRenderer': 'elk'}} }%%");
        sb.AppendLine("graph TD");

        sb.AppendLine("    classDef endpoint fill:#4caf50,stroke:#2e7d32,stroke-width:2px,color:white;");
        sb.AppendLine("    classDef facade fill:#e3f2fd,stroke:#1565c0,stroke-width:2px;");
        sb.AppendLine("    classDef handler fill:#7e57c2,stroke:#4a148c,stroke-width:2px,color:white;");
        sb.AppendLine("    classDef db fill:#efebe9,stroke:#4e342e,stroke-width:2px,shape:cylinder;");
        sb.AppendLine("    classDef message fill:#fff9c4,stroke:#fbc02d,stroke-width:1px,stroke-dasharray: 5 5;");
        sb.AppendLine("    classDef generic fill:#f5f5f5,stroke:#9e9e9e,stroke-width:1px;");

        var trackedTypeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ep in endpoints)
        {
            sb.AppendLine($"    {ep.Id}([\"{ep.Verb} {ep.Route}\"]):::endpoint");
            trackedTypeIds.Add(ep.Id);
        }

        foreach (var moduleGroup in nodes.GroupBy(n => n.Module))
        {
            bool isCore = moduleGroup.Key == "Core";
            if (!isCore) sb.AppendLine($"    subgraph {Sanitize(moduleGroup.Key)}_Module [\"{moduleGroup.Key} Module\"]");

            foreach (var node in moduleGroup)
            {
                string shape = node.ArtifactType == ArtifactType.Repository ? "[(" : (node.ArtifactType == ArtifactType.Message ? "{{" : "(");
                string endShape = node.ArtifactType == ArtifactType.Repository ? ")]" : (node.ArtifactType == ArtifactType.Message ? "}}" : ")");

                string style = node.ArtifactType switch
                {
                    ArtifactType.Repository => "db",
                    ArtifactType.Entrypoint => "facade",
                    ArtifactType.Handler => "handler",
                    ArtifactType.Message => "message",
                    _ => "generic"
                };

                string indent = isCore ? "    " : "        ";
                sb.AppendLine($"{indent}{Sanitize(node.FullName)}{shape}\"{node.Name}\"{endShape}:::{style}");
                trackedTypeIds.Add(node.FullName);
            }

            if (!isCore) sb.AppendLine("    end");
        }

        foreach (var dep in dependencies)
        {
            if (trackedTypeIds.Contains(dep.TargetFullName) && trackedTypeIds.Contains(dep.SourceFullName))
            {
                sb.AppendLine($"    {Sanitize(dep.SourceFullName)} {dep.EdgeStyle}|\"{dep.Label}\"| {Sanitize(dep.TargetFullName)}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates the C# static class containing the final Mermaid script.
    /// Injects the code into the specific target namespace.
    /// </summary>
    /// <param name="mermaidSyntax">The raw diagram payload.</param>
    /// <returns>A valid C# class string.</returns>
    private static string GenerateCSharpWrapper(string mermaidSyntax)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("using System;");
        sb.AppendLine("namespace Faster.Modulith;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Contains the generated Mermaid diagram representing the system architecture.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class ComponentDiagram {");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// The raw string defining the C4 Level 3 Component Diagram.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public const string MasterDiagram = @\"");

        string escapedSyntax = mermaidSyntax.Replace("\"", "\"\"");
        sb.Append(escapedSyntax);

        sb.AppendLine("\";");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Safely attempts to output the raw Mermaid string to the physical project directory.
    /// Swallows exceptions intentionally to avoid crashing background IDE analytical processes upon file locks.
    /// </summary>
    /// <param name="options">The analyzer option payload detailing project build configurations.</param>
    /// <param name="mermaidSyntax">The parsed string payload to write.</param>
    private static void WriteDiagramToDisk(AnalyzerConfigOptionsProvider options, string mermaidSyntax)
    {
#pragma warning disable RS1035 // Do not use APIs banned for analyzers
        try
        {
            if (options.GlobalOptions.TryGetValue("build_property.projectdir", out string projectDir) && !string.IsNullOrWhiteSpace(projectDir))
            {
                string filePath = Path.Combine(projectDir, "ComponentDiagram.mmd");

                bool shouldWrite = true;
                if (File.Exists(filePath))
                {
                    string existingContent = File.ReadAllText(filePath, Encoding.UTF8);
                    if (existingContent == mermaidSyntax)
                    {
                        shouldWrite = false;
                    }
                }

                if (shouldWrite)
                {
                    File.WriteAllText(filePath, mermaidSyntax, Encoding.UTF8);
                }
            }
        }
        catch (Exception)
        {
            // Silently swallow exceptions to ensure background IDE processes do not crash on file lock
        }
#pragma warning restore RS1035
    }

    /// <summary>
    /// Categorizes structural components into recognized domain-driven layer types based on naming conventions.
    /// </summary>
    /// <param name="symbol">The named type symbol provided by the compiler.</param>
    /// <returns>The resolved <see cref="ArtifactType"/>.</returns>
    private static ArtifactType ClassifyArtifact(INamedTypeSymbol symbol)
    {
        string name = symbol.Name;
        if (name.EndsWith("Repository", StringComparison.OrdinalIgnoreCase) || name.EndsWith("DbContext", StringComparison.OrdinalIgnoreCase)) return ArtifactType.Repository;
        if (name.EndsWith("Handler", StringComparison.OrdinalIgnoreCase) || name.EndsWith("UseCase", StringComparison.OrdinalIgnoreCase)) return ArtifactType.Handler;
        if (name.EndsWith("Entrypoint", StringComparison.OrdinalIgnoreCase)) return ArtifactType.Entrypoint;
        if (name.EndsWith("Event") || name.EndsWith("Command")) return ArtifactType.Message;
        return ArtifactType.Generic;
    }

    /// <summary>
    /// Resolves standard namespace strings into logical domain modules.
    /// </summary>
    /// <param name="ns">The fully qualified namespace string.</param>
    /// <returns>A concise module representation string.</returns>
    private static string ExtractModuleName(string ns)
    {
        if (string.IsNullOrWhiteSpace(ns)) return "Core";
        if (ns.Contains(".Shared") || !ns.Contains(".Modules.")) return "Core";
        int index = ns.IndexOf(".Modules.");
        if (index >= 0)
        {
            var parts = ns.Substring(index + 9).Split('.');
            if (parts.Length > 0) return parts[0];
        }
        return "Core";
    }

    /// <summary>
    /// Prepares C# identifiers for consumption by Mermaid graph syntax definitions.
    /// </summary>
    /// <param name="input">The string to sanitize.</param>
    /// <returns>The sanitized string safe for chart rendering.</returns>
    private static string Sanitize(string input) => input.Replace(".", "_").Replace("<", "_").Replace(">", "_").Replace("/", "_").Replace(" ", "_").Replace(":", "_");

    // =========================================================================
    // Lightweight Immutable Records for Incremental Caching
    // =========================================================================

    /// <summary>
    /// Determines the shape and styling behavior rendered for a given specific component node.
    /// </summary>
    internal enum ArtifactType { Generic, Handler, Repository, Entrypoint, Message }

    /// <summary>
    /// An immutable representation capturing syntax semantics for Types mapping into the diagram.
    /// </summary>
    internal record TypeMetadata(string FullName, string Name, string Module, ArtifactType ArtifactType, TypeKind TypeKind, ImmutableArray<string> Interfaces, ImmutableArray<string> Dependencies, ImmutableArray<string> DispatchedMessages, string HandledMessage);

    /// <summary>
    /// An immutable representation capturing routing capabilities from API invocation declarations.
    /// </summary>
    internal record EndpointMetadata(string Verb, string Route, string Id, ImmutableArray<string> Dependencies, ImmutableArray<string> DispatchedMessages);

    /// <summary>
    /// Structure linking routing attributes mapped cleanly into generic dependency edges.
    /// </summary>
    internal class EndpointInfo
    {
        public EndpointInfo(string verb, string route, string id)
        {
            Verb = verb;
            Route = route;
            Id = id;
        }
        public string Verb { get; }
        public string Route { get; }
        public string Id { get; }
    }

    /// <summary>
    /// Physical node payload carrying presentation details for the Mermaid pipeline.
    /// </summary>
    internal class TypeNode
    {
        public TypeNode(string fullName, string module, string name, ArtifactType artifactType)
        {
            FullName = fullName;
            Module = module;
            Name = name;
            ArtifactType = artifactType;
        }
        public string FullName { get; }
        public string Module { get; }
        public string Name { get; }
        public ArtifactType ArtifactType { get; }
    }

    /// <summary>
    /// Represents a structural connection flowing outward from a caller context to a referenced invocation.
    /// Equality is explicitly calculated natively to ensure duplicate links are ignored effectively by hash sets.
    /// </summary>
    internal class DependencyEdge : IEquatable<DependencyEdge>
    {
        public DependencyEdge(string sourceFullName, string targetFullName, string label, string edgeStyle)
        {
            SourceFullName = sourceFullName;
            TargetFullName = targetFullName;
            Label = label;
            EdgeStyle = edgeStyle;
        }
        public string SourceFullName { get; }
        public string TargetFullName { get; }
        public string Label { get; }
        public string EdgeStyle { get; }

        public bool Equals(DependencyEdge other) =>
            other != null && SourceFullName == other.SourceFullName && TargetFullName == other.TargetFullName && Label == other.Label && EdgeStyle == other.EdgeStyle;
        public override bool Equals(object obj) => Equals(obj as DependencyEdge);
        public override int GetHashCode() => (SourceFullName, TargetFullName, Label, EdgeStyle).GetHashCode();
    }
}