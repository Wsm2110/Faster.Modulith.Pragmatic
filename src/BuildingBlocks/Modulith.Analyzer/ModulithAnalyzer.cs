using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Modulith.Analyzer;

/// <summary>
/// Enforces modular monolith architectural boundaries, strict entrypoint definitions, and strict event publishing rules.
/// Configuration strictly relies on the presence of an .editorconfig file.
/// Engineered for extreme performance and near-zero memory allocation during syntax traversal.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class ModuleBoundaryAnalyzer : DiagnosticAnalyzer
{
    internal const string BoundaryDiagnosticId = "MOD001";
    internal const string EntrypointDiagnosticId = "MOD002";
    internal const string ForeignInterfaceDiagnosticId = "MOD003";
    internal const string ServiceLocatorDiagnosticId = "MOD004";
    internal const string EventPublishDiagnosticId = "MOD005";
    internal const string EmptyHandlerDiagnosticId = "MOD007";

    private const string Category = "Architecture";
    private const string ContractsNamespace = "Contracts";
    private const string EntrypointSuffix = "Entrypoint";
    private const string PublishMethodPrefix = "Publish";
    private const string EventInterfaceName = "IEvent";
    private const string EventHandlerInterfaceName = "IEventHandler";
    private const string ModulesNamespaceSegment = "Modules";

    private static readonly DiagnosticDescriptor BoundaryRule = new DiagnosticDescriptor(BoundaryDiagnosticId, "Invalid Cross-Module Reference", "Module '{0}' cannot reference '{1}' from module '{2}'. Communication must route through .Contracts.", Category, DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor EntrypointRule = new DiagnosticDescriptor(EntrypointDiagnosticId, "Entrypoints Must Not Contain Public Fields Or Properties", "The entrypoint '{0}' contains a public {1} '{2}'. Entrypoints must only define behavioral usecase methods.", Category, DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor ForeignInterfaceRule = new DiagnosticDescriptor(ForeignInterfaceDiagnosticId, "Invalid Cross-Module Interface Usage", "Cannot depend on interface '{0}' from module '{1}' inside module '{2}'. Only the explicit I{1}Entrypoint is permitted.", Category, DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor ServiceLocatorRule = new DiagnosticDescriptor(ServiceLocatorDiagnosticId, "Service Locator Anti-Pattern Detected", "Constructors must not inject IServiceProvider. Dependencies must be declared explicitly.", Category, DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor EventPublishRule = new DiagnosticDescriptor(EventPublishDiagnosticId, "Invalid Cross-Module Event Publishing", "Module '{0}' cannot publish event '{1}' because it belongs to module '{2}'. Only the owning module can publish its own events.", Category, DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor EmptyHandlerRule = new DiagnosticDescriptor(EmptyHandlerDiagnosticId, "Unimplemented Handler Detected", "'{0}' is a handler with no implementation. Use the lightbulb to scaffold the boilerplate.", Category, DiagnosticSeverity.Warning, true);

    /// <summary>
    /// Gets the set of diagnostics supported by this analyzer.
    /// </summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(BoundaryRule, EntrypointRule, ForeignInterfaceRule, ServiceLocatorRule, EventPublishRule, EmptyHandlerRule);

    /// <summary>
    /// Initializes the analyzer and registers syntax and symbol actions.
    /// </summary>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(ctx =>
        {
            var exempt = GetExemptKeywords(ctx.Options, ctx.Node.SyntaxTree);
            AnalyzeForEmptyHandler(ctx, exempt);
        }, SyntaxKind.ClassDeclaration);

        context.RegisterSyntaxNodeAction(ctx =>
        {
            var layers = GetArchitecturalLayers(ctx.Options, ctx.Node.SyntaxTree);
            var exempt = GetExemptKeywords(ctx.Options, ctx.Node.SyntaxTree);
            AnalyzeNode(ctx, layers, exempt);
        }, SyntaxKind.IdentifierName, SyntaxKind.GenericName, SyntaxKind.QualifiedName, SyntaxKind.AliasQualifiedName);

        context.RegisterSyntaxNodeAction(ctx =>
        {
            var layers = GetArchitecturalLayers(ctx.Options, ctx.Node.SyntaxTree);
            var exempt = GetExemptKeywords(ctx.Options, ctx.Node.SyntaxTree);
            AnalyzeInvocation(ctx, layers, exempt);
        }, SyntaxKind.InvocationExpression);

        context.RegisterSymbolAction(ctx => AnalyzeNamedType(ctx, GetExemptKeywords(ctx.Options, ctx.Symbol.Locations[0]?.SourceTree)), SymbolKind.NamedType);

        context.RegisterSymbolAction(ctx =>
        {
            var serviceProviderType = ctx.Compilation.GetTypeByMetadataName("System.IServiceProvider");
            AnalyzeConstructor(ctx, GetExemptKeywords(ctx.Options, ctx.Symbol.Locations[0]?.SourceTree), serviceProviderType);
        }, SymbolKind.Method);
    }

    // =========================================================================
    // MOD007 — empty / unimplemented handler detection
    // =========================================================================

    public static string? DetectHandlerKind(string className, string filePath, string namespaceName)
    {
        if (className.EndsWith("CommandHandler", StringComparison.OrdinalIgnoreCase)) return "CommandHandler";
        if (className.EndsWith("EventHandler", StringComparison.OrdinalIgnoreCase)) return "EventHandler";

        if (className.EndsWith("Handler", StringComparison.OrdinalIgnoreCase))
        {
            if (namespaceName.IndexOf("CommandHandlers", StringComparison.OrdinalIgnoreCase) >= 0 || filePath.IndexOf("/CommandHandlers/", StringComparison.OrdinalIgnoreCase) >= 0 || filePath.IndexOf("\\CommandHandlers\\", StringComparison.OrdinalIgnoreCase) >= 0)
                return "CommandHandler";

            if (namespaceName.IndexOf("EventHandlers", StringComparison.OrdinalIgnoreCase) >= 0 || filePath.IndexOf("/EventHandlers/", StringComparison.OrdinalIgnoreCase) >= 0 || filePath.IndexOf("\\EventHandlers\\", StringComparison.OrdinalIgnoreCase) >= 0)
                return "EventHandler";
        }
        return null;
    }

    private static void AnalyzeForEmptyHandler(SyntaxNodeAnalysisContext context, string[] exemptKeywords)
    {
        if (IsContextExempt(context.Node, exemptKeywords)) return;

        var classDecl = (ClassDeclarationSyntax)context.Node;
        var className = classDecl.Identifier.Text;
        var filePath = context.Node.SyntaxTree.FilePath?.Replace('\\', '/') ?? string.Empty;
        var namespaceName = GetContainingNamespace(classDecl);

        var handlerKind = DetectHandlerKind(className, filePath, namespaceName);

        if (string.IsNullOrEmpty(handlerKind)) return;
        if (context.SemanticModel.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol classSymbol) return;

        bool hasHandleMethod = false;
        var members = classSymbol.GetMembers();

        foreach (var member in members)
        {
            if (member is IMethodSymbol method && method.Name.Equals("Handle", StringComparison.OrdinalIgnoreCase))
            {
                hasHandleMethod = true;
                break;
            }
        }

        if (!hasHandleMethod)
        {
            context.ReportDiagnostic(Diagnostic.Create(EmptyHandlerRule, classDecl.Identifier.GetLocation(), className));
        }
    }

    // =========================================================================
    // EXEMPTION HELPERS
    // =========================================================================

    private static bool IsContextExempt(SyntaxNode node, string[] exemptKeywords)
    {
        if (exemptKeywords.Length == 0) return false;

        var filePath = node.SyntaxTree.FilePath;
        if (!string.IsNullOrEmpty(filePath))
        {
            foreach (var keyword in exemptKeywords)
            {
                if (filePath.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
        }

        var ns = GetContainingNamespace(node);
        if (!string.IsNullOrEmpty(ns))
        {
            foreach (var keyword in exemptKeywords)
            {
                if (ns.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
        }

        return false;
    }

    private static string? GetExemptMatch(ISymbol? symbol, string[] exemptKeywords)
    {
        if (symbol == null || exemptKeywords.Length == 0) return null;

        symbol = symbol.OriginalDefinition;

        var nsString = symbol.ContainingNamespace?.ToDisplayString();
        if (!string.IsNullOrEmpty(nsString))
        {
            foreach (var keyword in exemptKeywords)
            {
                if (nsString!.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return keyword;
            }
        }

        string? assemblyName = symbol.ContainingAssembly?.Name;
        if (!string.IsNullOrEmpty(assemblyName))
        {
            foreach (var keyword in exemptKeywords)
            {
                if (assemblyName!.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return keyword;
            }
        }

        foreach (var location in symbol.Locations)
        {
            string? filePath = location.SourceTree?.FilePath;
            if (!string.IsNullOrEmpty(filePath))
            {
                foreach (var keyword in exemptKeywords)
                {
                    if (filePath.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return keyword;
                }
            }
        }

        return null;
    }

    internal static string GetContainingNamespace(SyntaxNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current is FileScopedNamespaceDeclarationSyntax fileScoped) return fileScoped.Name.ToString();
            if (current is NamespaceDeclarationSyntax blockScoped) return blockScoped.Name.ToString();
            current = current.Parent;
        }
        return string.Empty;
    }

    // =========================================================================
    // MOD001 / MOD002 / MOD003 / MOD004 / MOD005
    // =========================================================================

    private static void AnalyzeConstructor(SymbolAnalysisContext context, string[] exemptKeywords, INamedTypeSymbol? serviceProviderType)
    {
        if (serviceProviderType == null) return;
        var methodSymbol = (IMethodSymbol)context.Symbol;

        var location = methodSymbol.Locations.Length > 0 ? methodSymbol.Locations[0] : null;
        if (location?.SourceTree != null)
        {
            var filePath = location.SourceTree.FilePath;
            if (!string.IsNullOrEmpty(filePath))
            {
                foreach (var keyword in exemptKeywords)
                {
                    if (filePath.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return;
                }
            }
        }

        if (methodSymbol.MethodKind != MethodKind.Constructor || GetExemptMatch(methodSymbol, exemptKeywords) != null) return;

        foreach (var parameter in methodSymbol.Parameters)
        {
            if (SymbolEqualityComparer.Default.Equals(parameter.Type.OriginalDefinition, serviceProviderType))
            {
                var diagnosticLocation = parameter.Locations.Length > 0 ? parameter.Locations[0] : location;
                if (diagnosticLocation != null) context.ReportDiagnostic(Diagnostic.Create(ServiceLocatorRule, diagnosticLocation));
            }
        }
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context, string[] exemptKeywords)
    {
        var namedType = (INamedTypeSymbol)context.Symbol;
        if (GetExemptMatch(namedType, exemptKeywords) != null) return;

        if (namedType.Name.EndsWith(EntrypointSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var members = namedType.GetMembers();
            foreach (var member in members)
            {
                if (member.DeclaredAccessibility == Accessibility.Public && !member.IsImplicitlyDeclared)
                {
                    if (member is IPropertySymbol || member is IFieldSymbol)
                    {
                        var location = member.Locations.Length > 0 ? member.Locations[0] : null;
                        string memberType = member is IPropertySymbol ? "property" : "field";
                        context.ReportDiagnostic(Diagnostic.Create(EntrypointRule, location, namedType.Name, memberType, member.Name));
                    }
                }
            }
        }
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, HashSet<string> layers, string[] exemptKeywords)
    {
        if (layers.Count == 0 || context.ContainingSymbol == null) return;
        if (IsContextExempt(context.Node, exemptKeywords)) return;

        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol) return;
        if (!methodSymbol.Name.StartsWith(PublishMethodPrefix, StringComparison.OrdinalIgnoreCase)) return;

        var callerSymbol = context.ContainingSymbol;

        string? callerModule = ExtractModuleName(callerSymbol.ContainingNamespace, layers);
        if (string.IsNullOrEmpty(callerModule)) return;

        var reportedModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            var argTypeInfo = context.SemanticModel.GetTypeInfo(argument.Expression);
            AnalyzeEventPublishing(context, argTypeInfo.Type, callerModule!, argument.GetLocation(), layers, exemptKeywords, reportedModules);
        }

        foreach (var typeArg in methodSymbol.TypeArguments)
        {
            AnalyzeEventPublishing(context, typeArg, callerModule!, invocation.GetLocation(), layers, exemptKeywords, reportedModules);
        }
    }

    private static void AnalyzeEventPublishing(SyntaxNodeAnalysisContext context, ITypeSymbol? argType, string callerModule, Location location, HashSet<string> layers, string[] exemptKeywords, HashSet<string> reportedModules)
    {
        if (argType == null || GetExemptMatch(argType, exemptKeywords) != null) return;

        bool isEvent = argType.Name.Equals(EventInterfaceName, StringComparison.OrdinalIgnoreCase);
        if (!isEvent)
        {
            foreach (var i in argType.AllInterfaces)
            {
                if (i.Name.Equals(EventInterfaceName, StringComparison.OrdinalIgnoreCase))
                {
                    isEvent = true;
                    break;
                }
            }
        }

        if (!isEvent) return;

        string? eventModule = ExtractModuleName(argType.ContainingNamespace, layers);

        if (!string.IsNullOrEmpty(eventModule) && !string.Equals(eventModule, callerModule, StringComparison.OrdinalIgnoreCase))
        {
            if (reportedModules.Add(eventModule))
                context.ReportDiagnostic(Diagnostic.Create(EventPublishRule, location, callerModule, argType.Name, eventModule));
        }
    }

    private static void AnalyzeNode(SyntaxNodeAnalysisContext context, HashSet<string> layers, string[] exemptKeywords)
    {
        if (layers.Count == 0 || context.ContainingSymbol == null) return;
        if (IsContextExempt(context.Node, exemptKeywords)) return;

        if (context.Node.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == context.Node) return;
        if (context.Node.Parent is QualifiedNameSyntax || context.Node.Parent is AliasQualifiedNameSyntax) return;

        var symbolInfo = context.SemanticModel.GetSymbolInfo(context.Node);
        var targetSymbol = symbolInfo.Symbol as ITypeSymbol ?? symbolInfo.Symbol?.ContainingType;

        if (targetSymbol == null) return;
        if (GetExemptMatch(targetSymbol, exemptKeywords) != null) return;

        var callerSymbol = context.ContainingSymbol;

        string? targetModule = ExtractModuleName(targetSymbol.ContainingNamespace, layers);
        if (string.IsNullOrEmpty(targetModule)) return;

        string? callerModule = ExtractModuleName(callerSymbol.ContainingNamespace, layers);
        if (string.IsNullOrEmpty(callerModule) || string.Equals(callerModule, targetModule, StringComparison.OrdinalIgnoreCase)) return;

        var originalDef = targetSymbol.OriginalDefinition;

        bool isTargetInContracts = false;
        var currentNs = targetSymbol.ContainingNamespace;
        while (currentNs != null && !currentNs.IsGlobalNamespace)
        {
            if (currentNs.Name.Equals(ContractsNamespace, StringComparison.OrdinalIgnoreCase))
            {
                isTargetInContracts = true;
                break;
            }
            currentNs = currentNs.ContainingNamespace;
        }

        if (isTargetInContracts)
        {
            bool isInterface = targetSymbol.TypeKind == TypeKind.Interface;
            bool isEntrypoint = isInterface && targetSymbol.Name.Equals($"I{targetModule}{EntrypointSuffix}", StringComparison.OrdinalIgnoreCase);

            if (isInterface && !isEntrypoint)
            {
                context.ReportDiagnostic(Diagnostic.Create(ForeignInterfaceRule, context.Node.GetLocation(), targetSymbol.Name, targetModule, callerModule));
                return;
            }

            if (IsStateDeclaration(context.Node, out _, out bool isPublicProperty))
            {
                if (isEntrypoint && !isPublicProperty) return;
                context.ReportDiagnostic(Diagnostic.Create(BoundaryRule, context.Node.GetLocation(), callerModule, targetSymbol.Name, targetModule));
            }
            return;
        }

        if (targetSymbol.TypeKind == TypeKind.Interface)
            context.ReportDiagnostic(Diagnostic.Create(ForeignInterfaceRule, context.Node.GetLocation(), targetSymbol.Name, targetModule, callerModule));
        else
            context.ReportDiagnostic(Diagnostic.Create(BoundaryRule, context.Node.GetLocation(), callerModule, targetSymbol.Name, targetModule));
    }

    private static bool IsStateDeclaration(SyntaxNode node, out bool isConstructorParam, out bool isPublicProperty)
    {
        isConstructorParam = false;
        isPublicProperty = false;
        var current = node;

        while (current != null)
        {
            if (current is BlockSyntax || current is ArrowExpressionClauseSyntax) return false;

            if (current is PropertyDeclarationSyntax prop)
            {
                foreach (var modifier in prop.Modifiers)
                {
                    if (modifier.IsKind(SyntaxKind.PublicKeyword)) isPublicProperty = true;
                }
                return true;
            }

            if (current is FieldDeclarationSyntax || current is EventDeclarationSyntax || current is EventFieldDeclarationSyntax) return true;

            if (current is ParameterSyntax)
            {
                var owner = current.Parent?.Parent;
                if (owner is ConstructorDeclarationSyntax || owner is ClassDeclarationSyntax || owner is StructDeclarationSyntax)
                {
                    isConstructorParam = true;
                    return true;
                }
                if (owner is RecordDeclarationSyntax)
                {
                    isConstructorParam = true;
                    isPublicProperty = true;
                    return true;
                }
                return false;
            }

            if (current is MethodDeclarationSyntax || current is LocalFunctionStatementSyntax || current is DelegateDeclarationSyntax) return false;

            current = current.Parent;
        }
        return false;
    }

    private static string? ExtractModuleName(INamespaceSymbol? namespaceSymbol, HashSet<string> architecturalLayers)
    {
        var current = namespaceSymbol;

        while (current != null && !current.IsGlobalNamespace)
        {
            if (current.ContainingNamespace != null &&
                current.ContainingNamespace.Name.Equals(ModulesNamespaceSegment, StringComparison.OrdinalIgnoreCase))
            {
                return current.Name;
            }

            if (architecturalLayers.Contains(current.Name))
            {
                return current.ContainingNamespace?.Name;
            }

            current = current.ContainingNamespace;
        }

        return null;
    }

    private static HashSet<string> GetArchitecturalLayers(AnalyzerOptions options, SyntaxTree? tree)
    {
        var parsedLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tree != null)
        {
            var config = options.AnalyzerConfigOptionsProvider.GetOptions(tree);
            if (config.TryGetValue("modulith.architectural_layers", out string? layers) && !string.IsNullOrWhiteSpace(layers))
            {
                var parts = layers.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts) parsedLayers.Add(part.Trim());
            }
        }
        return parsedLayers;
    }

    private static string[] GetExemptKeywords(AnalyzerOptions options, SyntaxTree? tree)
    {
        if (tree != null)
        {
            var config = options.AnalyzerConfigOptionsProvider.GetOptions(tree);
            if (config.TryGetValue("modulith.exempt_keywords", out string? keywords) && !string.IsNullOrWhiteSpace(keywords))
            {
                var parts = keywords.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                var result = new string[parts.Length];
                for (int i = 0; i < parts.Length; i++) result[i] = parts[i].Trim();
                return result;
            }
        }
        return Array.Empty<string>();
    }
}