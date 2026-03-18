using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Modulith.ArchitectureOverview;

/// <summary>
/// Generates a complete C4 Level 3 Component Diagram using Mermaid syntax.
/// Resolves interfaces to their concrete implementations and treats messages (Events/Commands) 
/// as intermediary nodes. Excludes Shared and Api projects from module grouping.
/// Also outputs a standalone .mmd file to the target project directory.
/// </summary>
[Generator]
public class ComponentDiagramGenerator : IIncrementalGenerator
{
    /// <summary>
    /// Initializes the incremental generator pipelines and combines the compilation provider with analyzer options.
    /// </summary>
    /// <param name="context">The incremental generator context.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var provider = context.CompilationProvider.Combine(context.AnalyzerConfigOptionsProvider);
        context.RegisterSourceOutput(provider, static (spc, source) => Execute(spc, source.Left, source.Right));
    }

    /// <summary>
    /// Core execution pipeline for mapping components and writing the diagram outputs.
    /// </summary>
    /// <param name="context">The source production context.</param>
    /// <param name="compilation">The current compilation.</param>
    /// <param name="options">The analyzer configuration options containing MSBuild properties.</param>
    private static void Execute(SourceProductionContext context, Compilation compilation, AnalyzerConfigOptionsProvider options)
    {
        var typeNodes = new Dictionary<string, TypeNode>(StringComparer.OrdinalIgnoreCase);
        var dependencies = new HashSet<DependencyEdge>();
        var endpoints = new List<EndpointInfo>();

        var interfaceToImplementation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var messageHandlers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var handlerMessages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dispatchedMessages = new List<(string CallerId, string MessageType)>();

        WalkSymbolTree(compilation.Assembly.GlobalNamespace, compilation, typeNodes, dependencies, endpoints, interfaceToImplementation, messageHandlers, handlerMessages, dispatchedMessages);

        if (typeNodes.Count == 0 && endpoints.Count == 0) return;

        var resolvedDeps = ResolveConcreteDependencies(dependencies, interfaceToImplementation);

        BridgeDynamicDispatch(resolvedDeps, dispatchedMessages, messageHandlers, handlerMessages, interfaceToImplementation, typeNodes);

        string mermaidSyntax = GenerateMermaidSyntax(typeNodes.Values, resolvedDeps, endpoints);

        string sourceCode = GenerateCSharpWrapper(mermaidSyntax);
        context.AddSource("ComponentDiagram.g.cs", SourceText.From(sourceCode, Encoding.UTF8));

#pragma warning disable RS1035 // Do not use APIs banned for analyzers
        try
        {
            // Attempt to retrieve the ProjectDir property from MSBuild
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
    /// Generates the raw Mermaid syntax string.
    /// </summary>
    private static string GenerateMermaidSyntax(IEnumerable<TypeNode> nodes, HashSet<DependencyEdge> dependencies, List<EndpointInfo> endpoints)
    {
        var sb = new StringBuilder();
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
    /// Wraps the raw Mermaid syntax into a valid C# class structure.
    /// </summary>
    private static string GenerateCSharpWrapper(string mermaidSyntax)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("using System;");
        sb.AppendLine("namespace Modulith.Template.Pragmatic.ArchitectureOverview;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Contains the generated Mermaid diagram representing the system architecture.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class ComponentDiagram {");

        sb.AppendLine("    public const string MasterDiagram = @\"");

        string escapedSyntax = mermaidSyntax.Replace("\"", "\"\"");
        sb.Append(escapedSyntax);

        sb.AppendLine("\";");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Recursively walks the symbol tree to aggregate types and structural relationships.
    /// </summary>
    private static void WalkSymbolTree(INamespaceSymbol namespaceSymbol, Compilation compilation, Dictionary<string, TypeNode> typeNodes, HashSet<DependencyEdge> dependencies, List<EndpointInfo> endpoints, Dictionary<string, string> interfaceMap, Dictionary<string, HashSet<string>> messageHandlers, Dictionary<string, string> handlerMessages, List<(string, string)> dispatchedMessages)
    {
        foreach (var typeMember in namespaceSymbol.GetTypeMembers())
        {
            AnalyzeType(typeMember, compilation, typeNodes, dependencies, endpoints, interfaceMap, messageHandlers, handlerMessages, dispatchedMessages);
        }

        foreach (var childNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            WalkSymbolTree(childNamespace, compilation, typeNodes, dependencies, endpoints, interfaceMap, messageHandlers, handlerMessages, dispatchedMessages);
        }
    }

    /// <summary>
    /// Analyzes a specific type for dependencies, endpoint definitions, and implementation mapping.
    /// </summary>
    private static void AnalyzeType(INamedTypeSymbol typeSymbol, Compilation compilation, Dictionary<string, TypeNode> typeNodes, HashSet<DependencyEdge> dependencies, List<EndpointInfo> endpoints, Dictionary<string, string> interfaceMap, Dictionary<string, HashSet<string>> messageHandlers, Dictionary<string, string> handlerMessages, List<(string, string)> dispatchedMessages)
    {
        string typeFullName = typeSymbol.ToDisplayString();

        foreach (var method in typeSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            ScanForEndpoints(method, compilation, endpoints, dependencies, dispatchedMessages);
        }

        string moduleName = ExtractModuleName(typeSymbol.ContainingNamespace?.ToDisplayString() ?? string.Empty);
        ArtifactType artifactType = ClassifyArtifact(typeSymbol);

        if (typeSymbol.TypeKind != TypeKind.Interface)
        {
            if (!typeNodes.ContainsKey(typeFullName))
                typeNodes[typeFullName] = new TypeNode(typeFullName, moduleName, typeSymbol.Name, artifactType);
        }

        if (typeSymbol.TypeKind == TypeKind.Class && !typeSymbol.IsAbstract)
        {
            foreach (var iface in typeSymbol.AllInterfaces)
            {
                string ifaceName = iface.ToDisplayString();
                if (!interfaceMap.ContainsKey(ifaceName)) interfaceMap[ifaceName] = typeFullName;

                if ((iface.Name.Contains("Handler") || iface.Name.Contains("IEventHandler")) && iface.TypeArguments.Length > 0)
                {
                    string messageType = iface.TypeArguments[0].ToDisplayString();
                    if (!messageHandlers.ContainsKey(messageType)) messageHandlers[messageType] = new HashSet<string>();
                    messageHandlers[messageType].Add(typeFullName);
                    handlerMessages[typeFullName] = messageType;
                }
            }

            if (typeFullName.EndsWith("Handler") && !handlerMessages.ContainsKey(typeFullName))
            {
                var handleMethod = typeSymbol.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == "Handle" && m.Parameters.Length > 0);
                if (handleMethod != null)
                {
                    string messageType = handleMethod.Parameters[0].Type.ToDisplayString();
                    handlerMessages[typeFullName] = messageType;
                    if (!messageHandlers.ContainsKey(messageType)) messageHandlers[messageType] = new HashSet<string>();
                    messageHandlers[messageType].Add(typeFullName);
                }
            }
        }

        foreach (var constructor in typeSymbol.Constructors)
        {
            foreach (var parameter in constructor.Parameters)
            {
                string targetType = parameter.Type.ToDisplayString();
                if (!string.Equals(typeFullName, targetType, StringComparison.OrdinalIgnoreCase))
                {
                    dependencies.Add(new DependencyEdge(typeFullName, targetType, "Calls", "-->"));
                }
            }
        }

        foreach (var syntaxRef in typeSymbol.DeclaringSyntaxReferences)
        {
            var model = compilation.GetSemanticModel(syntaxRef.SyntaxTree);
            var invocations = syntaxRef.GetSyntax().DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                var methodSymbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                if (methodSymbol != null && methodSymbol.ContainingType != null)
                {
                    string targetType = methodSymbol.ContainingType.ToDisplayString();
                    if (!string.Equals(typeFullName, targetType, StringComparison.OrdinalIgnoreCase))
                    {
                        dependencies.Add(new DependencyEdge(typeFullName, targetType, "Calls", "-->"));
                    }
                }

                foreach (var arg in invocation.ArgumentList.Arguments)
                {
                    var argType = model.GetTypeInfo(arg.Expression).Type;
                    if (argType != null && (argType.Name.EndsWith("Event") || argType.Name.EndsWith("Command")))
                    {
                        dispatchedMessages.Add((typeFullName, argType.ToDisplayString()));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Locates web API endpoints mapped via methods matching MapGet, MapPost, etc.
    /// </summary>
    private static void ScanForEndpoints(IMethodSymbol method, Compilation compilation, List<EndpointInfo> endpoints, HashSet<DependencyEdge> dependencies, List<(string, string)> dispatchedMessages)
    {
        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            var model = compilation.GetSemanticModel(syntaxRef.SyntaxTree);
            var methodSyntax = syntaxRef.GetSyntax() as MethodDeclarationSyntax;
            if (methodSyntax == null) continue;

            var invocations = methodSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>();
            foreach (var invocation in invocations)
            {
                var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                if (symbol == null || !symbol.Name.StartsWith("Map")) continue;

                var routeArg = invocation.ArgumentList.Arguments.FirstOrDefault();
                if (routeArg == null) continue;

                var route = routeArg.Expression.ToString().Trim('"');
                var verb = symbol.Name.Replace("Map", "").ToUpper();
                var epId = $"EP_{Sanitize(verb + route)}";

                endpoints.Add(new EndpointInfo(verb, route, epId));

                var lambda = invocation.ArgumentList.Arguments
                    .Select(a => a.Expression)
                    .FirstOrDefault(e => e is LambdaExpressionSyntax) as LambdaExpressionSyntax;

                if (lambda != null)
                {
                    var lambdaModel = compilation.GetSemanticModel(lambda.SyntaxTree);
                    var bodyCalls = lambda.DescendantNodes().OfType<InvocationExpressionSyntax>();
                    foreach (var bc in bodyCalls)
                    {
                        var bcSymbol = lambdaModel.GetSymbolInfo(bc).Symbol as IMethodSymbol;
                        if (bcSymbol != null && bcSymbol.ContainingType != null)
                        {
                            dependencies.Add(new DependencyEdge(epId, bcSymbol.ContainingType.ToDisplayString(), "Calls", "==>"));
                        }

                        foreach (var arg in bc.ArgumentList.Arguments)
                        {
                            var argType = lambdaModel.GetTypeInfo(arg.Expression).Type;
                            if (argType != null && (argType.Name.EndsWith("Event") || argType.Name.EndsWith("Command")))
                            {
                                dispatchedMessages.Add((epId, argType.ToDisplayString()));
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Rewrites both the Source and Target of each dependency edge resolving interfaces to their concretions.
    /// </summary>
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
    /// Intercepts and rewrites edges so that all interactions with a handler are routed through the message node.
    /// </summary>
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
    /// Ascertains component responsibilities.
    /// </summary>
    private static ArtifactType ClassifyArtifact(INamedTypeSymbol symbol)
    {
        string name = symbol.Name;
        if (name.EndsWith("Repository") || name.EndsWith("DbContext")) return ArtifactType.Repository;
        if (name.EndsWith("Handler") || name.EndsWith("UseCase")) return ArtifactType.Handler;
        if (name.EndsWith("Entrypoint")) return ArtifactType.Entrypoint;
        if (name.EndsWith("Event") || name.EndsWith("Command")) return ArtifactType.Message;
        return ArtifactType.Generic;
    }

    /// <summary>
    /// Ascertains module containment, filtering out Shared and Api/Program-level namespaces.
    /// </summary>
    private static string ExtractModuleName(string ns)
    {
        if (string.IsNullOrWhiteSpace(ns)) return "Core";
        if (ns.Contains(".Shared") || ns.EndsWith(".Api") || !ns.Contains(".Modules.")) return "Core";
        int index = ns.IndexOf(".Modules.");
        if (index >= 0)
        {
            var parts = ns.Substring(index + 9).Split('.');
            if (parts.Length > 0) return parts[0];
        }
        return "Core";
    }

    /// <summary>
    /// Cleans strings mapped to identifiers required by Mermaid's syntax layout.
    /// </summary>
    private static string Sanitize(string input) => input.Replace(".", "_").Replace("<", "_").Replace(">", "_").Replace("/", "_").Replace(" ", "_").Replace(":", "_");

    internal enum ArtifactType { Generic, Handler, Repository, Entrypoint, Message }

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
}