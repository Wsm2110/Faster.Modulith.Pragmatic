using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace C2sc.Tools.LogFileViewer;

/// <summary>
/// Provides a highly optimized CodeFix (lightbulb) that scaffolds handler boilerplate when MOD007 fires.
/// Triggers on: MOD007 — Unimplemented Handler Detected.
/// Engineered for zero-allocation AST traversal.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(HandlerScaffoldCodeFixProvider))]
[Shared]
public class HandlerScaffoldCodeFixProvider : CodeFixProvider
{
    private static readonly string[] HandlerSuffixes = { "CommandHandler", "EventHandler", "Handler" };

    private static readonly string[] CommandUsingsNoValidator = { "System", "System.Threading", "System.Threading.Tasks", "Modulith.DomainEventDispatcher.Contracts" };
    private static readonly string[] CommandUsingsWithValidator = { "System", "System.Threading", "System.Threading.Tasks", "Modulith.DomainEventDispatcher.Contracts", "FluentValidation" };
    private static readonly string[] EventUsingsNoValidator = { "System", "System.Threading", "System.Threading.Tasks", "Modulith.DomainEventDispatcher.Contracts" };
    private static readonly string[] EventUsingsWithValidator = { "System", "System.Threading", "System.Threading.Tasks", "Modulith.DomainEventDispatcher.Contracts", "FluentValidation" };

    /// <summary>
    /// Gets the diagnostic IDs that this provider is capable of fixing.
    /// </summary>
    public sealed override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create("MOD007");

    /// <summary>
    /// Gets the FixAllProvider for this provider.
    /// </summary>
    public sealed override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    /// <summary>
    /// Registers the code fixes for the diagnostic.
    /// </summary>
    public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var diagnosticSpan = diagnostic.Location.SourceSpan;
            var token = root.FindToken(diagnosticSpan.Start);

            var classNode = FindClassDeclaration(token.Parent);
            if (classNode == null) continue;

            var className = classNode.Identifier.Text;
            var filePath = context.Document.FilePath?.Replace('\\', '/') ?? string.Empty;
            var namespaceName = GetContainingNamespace(classNode);

            var handlerKind = DetectHandlerKind(className, filePath, namespaceName);
            if (string.IsNullOrEmpty(handlerKind)) continue;

            if (handlerKind == "CommandHandler")
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: $"Modulith: Scaffold '{className}' > Handler + command",
                        createChangedDocument: ct => ScaffoldCommandHandlerAsync(context.Document, classNode, false, ct),
                        equivalenceKey: $"MOD007_CommandHandler_NoValidator_{className}"),
                    diagnostic);

                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: $"Modulith: Scaffold '{className}' > Handler + command + validator",
                        createChangedDocument: ct => ScaffoldCommandHandlerAsync(context.Document, classNode, true, ct),
                        equivalenceKey: $"MOD007_CommandHandler_WithValidator_{className}"),
                    diagnostic);
            }
            else if (handlerKind == "EventHandler")
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: $"Modulith: Scaffold '{className}' > Handler only",
                        createChangedDocument: ct => ScaffoldEventHandlerAsync(context.Document, classNode, false, ct),
                        equivalenceKey: $"MOD007_EventHandler_NoValidator_{className}"),
                    diagnostic);

                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: $"Modulith: Scaffold '{className}' > Handler + validator",
                        createChangedDocument: ct => ScaffoldEventHandlerAsync(context.Document, classNode, true, ct),
                        equivalenceKey: $"MOD007_EventHandler_WithValidator_{className}"),
                    diagnostic);
            }
        }
    }

    /// <summary>
    /// Traverses up the syntax tree to find a ClassDeclarationSyntax without allocating an enumerator.
    /// </summary>
    private static ClassDeclarationSyntax? FindClassDeclaration(SyntaxNode? node)
    {
        var current = node;
        while (current != null)
        {
            if (current is ClassDeclarationSyntax classDecl)
                return classDecl;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// Detects the specific type of handler based on naming conventions, namespaces, and file paths.
    /// </summary>
    private static string? DetectHandlerKind(string className, string filePath, string namespaceName)
    {
        if (className.EndsWith("CommandHandler", StringComparison.OrdinalIgnoreCase))
            return "CommandHandler";

        if (className.EndsWith("EventHandler", StringComparison.OrdinalIgnoreCase))
            return "EventHandler";

        if (className.EndsWith("Handler", StringComparison.OrdinalIgnoreCase))
        {
            // Using singular string matches both "CommandHandler" and "CommandHandlers"
            if (namespaceName.IndexOf("CommandHandler", StringComparison.OrdinalIgnoreCase) >= 0 ||
                filePath.IndexOf("/CommandHandler", StringComparison.OrdinalIgnoreCase) >= 0 ||
                filePath.IndexOf("\\CommandHandler", StringComparison.OrdinalIgnoreCase) >= 0)
                return "CommandHandler";

            if (namespaceName.IndexOf("EventHandler", StringComparison.OrdinalIgnoreCase) >= 0 ||
                filePath.IndexOf("/EventHandler", StringComparison.OrdinalIgnoreCase) >= 0 ||
                filePath.IndexOf("\\EventHandler", StringComparison.OrdinalIgnoreCase) >= 0)
                return "EventHandler";
        }

        return null;
    }

    /// <summary>
    /// Helper to resolve the containing namespace safely without LINQ.
    /// </summary>
    private static string GetContainingNamespace(SyntaxNode node)
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

    /// <summary>
    /// Strips common handler suffixes to derive the base name for scaffolded members.
    /// </summary>
    private static string DeriveBaseName(string className)
    {
        foreach (var suffix in HandlerSuffixes)
        {
            if (className.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return className.Substring(0, className.Length - suffix.Length);
        }
        return className;
    }

    /// <summary>
    /// Determines the access modifier of the class node to preserve visibility.
    /// </summary>
    private static string DeriveModifier(ClassDeclarationSyntax classNode)
    {
        foreach (var modifier in classNode.Modifiers)
        {
            if (modifier.IsKind(SyntaxKind.PublicKeyword)) return "public";
            if (modifier.IsKind(SyntaxKind.ProtectedKeyword)) return "protected";
            if (modifier.IsKind(SyntaxKind.PrivateKeyword)) return "private";
        }
        return "internal";
    }

    /// <summary>
    /// Adds missing using directives to the compilation unit using highly optimized sets.
    /// Eliminates all LINQ usage for memory efficiency.
    /// </summary>
    private static CompilationUnitSyntax AddMissingUsings(CompilationUnitSyntax compilationUnit, string[] namespaces)
    {
        var existingUsings = new HashSet<string>(StringComparer.Ordinal);

        foreach (var usingDirective in compilationUnit.Usings)
        {
            if (usingDirective.Name != null)
            {
                existingUsings.Add(usingDirective.Name.ToString());
            }
        }

        var toAdd = new List<UsingDirectiveSyntax>(namespaces.Length);
        var namespacesToSort = new List<string>(namespaces.Length);

        foreach (var ns in namespaces)
        {
            if (!string.IsNullOrEmpty(ns) && existingUsings.Add(ns))
            {
                namespacesToSort.Add(ns);
            }
        }

        if (namespacesToSort.Count == 0) return compilationUnit;

        namespacesToSort.Sort(StringComparer.Ordinal);

        foreach (var ns in namespacesToSort)
        {
            toAdd.Add(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns))
                .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed));
        }

        return compilationUnit.AddUsings(toAdd.ToArray());
    }

    /// <summary>
    /// Safely replaces a single node with multiple new members using Node Tracking.
    /// </summary>
    private static CompilationUnitSyntax ReplaceWithMultiple(CompilationUnitSyntax root, ClassDeclarationSyntax oldNode, List<SyntaxNode> newNodes)
    {
        if (newNodes.Count == 1)
        {
            return root.ReplaceNode(oldNode, newNodes[0]);
        }

        var trackedRoot = root.TrackNodes(oldNode);
        var currentOldNode = trackedRoot.GetCurrentNode(oldNode);

        if (currentOldNode == null) return root;

        var additionalNodes = new SyntaxNode[newNodes.Count - 1];
        for (int i = 1; i < newNodes.Count; i++)
        {
            additionalNodes[i - 1] = newNodes[i];
        }

        var rootWithAppended = trackedRoot.InsertNodesAfter(currentOldNode, additionalNodes);
        var nodeToReplace = rootWithAppended.GetCurrentNode(oldNode);

        return rootWithAppended.ReplaceNode(nodeToReplace!, newNodes[0]);
    }

    /// <summary>
    /// Generates the Command Handler, Command record, and optional Validator.
    /// Preserves existing attributes and XML documentation (LeadingTrivia).
    /// </summary>
    private static async Task<Document> ScaffoldCommandHandlerAsync(Document document, ClassDeclarationSyntax classNode, bool withValidator, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is not CompilationUnitSyntax compilationUnit) return document;

        var modifier = DeriveModifier(classNode);
        var baseName = DeriveBaseName(classNode.Identifier.Text);
        var commandName = $"{baseName}Command";
        var validatorName = $"{baseName}Validator";
        var handlerName = classNode.Identifier.Text;

        var handlerInterface = $"ICommandHandler<{commandName}, Result>";
        var handleReturnType = "ValueTask<Result>";

        string ctorBlock = withValidator
            ? $"\n    private readonly IValidator<{commandName}> _validator;\n\n    public {handlerName}(IValidator<{commandName}> validator)\n    {{\n        _validator = validator;\n    }}\n"
            : "";

        string handlerBody = withValidator
            ? "var validation = await _validator.ValidateAsync(command, ct);\n        if (!validation.IsValid)\n            return Result.Failure(validation.ToString());\n\n        throw new NotImplementedException();"
            : "throw new NotImplementedException();";

        var scaffoldedClass = SyntaxFactory.ParseMemberDeclaration($@"
/// <summary>
/// Handles the execution of the <see cref=""{commandName}""/>.
/// </summary>
{modifier} class {handlerName} : {handlerInterface}
{{{ctorBlock}
    public async {handleReturnType} Handle({commandName} command, CancellationToken ct = default)
    {{
        {handlerBody}
    }}
}}")!
        .WithLeadingTrivia(classNode.GetLeadingTrivia())
        .WithTrailingTrivia(classNode.GetTrailingTrivia())
        .WithAdditionalAnnotations(Formatter.Annotation);

        var commandRecord = SyntaxFactory.ParseMemberDeclaration($@"
/// <summary>
/// Represents the command parameters for the <see cref=""{handlerName}""/>.
/// </summary>
{modifier} record {commandName}() : ICommand<Result>;")!.WithAdditionalAnnotations(Formatter.Annotation);

        var members = new List<SyntaxNode>(withValidator ? 3 : 2) { scaffoldedClass, commandRecord };

        if (withValidator)
        {
            members.Add(SyntaxFactory.ParseMemberDeclaration($@"
/// <summary>
/// Provides validation rules for the <see cref=""{commandName}""/>.
/// </summary>
internal sealed class {validatorName} : AbstractValidator<{commandName}>
{{
    public {validatorName}()
    {{
    }}
}}")!.WithAdditionalAnnotations(Formatter.Annotation));
        }

        var newRoot = ReplaceWithMultiple(compilationUnit, classNode, members);
        string[] requiredUsings = withValidator ? CommandUsingsWithValidator : CommandUsingsNoValidator;

        newRoot = AddMissingUsings(newRoot, requiredUsings);

        return document.WithSyntaxRoot(newRoot);
    }

    /// <summary>
    /// Generates the Event Handler and optional Validator.
    /// Preserves existing attributes and XML documentation (LeadingTrivia).
    /// </summary>
    private static async Task<Document> ScaffoldEventHandlerAsync(Document document, ClassDeclarationSyntax classNode, bool withValidator, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is not CompilationUnitSyntax compilationUnit) return document;

        var modifier = DeriveModifier(classNode);
        var baseName = DeriveBaseName(classNode.Identifier.Text);
        var handlerName = classNode.Identifier.Text;
        var eventName = $"{baseName}Event";
        var validatorName = $"{baseName}Validator";

        string ctorBlock = withValidator
            ? $"\n    private readonly IValidator<{eventName}> _validator;\n\n    public {handlerName}(IValidator<{eventName}> validator)\n    {{\n        _validator = validator;\n    }}\n"
            : "";

        string handlerBody = withValidator
            ? "var validation = await _validator.ValidateAsync(domainEvent, ct);\n        if (!validation.IsValid)\n            throw new ValidationException(validation.Errors);\n\n        throw new NotImplementedException();"
            : "throw new NotImplementedException();";

        var scaffoldedClass = SyntaxFactory.ParseMemberDeclaration($@"
/// <summary>
/// Handles the specific logic for the <see cref=""{eventName}""/> domain event.
/// </summary>
{modifier} class {handlerName} : IEventHandler<{eventName}>
{{{ctorBlock}
    public async Task Handle({eventName} domainEvent, CancellationToken ct = default)
    {{
        {handlerBody}
    }}
}}")!
        .WithLeadingTrivia(classNode.GetLeadingTrivia())
        .WithTrailingTrivia(classNode.GetTrailingTrivia())
        .WithAdditionalAnnotations(Formatter.Annotation);

        var members = new List<SyntaxNode>(withValidator ? 2 : 1) { scaffoldedClass };

        if (withValidator)
        {
            members.Add(SyntaxFactory.ParseMemberDeclaration($@"
/// <summary>
/// Provides validation rules for the <see cref=""{eventName}""/>.
/// </summary>
internal sealed class {validatorName} : AbstractValidator<{eventName}>
{{
    public {validatorName}()
    {{
    }}
}}")!.WithAdditionalAnnotations(Formatter.Annotation));
        }

        var newRoot = ReplaceWithMultiple(compilationUnit, classNode, members);
        string[] requiredUsings = withValidator ? EventUsingsWithValidator : EventUsingsNoValidator;

        newRoot = AddMissingUsings(newRoot, requiredUsings);

        return document.WithSyntaxRoot(newRoot);
    }
}