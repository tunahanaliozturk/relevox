using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Relevox.Migration;

/// <summary>
/// Points at Mediator code left after the package swap, so the migration code fix has one id to hang off.
/// </summary>
/// <remarks>
/// <para>
/// The same reasoning as <see cref="MediatRUsageAnalyzer"/> (RVX0007): the compiler reports the leftovers of a
/// removed package under several ids, an IDE's "Fix all" covers one id, so RVX0017 is one id for all of it.
/// </para>
/// <para>
/// Mediator is further from Relevox than MediatR is, so there is a kind more than there: besides the names and
/// the <c>AddMediator</c> call, a type is reported as a whole when its base list or its <c>Handle</c> method is in
/// Mediator's shape, so that it is moved completely or not at all (ADR 0024).
/// </para>
/// <para>
/// Nothing is reported while the Mediator package is still referenced: a project half way through a move keeps
/// its Mediator code until it is ready.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MediatorUsageAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Property naming what was found, read by the code fix.</summary>
    public const string KindProperty = "Kind";

    /// <summary>A name rooted at <c>Mediator</c>.</summary>
    public const string NameKind = "Name";

    /// <summary>A class, record, struct or interface in Mediator's shape.</summary>
    public const string TypeKind = "Type";

    /// <summary>An <c>AddMediator</c> call.</summary>
    public const string RegistrationKind = "Registration";

    /// <summary>
    /// Info, not a warning, for the reason RVX0007 is: the compiler already reports this code as an error once the
    /// Mediator package is gone, and this exists so that one "Fix all" can reach every kind of it.
    /// </summary>
    public static readonly DiagnosticDescriptor Rule = new(
        id: "RVX0017",
        title: "Mediator code to move to Relevox",
        messageFormat: "'{0}' is Mediator code; the fix 'Move from Mediator to Relevox' rewrites it",
        category: "Relevox",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        helpLinkUri: "https://github.com/tunahanaliozturk/relevox/blob/main/docs/diagnostics.md#rvx0017",
        description: "Reported only where Mediator is no longer referenced and the name does not resolve. The code fix rewrites the namespace, the handlers and messages in Mediator's shape and the AddMediator call; 'Fix all in solution' does the whole move.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            if (start.Compilation.GetTypeByMetadataName("Relevox.IBaseRequest") is null
                || start.Compilation.GetTypeByMetadataName("Mediator.IMediator") is not null)
            {
                return;
            }

            start.RegisterSyntaxNodeAction(AnalyzeName, SyntaxKind.IdentifierName);
            start.RegisterSyntaxNodeAction(AnalyzeType, SyntaxKind.ClassDeclaration, SyntaxKind.RecordDeclaration, SyntaxKind.StructDeclaration, SyntaxKind.RecordStructDeclaration, SyntaxKind.InterfaceDeclaration);
            start.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        });
    }

    private static void AnalyzeName(SyntaxNodeAnalysisContext context)
    {
        IdentifierNameSyntax name = (IdentifierNameSyntax)context.Node;
        if (name.Identifier.ValueText != "Mediator"
            || !MediatorSyntax.IsMovable(name)
            || MediatorSyntax.Resolves(context.SemanticModel, name, context.CancellationToken))
        {
            return;
        }

        string? second = MediatorSyntax.SecondPart(name);
        Report(context, name.GetLocation(), NameKind, second is null ? "Mediator" : $"Mediator.{second}");
    }

    private static void AnalyzeType(SyntaxNodeAnalysisContext context)
    {
        TypeDeclarationSyntax declaration = (TypeDeclarationSyntax)context.Node;
        if (MediatorSyntax.IsMigratable(declaration, context.SemanticModel, context.CancellationToken))
        {
            Report(context, declaration.Identifier.GetLocation(), TypeKind, declaration.Identifier.ValueText);
        }
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

        if (method is { Identifier.ValueText: "AddMediator" }
            && context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is null)
        {
            Report(context, method.GetLocation(), RegistrationKind, "AddMediator");
        }
    }

    private static void Report(SyntaxNodeAnalysisContext context, Location location, string kind, string shown) =>
        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            location,
            ImmutableDictionary<string, string?>.Empty.Add(KindProperty, kind),
            shown));
}
