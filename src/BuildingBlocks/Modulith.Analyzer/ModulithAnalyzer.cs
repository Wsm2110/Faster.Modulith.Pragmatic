using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Modulith.Analyzer;

/// <summary>
/// Enforces modular monolith architectural boundaries, strict entrypoint definitions, DI constraints, and strict event publishing rules.
/// Configuration strictly relies on the presence of an .editorconfig file.
/// 
/// <para><b>The Baseline:</b></para>
/// <para>By default, modules are strictly isolated. Code inside one module is completely blind to code inside another. You cannot instantiate, reference, inject, or inherit any types (classes, records, interfaces) across module boundaries.</para>
/// 
/// <para><b>The Rules (Enforcements):</b></para>
/// <list type="bullet">
/// <item><description><b>MOD001 (Boundary Rule):</b> No Cross-Module State. You cannot store a foreign module's types as structural state (properties, fields, or primary constructor parameters).</description></item>
/// <item><description><b>MOD002 (Entrypoint Rule):</b> Stateless Entrypoints. Entrypoint interfaces must strictly define behavior (use cases). They are forbidden from exposing public properties or fields.</description></item>
/// <item><description><b>MOD003 (Foreign Interface Rule):</b> No Internal Interface Leaks. You cannot inject or depend on a foreign module's internal interfaces.</description></item>
/// <item><description><b>MOD004 (Service Locator Anti-Pattern):</b> Strict DI Required. You cannot inject IServiceProvider into a constructor; dependencies must be statically typed.</description></item>
/// <item><description><b>MOD005 (Event Publish Rule):</b> Module Ownership of Events. A module is only allowed to publish its own events.</description></item>
/// </list>
/// 
/// <para><b>The Exceptions (Allowed Communication Paths):</b></para>
/// <list type="bullet">
/// <item><description><b>Entrypoints (Synchronous Communication):</b> You are allowed to inject a foreign module's Entrypoint interface (e.g., IStorageEntrypoint) into your local module's constructors or private fields. They cannot be exposed as public properties.</description></item>
/// <item><description><b>Events (Asynchronous Communication):</b> Any module can reference an event type from any other module to subscribe to it, provided it implements IEvent.</description></item>
/// <item><description><b>Contracts / DTOs (Data Transfer):</b> You can reference Records, Classes, or DTOs from a foreign module's Contracts namespace in behavioral contexts (method bodies/parameters), but not as class-level state.</description></item>
/// <item><description><b>Explicit Configuration Exemptions (The Global Bypass):</b> Entire folders, namespaces, or specific files can bypass all boundary rules entirely if listed in the .editorconfig file under the modulith.exempt_keywords key.</description></item>
/// </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class ModuleBoundaryAnalyzer : DiagnosticAnalyzer
{
    private const string BoundaryDiagnosticId = "MOD001";
    private const string EntrypointDiagnosticId = "MOD002";
    private const string ForeignInterfaceDiagnosticId = "MOD003";
    private const string ServiceLocatorDiagnosticId = "MOD004";
    private const string EventPublishDiagnosticId = "MOD005";
    private const string Category = "Architecture";

    private const string ContractsNamespace = "Contracts";
    private const string EntrypointSuffix = "Entrypoint";
    private const string PublishMethodPrefix = "Publish";
    private const string EventInterfaceName = "IEvent";
    private const string EventHandlerInterfaceName = "IEventHandler";
    private const string ModulesNamespaceSegment = "Modules";

    private static readonly DiagnosticDescriptor BoundaryRule = new DiagnosticDescriptor(
        id: BoundaryDiagnosticId,
        title: "Invalid Cross-Module Reference",
        messageFormat: "Module '{0}' cannot reference '{1}' from module '{2}'. Communication must route through .Contracts.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Modules must remain physically isolated. Cross-module references are restricted to explicit entrypoints and DTOs.");

    private static readonly DiagnosticDescriptor EntrypointRule = new DiagnosticDescriptor(
        id: EntrypointDiagnosticId,
        title: "Entrypoints Must Not Contain Public Fields Or Properties",
        messageFormat: "The entrypoint '{0}' contains a public {1} '{2}'. Entrypoints must only define behavioral usecase methods.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Entrypoint interfaces must not expose state.");

    private static readonly DiagnosticDescriptor ForeignInterfaceRule = new DiagnosticDescriptor(
        id: ForeignInterfaceDiagnosticId,
        title: "Invalid Cross-Module Interface Usage",
        messageFormat: "Cannot depend on interface '{0}' from module '{1}' inside module '{2}'. Only the explicit I{1}Entrypoint is permitted.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Strictly bans the injection or declaration of internal module interfaces across boundaries.");

    private static readonly DiagnosticDescriptor ServiceLocatorRule = new DiagnosticDescriptor(
        id: ServiceLocatorDiagnosticId,
        title: "Service Locator Anti-Pattern Detected",
        messageFormat: "Constructors must not inject IServiceProvider. Dependencies must be declared explicitly.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Bypasses compile-time validation and violates explicit dependency injection principles.");

    private static readonly DiagnosticDescriptor EventPublishRule = new DiagnosticDescriptor(
        id: EventPublishDiagnosticId,
        title: "Invalid Cross-Module Event Publishing",
        messageFormat: "Module '{0}' cannot publish event '{1}' because it belongs to module '{2}'. Only the owning module can publish its own events.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An event can only be published by the module that defines and owns it.");

    /// <summary>
    /// Gets the supported diagnostics.
    /// </summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(BoundaryRule, EntrypointRule, ForeignInterfaceRule, ServiceLocatorRule, EventPublishRule);

    /// <summary>
    /// Initializes the analyzer and caches configurations at the start of the compilation phase.
    /// </summary>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // Register directly on the main context, not inside CompilationStartAction
        context.RegisterSyntaxNodeAction(ctx =>
        {
            var layers = GetArchitecturalLayers(ctx.Options, ctx.Node.SyntaxTree);
            var exempt = GetExemptKeywords(ctx.Options, ctx.Node.SyntaxTree);
            AnalyzeNode(ctx, layers, exempt);
        },
        SyntaxKind.IdentifierName,
        SyntaxKind.GenericName,
        SyntaxKind.QualifiedName,
        SyntaxKind.AliasQualifiedName);

        context.RegisterSyntaxNodeAction(ctx =>
        {
            var layers = GetArchitecturalLayers(ctx.Options, ctx.Node.SyntaxTree);
            var exempt = GetExemptKeywords(ctx.Options, ctx.Node.SyntaxTree);
            AnalyzeInvocation(ctx, layers, exempt);
        }, SyntaxKind.InvocationExpression);

        context.RegisterSymbolAction(ctx =>
        {
            var exempt = GetExemptKeywords(ctx.Options,
                ctx.Symbol.Locations.FirstOrDefault()?.SourceTree);
            AnalyzeNamedType(ctx, exempt);
        }, SymbolKind.NamedType);

        context.RegisterSymbolAction(ctx =>
        {
            var serviceProviderType = ctx.Compilation
                .GetTypeByMetadataName("System.IServiceProvider");
            var exempt = GetExemptKeywords(ctx.Options,
                ctx.Symbol.Locations.FirstOrDefault()?.SourceTree);
            AnalyzeConstructor(ctx, exempt, serviceProviderType);
        }, SymbolKind.Method);
    }
    /// <summary>
    /// Retrieves the first syntax tree from the compilation context.
    /// </summary>
    private static SyntaxTree? GetFirstSyntaxTree(Compilation compilation)
    {
        return compilation.SyntaxTrees.FirstOrDefault();
    }

    /// <summary>
    /// Parses the architectural layers configuration from the editorconfig.
    /// </summary>
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

    /// <summary>
    /// Parses the exempt keywords configuration from the editorconfig.
    /// </summary>
    private static string[] GetExemptKeywords(AnalyzerOptions options, SyntaxTree? tree)
    {
        if (tree != null)
        {
            var config = options.AnalyzerConfigOptionsProvider.GetOptions(tree);
            if (config.TryGetValue("modulith.exempt_keywords", out string? keywords) && !string.IsNullOrWhiteSpace(keywords))
            {
                return keywords.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(k => k.Trim())
                               .ToArray();
            }
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Analyzes constructors for potential service locator anti-patterns.
    /// </summary>
    private static void AnalyzeConstructor(SymbolAnalysisContext context, string[] exemptKeywords, INamedTypeSymbol? serviceProviderType)
    {
        if (serviceProviderType == null) return;

        var methodSymbol = (IMethodSymbol)context.Symbol;

        if (methodSymbol.MethodKind != MethodKind.Constructor || GetExemptMatch(methodSymbol, exemptKeywords) != null) return;

        foreach (var parameter in methodSymbol.Parameters)
        {
            if (SymbolEqualityComparer.Default.Equals(parameter.Type.OriginalDefinition, serviceProviderType))
            {
                var location = parameter.Locations.FirstOrDefault() ?? methodSymbol.Locations.FirstOrDefault();
                if (location != null) context.ReportDiagnostic(Diagnostic.Create(ServiceLocatorRule, location));
            }
        }
    }

    /// <summary>
    /// Enforces the strict rule that entrypoint interfaces must not contain state members.
    /// </summary>
    private static void AnalyzeNamedType(SymbolAnalysisContext context, string[] exemptKeywords)
    {
        var namedType = (INamedTypeSymbol)context.Symbol;

        if (GetExemptMatch(namedType, exemptKeywords) != null) return;

        if (namedType.Name.EndsWith(EntrypointSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var publicStateMembers = namedType.GetMembers()
                .Where(m => m.DeclaredAccessibility == Accessibility.Public && !m.IsImplicitlyDeclared && (m is IPropertySymbol || m is IFieldSymbol));

            foreach (var member in publicStateMembers)
            {
                var location = member.Locations.FirstOrDefault();
                string memberType = member is IPropertySymbol ? "property" : "field";
                context.ReportDiagnostic(Diagnostic.Create(EntrypointRule, location, namedType.Name, memberType, member.Name));
            }
        }
    }

    /// <summary>
    /// Analyzes invocations to enforce cross-module event publishing boundaries.
    /// </summary>
    /// <summary>
    /// Analyzes invocations to enforce cross-module event publishing boundaries.
    /// </summary>
    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, HashSet<string> layers, string[] exemptKeywords)
    {
        if (layers.Count == 0 || context.ContainingSymbol == null) return;

        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol) return;

        if (!methodSymbol.Name.StartsWith(PublishMethodPrefix, StringComparison.OrdinalIgnoreCase)) return;

        var callerSymbol = context.ContainingSymbol;
        string? callerExemptMatch = GetExemptMatch(callerSymbol, exemptKeywords);
        string? callerModule = callerExemptMatch ?? ExtractModuleName(callerSymbol.ContainingNamespace?.ToDisplayString(), layers);

        if (string.IsNullOrEmpty(callerModule)) return;

        // Track reported modules for this specific invocation to prevent duplicates
        var reportedModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Check Arguments
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            var argTypeInfo = context.SemanticModel.GetTypeInfo(argument.Expression);
            AnalyzeEventPublishing(context, argTypeInfo.Type, callerModule!, argument.GetLocation(), layers, exemptKeywords, reportedModules);
        }

        // 2. Check Explicit Type Arguments (e.g., .Publish<ForeignEvent>(...))
        foreach (var typeArg in methodSymbol.TypeArguments)
        {
            AnalyzeEventPublishing(context, typeArg, callerModule!, invocation.GetLocation(), layers, exemptKeywords, reportedModules);
        }
    }

    /// <summary>
    /// Validates an individual event publishing attempt against module boundaries.
    /// </summary>
    private static void AnalyzeEventPublishing(SyntaxNodeAnalysisContext context, ITypeSymbol? argType, string callerModule, Location location, HashSet<string> layers, string[] exemptKeywords, HashSet<string> reportedModules)
    {
        if (argType == null || GetExemptMatch(argType, exemptKeywords) != null) return;

        bool isEvent = argType.Name == EventInterfaceName || argType.AllInterfaces.Any(i => i.Name == EventInterfaceName);
        if (!isEvent) return;

        string? eventModule = ExtractModuleName(argType.ContainingNamespace?.ToDisplayString(), layers);

        if (!string.IsNullOrEmpty(eventModule) && !string.Equals(eventModule, callerModule, StringComparison.OrdinalIgnoreCase))
        {
            // Only report if we haven't already flagged this module for this invocation
            if (reportedModules.Add(eventModule))
            {
                context.ReportDiagnostic(Diagnostic.Create(EventPublishRule, location, callerModule, argType.Name, eventModule));
            }
        }
    }
    /// <summary>
    /// Validates an individual event publishing attempt against module boundaries.
    /// </summary>
    private static void AnalyzeEventPublishing(SyntaxNodeAnalysisContext context, ITypeSymbol? argType, string callerModule, Location location, HashSet<string> layers, string[] exemptKeywords)
    {
        if (argType == null || GetExemptMatch(argType, exemptKeywords) != null) return;

        bool isEvent = argType.Name == EventInterfaceName || argType.AllInterfaces.Any(i => i.Name == EventInterfaceName);
        if (!isEvent) return;

        string? eventModule = ExtractModuleName(argType.ContainingNamespace?.ToDisplayString(), layers);

        if (!string.IsNullOrEmpty(eventModule) && !string.Equals(eventModule, callerModule, StringComparison.OrdinalIgnoreCase))
        {
            context.ReportDiagnostic(Diagnostic.Create(EventPublishRule, location, callerModule, argType.Name, eventModule));
        }
    }

    /// <summary>
    /// Analyzes node declarations for cross-module boundary violations and state leaks.
    /// </summary>
    /// <summary>
    /// Analyzes node declarations for cross-module boundary violations and state leaks.
    /// </summary>
    private static void AnalyzeNode(SyntaxNodeAnalysisContext context, HashSet<string> layers, string[] exemptKeywords)
    {
        if (layers.Count == 0 || context.ContainingSymbol == null) return;

        // NEW: Deduplication logic
        // If this is a member access (like .GetKey()), ignore the member name.
        // We only care about the base expression (StorageFactory).
        if (context.Node.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == context.Node)
        {
            return;
        }

        // Existing QualifiedName check
        if (context.Node.Parent is QualifiedNameSyntax || context.Node.Parent is AliasQualifiedNameSyntax)
        {
            return;
        }

        var symbolInfo = context.SemanticModel.GetSymbolInfo(context.Node);
        var targetSymbol = symbolInfo.Symbol as ITypeSymbol ?? symbolInfo.Symbol?.ContainingType;

        if (targetSymbol == null) return;

        if (GetExemptMatch(targetSymbol, exemptKeywords) != null) return;

        var callerSymbol = context.ContainingSymbol;
        if (GetExemptMatch(callerSymbol, exemptKeywords) != null) return;

        string? targetNamespace = targetSymbol.ContainingNamespace?.ToDisplayString();
        string? targetModule = ExtractModuleName(targetNamespace, layers);

        if (string.IsNullOrEmpty(targetModule)) return;

        string? callerNamespace = callerSymbol.ContainingNamespace?.ToDisplayString();
        string? callerModule = ExtractModuleName(callerNamespace, layers);

        if (string.IsNullOrEmpty(callerModule) || string.Equals(callerModule, targetModule, StringComparison.OrdinalIgnoreCase)) return;

        var originalDef = targetSymbol.OriginalDefinition;
        if (originalDef.Name == EventInterfaceName ||
            originalDef.Name == EventHandlerInterfaceName ||
            originalDef.Name == "IEventDispatcher" ||
            targetSymbol.AllInterfaces.Any(i => i.Name == EventInterfaceName))
        {
            return;
        }

        var targetNamespaceSegments = targetNamespace!.Split('.');
        bool isTargetInContracts = targetNamespaceSegments.Contains(ContractsNamespace, StringComparer.OrdinalIgnoreCase);

        if (isTargetInContracts)
        {
            bool isInterface = targetSymbol.TypeKind == TypeKind.Interface;
            bool isEntrypoint = isInterface && targetSymbol.Name.Equals($"I{targetModule}{EntrypointSuffix}", StringComparison.OrdinalIgnoreCase);

            if (isInterface && !isEntrypoint)
            {
                context.ReportDiagnostic(Diagnostic.Create(ForeignInterfaceRule, context.Node.GetLocation(), targetSymbol.Name, targetModule, callerModule));
                return;
            }

            if (IsStateDeclaration(context.Node, out bool isConstructorParam, out bool isPublicProperty))
            {
                if (isEntrypoint && !isPublicProperty) return;
                context.ReportDiagnostic(Diagnostic.Create(BoundaryRule, context.Node.GetLocation(), callerModule, targetSymbol.Name, targetModule));
            }
            return;
        }

        if (targetSymbol.TypeKind == TypeKind.Interface)
        {
            context.ReportDiagnostic(Diagnostic.Create(ForeignInterfaceRule, context.Node.GetLocation(), targetSymbol.Name, targetModule, callerModule));
        }
        else
        {
            context.ReportDiagnostic(Diagnostic.Create(BoundaryRule, context.Node.GetLocation(), callerModule, targetSymbol.Name, targetModule));
        }
    }

    /// <summary>
    /// Determines if the current syntax node represents a variable being stored as state.
    /// Also evaluates whether that state is exposed publicly (e.g., a public property or a Record primary constructor).
    /// </summary>
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
                isPublicProperty = prop.Modifiers.Any(SyntaxKind.PublicKeyword);
                return true;
            }

            if (current is FieldDeclarationSyntax || current is EventDeclarationSyntax || current is EventFieldDeclarationSyntax)
                return true;

            if (current is ParameterSyntax)
            {
                var parentList = current.Parent as ParameterListSyntax;
                var owner = parentList?.Parent;

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

            if (current is MethodDeclarationSyntax || current is LocalFunctionStatementSyntax || current is DelegateDeclarationSyntax)
                return false;

            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// Analyzes the origin of a symbol to determine if it matches a dynamic exempt keyword strictly using folder paths, namespaces, or assembly segments.
    /// </summary>
    private static string? GetExemptMatch(ISymbol? symbol, string[] exemptKeywords)
    {
        if (symbol == null || exemptKeywords.Length == 0) return null;

        symbol = symbol.OriginalDefinition;

        string? namespaceName = symbol.ContainingNamespace?.ToDisplayString();
        if (!string.IsNullOrEmpty(namespaceName))
        {
            var segments = namespaceName.Split('.');
            foreach (var keyword in exemptKeywords)
            {
                if (segments.Contains(keyword, StringComparer.OrdinalIgnoreCase)) return keyword;
            }
        }

        string? assemblyName = symbol.ContainingAssembly?.Name;
        if (!string.IsNullOrEmpty(assemblyName))
        {
            var segments = assemblyName.Split('.');
            foreach (var keyword in exemptKeywords)
            {
                if (segments.Contains(keyword, StringComparer.OrdinalIgnoreCase)) return keyword;
            }
        }

        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        string? filePath = location?.SourceTree?.FilePath;
        if (!string.IsNullOrEmpty(filePath))
        {
            var pathSegments = filePath.Replace('\\', '/').Split('/');
            foreach (var keyword in exemptKeywords)
            {
                if (pathSegments.Contains(keyword, StringComparer.OrdinalIgnoreCase)) return keyword;
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the logical module name by securely tokenizing the namespace string. 
    /// </summary>
    private static string? ExtractModuleName(string? namespaceName, HashSet<string> architecturalLayers)
    {
        if (string.IsNullOrWhiteSpace(namespaceName) || namespaceName == "<global namespace>") return null;

        var segments = namespaceName.Split('.');

        int moduleIndex = Array.FindIndex(segments, s => s.Equals(ModulesNamespaceSegment, StringComparison.OrdinalIgnoreCase));
        if (moduleIndex >= 0 && moduleIndex + 1 < segments.Length) return segments[moduleIndex + 1];

        for (int i = 1; i < segments.Length; i++)
        {
            if (architecturalLayers.Contains(segments[i])) return segments[i - 1];
        }

        return null;
    }
}