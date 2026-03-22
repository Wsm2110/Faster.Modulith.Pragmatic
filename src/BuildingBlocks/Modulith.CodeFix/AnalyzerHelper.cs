using System;
using System.Collections.Generic;
using System.Text;

namespace Modulith.CodeFix;

/// <summary>
/// Shared diagnostic rule IDs referenced by both the analyzer project
/// (Modulith.Analyzer) and the CodeFix provider project (Modulith.Analyzer.CodeFixes).
///
/// Both projects link this file directly via a shared file reference — no project
/// dependency is needed between them.
///
/// In each .csproj, add:
///   &lt;Compile Include="..\Modulith.Analyzer.Shared\DiagnosticIds.cs" /&gt;
/// </summary>
internal static class DiagnosticIds
{
    public const string Boundary = "MOD001";
    public const string Entrypoint = "MOD002";
    public const string ForeignInterface = "MOD003";
    public const string ServiceLocator = "MOD004";
    public const string EventPublish = "MOD005";
    public const string UnregisteredDependency = "MOD006";
    public const string EmptyHandler = "MOD007";
}

/// <summary>
/// Shared helper utilities referenced by both projects via file link.
/// Keeps logic that both the analyzer and CodeFix provider need in one place.
/// </summary>
internal static class AnalyzerHelpers
{
    /// <summary>
    /// Walks up the syntax tree to find the containing namespace name.
    /// Handles both file-scoped (namespace Foo;) and block-scoped (namespace Foo { }) declarations.
    /// </summary>
    public static string GetContainingNamespace(Microsoft.CodeAnalysis.SyntaxNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current is Microsoft.CodeAnalysis.CSharp.Syntax.FileScopedNamespaceDeclarationSyntax fileScoped)
                return fileScoped.Name.ToString();
            if (current is Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax blockScoped)
                return blockScoped.Name.ToString();
            current = current.Parent;
        }
        return string.Empty;
    }

    /// <summary>
    /// Determines whether a class name or namespace/folder signals a CommandHandler or EventHandler.
    /// Returns "CommandHandler", "EventHandler", or null.
    /// </summary>
    public static string? DetectHandlerKind(string className, string filePath, string namespaceName)
    {
        if (className.EndsWith("CommandHandler", System.StringComparison.OrdinalIgnoreCase))
            return "CommandHandler";

        if (className.EndsWith("EventHandler", System.StringComparison.OrdinalIgnoreCase))
            return "EventHandler";

        if (className.EndsWith("Handler", System.StringComparison.OrdinalIgnoreCase))
        {
            if (namespaceName.Contains("CommandHandlers", System.StringComparison.OrdinalIgnoreCase))
                return "CommandHandler";

            if (namespaceName.Contains("EventHandlers", System.StringComparison.OrdinalIgnoreCase))
                return "EventHandler";

            var normalizedPath = filePath.Replace('\\', '/');

            if (normalizedPath.Contains("/CommandHandlers/", System.StringComparison.OrdinalIgnoreCase))
                return "CommandHandler";

            if (normalizedPath.Contains("/EventHandlers/", System.StringComparison.OrdinalIgnoreCase))
                return "EventHandler";
        }

        return null;
    }
}