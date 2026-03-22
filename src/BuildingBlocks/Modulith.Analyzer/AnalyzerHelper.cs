using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Text;

namespace Modulith.Analyzer;

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
    /// Detects if a class declaration is intended to be a Command or Event handler based on naming and location.
    /// </summary>
    /// <param name="className">The name of the class.</param>
    /// <param name="filePath">The path of the file containing the class.</param>
    /// <param name="namespaceName">The containing namespace.</param>
    /// <returns>A string indicating the handler kind, or null if not a handler.</returns>
    public static string? DetectHandlerKind(string className, string filePath, string namespaceName)
    {
        if (className.EndsWith("CommandHandler", StringComparison.OrdinalIgnoreCase))
            return "CommandHandler";

        if (className.EndsWith("EventHandler", StringComparison.OrdinalIgnoreCase))
            return "EventHandler";

        if (className.EndsWith("Handler", StringComparison.OrdinalIgnoreCase))
        {
            if (namespaceName.Contains("CommandHandlers") || filePath.Contains("/CommandHandlers/"))
                return "CommandHandler";

            if (namespaceName.Contains("EventHandlers") || filePath.Contains("/EventHandlers/"))
                return "EventHandler";
        }

        return null;
    }

    /// <summary>
    /// Retrieves the containing namespace of a syntax node.
    /// </summary>
    /// <param name="node">The syntax node to inspect.</param>
    /// <returns>The fully qualified namespace string.</returns>
    public static string GetContainingNamespace(SyntaxNode node)
    {
        var namespaceDeclaration = node.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        return namespaceDeclaration?.Name.ToString() ?? string.Empty;
    }
}