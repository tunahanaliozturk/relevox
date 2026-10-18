using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Capar.SourceGenerator;

/// <summary>
/// Points at MediatR code left after the package swap, so the migration code fix has one id to hang off.
/// </summary>
/// <remarks>
/// <para>
/// The compiler reports the same leftovers under several ids: CS0246 for a <c>using</c>, CS0103 for
/// <c>MediatR.Unit.Value</c> in an expression, CS0400 for <c>global::MediatR</c>, CS1061 for
/// <c>AddMediatR</c>. An IDE's "Fix all" covers only the id it was started on, so a fix hung off those would
/// take one run per id and another after each rebuild. CPR0007 is one id for all of it.
/// </para>
/// <para>
/// Nothing is reported while the MediatR package is still referenced: a project half way through a gradual
/// migration keeps its MediatR code until it is ready.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MediatRUsageAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Property naming what was found, read by the code fix.</summary>
    public const string KindProperty = "Kind";

    /// <summary>A name rooted at <c>MediatR</c>.</summary>
    public const string NameKind = "Name";

    /// <summary><c>MediatRServiceConfiguration</c>.</summary>
    public const string ConfigurationKind = "Configuration";

    /// <summary>An <c>AddMediatR</c> call.</summary>
    public const string RegistrationKind = "Registration";

    // MediatR namespaces whose types Çapar has under the same names below Capar.
    private static readonly HashSet<string> Mapped = new(System.StringComparer.Ordinal) { "Pipeline", "NotificationPublishers", "Entities" };

    // MediatR namespaces with no Çapar counterpart. Names in them are left for the compiler to point at.
    private static readonly HashSet<string> Unmapped = new(System.StringComparer.Ordinal) { "Registration", "Wrappers", "Licensing", "Internal" };

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Diagnostics.MediatRUsage];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            if (start.Compilation.GetTypeByMetadataName("Capar.IBaseRequest") is null
                || start.Compilation.GetTypeByMetadataName("MediatR.IMediator") is not null)
            {
                return;
            }

            start.RegisterSyntaxNodeAction(AnalyzeName, SyntaxKind.IdentifierName);
            start.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        });
    }

    private static void AnalyzeName(SyntaxNodeAnalysisContext context)
    {
        IdentifierNameSyntax name = (IdentifierNameSyntax)context.Node;
        string text = name.Identifier.ValueText;
        if (text is not ("MediatR" or "MediatRServiceConfiguration") || Resolves(context.SemanticModel, name, context))
        {
            return;
        }

        if (text == "MediatRServiceConfiguration")
        {
            Report(context, name, ConfigurationKind, text);
            return;
        }

        // MediatR has to be the root of the name: Company.MediatR.Foo is somebody else's namespace.
        switch (name.Parent)
        {
            case QualifiedNameSyntax qualified when qualified.Right == name:
            case MemberAccessExpressionSyntax access when access.Name == name:
            case AliasQualifiedNameSyntax alias when alias.Name == name && alias.Alias.Identifier.ValueText != "global":
                return;
            default:
                break;
        }

        string? second = SecondPart(name);
        if (second is not null && Unmapped.Contains(second))
        {
            return;
        }

        // In a plain using directive the second part is a namespace, which must be one Çapar has. An alias or a
        // using static names a type, which Çapar has under the same name.
        if (name.FirstAncestorOrSelf<UsingDirectiveSyntax>() is { Alias: null, StaticKeyword.RawKind: 0 }
            && second is not null
            && !Mapped.Contains(second))
        {
            return;
        }

        Report(context, name, NameKind, second is null ? text : $"{text}.{second}");
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
        SimpleNameSyntax? method = invocation.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name,
            IdentifierNameSyntax identifier => identifier,
            _ => null,
        };

        if (method is { Identifier.ValueText: "AddMediatR" }
            && context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is null)
        {
            Report(context, method, RegistrationKind, "AddMediatR");
        }
    }

    private static bool Resolves(SemanticModel model, IdentifierNameSyntax name, SyntaxNodeAnalysisContext context)
    {
        SymbolInfo info = model.GetSymbolInfo(name, context.CancellationToken);
        return info.Symbol is not null || info.CandidateSymbols.Length > 0;
    }

    /// <summary>The part after <c>MediatR</c> in <c>MediatR.Pipeline</c> or <c>MediatR.Unit.Value</c>.</summary>
    private static string? SecondPart(IdentifierNameSyntax name)
    {
        SyntaxNode node = name.Parent is AliasQualifiedNameSyntax alias ? alias : name;
        return node.Parent switch
        {
            QualifiedNameSyntax qualified when qualified.Left == node => qualified.Right.Identifier.ValueText,
            MemberAccessExpressionSyntax access when access.Expression == node => access.Name.Identifier.ValueText,
            _ => null,
        };
    }

    private static void Report(SyntaxNodeAnalysisContext context, SyntaxNode node, string kind, string shown) =>
        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.MediatRUsage,
            node.GetLocation(),
            ImmutableDictionary<string, string?>.Empty.Add(KindProperty, kind),
            shown));
}
