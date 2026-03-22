using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Modulith.Analyzer

/// <summary>
/// Enforces modular monolith architectural boundaries, strict entrypoint definitions, DI constraints, and strict event publishing rules.
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
    internal const string UnregisteredDependencyDiagnosticId = "MOD006";
    internal const string EmptyHandlerDiagnosticId = "MOD007";

    private const string Category = "Architecture";
    private const string ContractsNamespace = "Contracts";
    private const string EntrypointSuffix = "Entrypoint";
    private const string PublishMethodPrefix = "Publish";
    private const string EventInterfaceName = "IEvent";
    private const string EventHandlerInterfaceName = "IEventHandler";
    private const string ModulesNamespaceSegment = "Modules";

    // Optimized static hashset for O(1) lookups
    private static readonly HashSet<string> DiRegistrationMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "AddScoped", "AddTransient", "AddSingleton",
        "TryAddScoped", "TryAddTransient", "TryAddSingleton",
        "TryAddEnumerable"
    };

    private static readonly DiagnosticDescriptor BoundaryRule = new DiagnosticDescriptor(BoundaryDiagnosticId, "Invalid Cross-Module Reference", "Module '{0}' cannot reference '{1}' from module '{2}'. Communication must route through .Contracts.", Category, DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor EntrypointRule = new DiagnosticDescriptor(EntrypointDiagnosticId, "Entrypoints Must Not Contain Public Fields Or Properties", "The entrypoint '{0}' contains a public {1} '{2}'. Entrypoints must only define behavioral usecase methods.", Category, DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor ForeignInterfaceRule = new DiagnosticDescriptor(ForeignInterfaceDiagnosticId, "Invalid Cross-Module Interface Usage", "Cannot depend on interface '{0}' from module '{1}' inside module '{2}'. Only the explicit I{1}Entrypoint is permitted.", Category, DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor ServiceLocatorRule = new DiagnosticDescriptor(ServiceLocatorDiagnosticId, "Service Locator Anti-Pattern Detected", "Constructors must not inject IServiceProvider. Dependencies must be declared explicitly.", Category, DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor EventPublishRule = new DiagnosticDescriptor(EventPublishDiagnosticId, "Invalid Cross-Module Event Publishing", "Module '{0}' cannot publish event '{1}' because it belongs to module '{2}'. Only the owning module can publish its own events.", Category, DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor UnregisteredDependencyRule = new DiagnosticDescriptor(UnregisteredDependencyDiagnosticId, "Unregistered Dependency Detected", "'{0}' is injected in '{1}' but no DI registration was found in the solution. Register it via services.Add*<{0}>() or services.Add*<{2}, {0}>().", Category, DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor EmptyHandlerRule = new DiagnosticDescriptor(EmptyHandlerDiagnosticId, "Unimplemented Handler Detected", "'{0}' is a handler with no implementation. Use the lightbulb to scaffold the boilerplate.", Category, DiagnosticSeverity.Warning, true);

    /// <summary>
    /// Gets the set of diagnostics supported by this analyzer.
    /// </summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(BoundaryRule, EntrypointRule, ForeignInterfaceRule, ServiceLocatorRule, EventPublishRule, UnregisteredDependencyRule, EmptyHandlerRule);

    /// <summary>
    /// Initializes the analyzer and registers syntax and symbol actions.
    /// </summary>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeForEmptyHandler, SyntaxKind.ClassDeclaration);

        context.RegisterCompilationStartAction(compilationContext =>
        {
            // Replaced HashSet with ConcurrentDictionary for thread safety during concurrent execution
            var registeredTypes = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

            compilationContext.RegisterSyntaxNodeAction(ctx => CollectDiRegistrations(ctx, registeredTypes), SyntaxKind.InvocationExpression);

            compilationContext.RegisterCompilationEndAction(endContext =>
            {
                foreach (var tree in endContext.Compilation.SyntaxTrees)
                {
                    var semanticModel = endContext.Compilation.GetSemanticModel(tree);
                    var root = tree.GetRoot();

                    // Zero-allocation syntax walker replaces DescendantNodes()
                    var walker = new ConstructorAnalyzerWalker(semanticModel, registeredTypes, endContext);
                    walker.Visit(root);
                }
            });
        });

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

    /// <summary>
    /// A zero-allocation syntax walker designed to analyze constructor declarations for unregistered dependencies.
    /// </summary>
    private class ConstructorAnalyzerWalker : CSharpSyntaxWalker
    {
        private readonly SemanticModel _semanticModel;
        private readonly ConcurrentDictionary<string, byte> _registeredTypes;
        private readonly CompilationAnalysisContext _context;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConstructorAnalyzerWalker"/> class.
        /// </summary>
        public ConstructorAnalyzerWalker(SemanticModel semanticModel, ConcurrentDictionary<string, byte> registeredTypes, CompilationAnalysisContext context)
        {
            _semanticModel = semanticModel;
            _registeredTypes = registeredTypes;
            _context = context;
        }

        /// <summary>
        /// Visits constructor declarations to evaluate dependency injection validity.
        /// </summary>
        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            if (_semanticModel.GetDeclaredSymbol(node) is not IMethodSymbol constructorSymbol) return;
            if (IsRegistrationContext(constructorSymbol)) return;

            foreach (var parameter in node.ParameterList.Parameters)
            {
                if (_semanticModel.GetDeclaredSymbol(parameter) is not IParameterSymbol paramSymbol) continue;

                var paramType = paramSymbol.Type;
                if (ShouldSkipType(paramType)) continue;

                var typeName = paramType.Name;
                var originalDefName = paramType.OriginalDefinition.Name;

                if (!_registeredTypes.ContainsKey(typeName) && !_registeredTypes.ContainsKey(originalDefName))
                {
                    string concreteHint = paramType.TypeKind == TypeKind.Interface ? typeName.TrimStart('I') : typeName;
                    _context.ReportDiagnostic(Diagnostic.Create(UnregisteredDependencyRule, parameter.Type.GetLocation(), typeName, constructorSymbol.ContainingType.Name, concreteHint));
                }
            }

            base.VisitConstructorDeclaration(node);
        }
    }

    // =========================================================================
    // MOD007 — empty / unimplemented handler detection
    // =========================================================================

    /// <summary>
    /// Detects the specific type of handler based on naming conventions, namespaces, and file paths.
    /// </summary>
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

    /// <summary>
    /// Analyzes class declarations to detect empty handlers.
    /// </summary>
    private static void AnalyzeForEmptyHandler(SyntaxNodeAnalysisContext context)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var className = classDecl.Identifier.Text;
        var filePath = context.Node.SyntaxTree.FilePath?.Replace('\\', '/') ?? string.Empty;
        var namespaceName = GetContainingNamespace(classDecl);

        var handlerKind = DetectHandlerKind(className, filePath, namespaceName);

        if (string.IsNullOrEmpty(handlerKind))
        {
            return;
        }

        if (context.SemanticModel.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol classSymbol) return;

        bool hasHandleMethod = false;
        var members = classSymbol.GetMembers();

        // Zero allocation loop
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

    /// <summary>
    /// Walks up the syntax tree to find the containing namespace name.
    /// </summary>
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
    // MOD006 helpers
    // =========================================================================

    private static void CollectDiRegistrations(SyntaxNodeAnalysisContext context, ConcurrentDictionary<string, byte> registeredTypes)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        string? methodName = invocation.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            IdentifierNameSyntax i => i.Identifier.Text,
            _ => null
        };

        if (methodName == null || !DiRegistrationMethods.Contains(methodName)) return;

        if (invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax g1 })
        {
            foreach (var typeArg in g1.TypeArgumentList.Arguments)
            {
                if (context.SemanticModel.GetTypeInfo(typeArg).Type is ITypeSymbol typeSymbol) registeredTypes.TryAdd(typeSymbol.Name, 0);
            }
            return;
        }

        if (invocation.Expression is GenericNameSyntax g2)
        {
            foreach (var typeArg in g2.TypeArgumentList.Arguments)
            {
                if (context.SemanticModel.GetTypeInfo(typeArg).Type is ITypeSymbol typeSymbol) registeredTypes.TryAdd(typeSymbol.Name, 0);
            }
            return;
        }

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (argument.Expression is TypeOfExpressionSyntax typeofExpr)
            {
                if (context.SemanticModel.GetTypeInfo(typeofExpr.Type).Type is ITypeSymbol typeSymbol) registeredTypes.TryAdd(typeSymbol.Name, 0);
            }
        }
    }

    private static bool IsRegistrationContext(IMethodSymbol constructor)
    {
        var type = constructor.ContainingType;
        if (type.IsStatic) return true;
        return type.Name.EndsWith("Extensions", StringComparison.OrdinalIgnoreCase) || type.Name.EndsWith("Startup", StringComparison.OrdinalIgnoreCase) || type.Name.Equals("Program", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSkipType(ITypeSymbol type)
    {
        if (type.IsValueType || type.SpecialType == SpecialType.System_String || type.TypeKind == TypeKind.TypeParameter || type.TypeKind == TypeKind.Array) return true;
        if (type.Name.StartsWith("ILogger", StringComparison.OrdinalIgnoreCase)) return true;

        // Zero allocation namespace checking
        var currentNs = type.ContainingNamespace;
        while (currentNs != null && !currentNs.IsGlobalNamespace)
        {
            if (currentNs.Name.Equals("System", StringComparison.OrdinalIgnoreCase) || currentNs.Name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
                return true;
            currentNs = currentNs.ContainingNamespace;
        }
        return false;
    }

    // =========================================================================
    // MOD001 / MOD002 / MOD003 / MOD004 / MOD005
    // =========================================================================

    private static void AnalyzeConstructor(SymbolAnalysisContext context, string[] exemptKeywords, INamedTypeSymbol? serviceProviderType)
    {
        if (serviceProviderType == null) return;
        var methodSymbol = (IMethodSymbol)context.Symbol;
        if (methodSymbol.MethodKind != MethodKind.Constructor || GetExemptMatch(methodSymbol, exemptKeywords) != null) return;

        foreach (var parameter in methodSymbol.Parameters)
        {
            if (SymbolEqualityComparer.Default.Equals(parameter.Type.OriginalDefinition, serviceProviderType))
            {
                var location = parameter.Locations.Length > 0 ? parameter.Locations[0] : methodSymbol.Locations.Length > 0 ? methodSymbol.Locations[0] : null;
                if (location != null) context.ReportDiagnostic(Diagnostic.Create(ServiceLocatorRule, location));
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

        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol) return;
        if (!methodSymbol.Name.StartsWith(PublishMethodPrefix, StringComparison.OrdinalIgnoreCase)) return;

        var callerSymbol = context.ContainingSymbol;
        string? callerExemptMatch = GetExemptMatch(callerSymbol, exemptKeywords);
        string? callerModule = callerExemptMatch ?? ExtractModuleName(callerSymbol.ContainingNamespace, layers);
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

        if (context.Node.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == context.Node) return;
        if (context.Node.Parent is QualifiedNameSyntax || context.Node.Parent is AliasQualifiedNameSyntax) return;

        var symbolInfo = context.SemanticModel.GetSymbolInfo(context.Node);
        var targetSymbol = symbolInfo.Symbol as ITypeSymbol ?? symbolInfo.Symbol?.ContainingType;

        if (targetSymbol == null) return;
        if (GetExemptMatch(targetSymbol, exemptKeywords) != null) return;

        var callerSymbol = context.ContainingSymbol;
        if (GetExemptMatch(callerSymbol, exemptKeywords) != null) return;

        string? targetModule = ExtractModuleName(targetSymbol.ContainingNamespace, layers);
        if (string.IsNullOrEmpty(targetModule)) return;

        string? callerModule = ExtractModuleName(callerSymbol.ContainingNamespace, layers);
        if (string.IsNullOrEmpty(callerModule) || string.Equals(callerModule, targetModule, StringComparison.OrdinalIgnoreCase)) return;

        var originalDef = targetSymbol.OriginalDefinition;
        if (originalDef.Name.Equals(EventInterfaceName, StringComparison.OrdinalIgnoreCase) ||
            originalDef.Name.Equals(EventHandlerInterfaceName, StringComparison.OrdinalIgnoreCase) ||
            originalDef.Name.Equals("IEventDispatcher", StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var i in targetSymbol.AllInterfaces)
        {
            if (i.Name.Equals(EventInterfaceName, StringComparison.OrdinalIgnoreCase)) return;
        }

        // Zero-allocation check for Contracts namespace
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

    // Zero-allocation namespace checking
    private static string? GetExemptMatch(ISymbol? symbol, string[] exemptKeywords)
    {
        if (symbol == null || exemptKeywords.Length == 0) return null;

        symbol = symbol.OriginalDefinition;

        var currentNs = symbol.ContainingNamespace;
        while (currentNs != null && !currentNs.IsGlobalNamespace)
        {
            foreach (var keyword in exemptKeywords)
            {
                if (currentNs.Name.Equals(keyword, StringComparison.OrdinalIgnoreCase)) return keyword;
            }
            currentNs = currentNs.ContainingNamespace;
        }

        string? assemblyName = symbol.ContainingAssembly?.Name;
        if (!string.IsNullOrEmpty(assemblyName))
        {
            foreach (var keyword in exemptKeywords)
            {
                if (assemblyName!.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return keyword;
            }
        }

        var location = symbol.Locations.Length > 0 ? symbol.Locations[0] : null;
        string? filePath = location?.SourceTree?.FilePath;
        if (!string.IsNullOrEmpty(filePath))
        {
            foreach (var keyword in exemptKeywords)
            {
                if (filePath!.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return keyword;
            }
        }

        return null;
    }

    // Zero allocation semantic hierarchy traversal for module extraction
    // Zero allocation semantic hierarchy traversal for module extraction
    private static string? ExtractModuleName(INamespaceSymbol? namespaceSymbol, HashSet<string> architecturalLayers)
    {
        var current = namespaceSymbol;

        while (current != null && !current.IsGlobalNamespace)
        {
            // Primary strategy: If the parent namespace is "Modules", then current is the root module name.
            if (current.ContainingNamespace != null &&
                current.ContainingNamespace.Name.Equals(ModulesNamespaceSegment, StringComparison.OrdinalIgnoreCase))
            {
                return current.Name;
            }

            // Fallback strategy: If we hit a known architectural layer (e.g., "Application"), the module name is its parent.
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