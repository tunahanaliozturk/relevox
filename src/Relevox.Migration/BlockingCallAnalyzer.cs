using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Relevox.Migration;

/// <summary>
/// Points at <c>.Result</c>, <c>.Wait()</c> and <c>GetAwaiter().GetResult()</c> inside handlers, behaviours and
/// processors.
/// </summary>
/// <remarks>
/// Blocking there holds a thread for the length of the awaited work, and under a synchronisation context, as in
/// classic ASP.NET or a UI, it can deadlock. Every method the pipeline calls returns a task, so awaiting is always
/// possible. General analyzers cover blocking calls everywhere; this one is about the pipeline only, so it can be
/// on by default without reporting the rest of an application. Info by default (ADR 0018).
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BlockingCallAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Blocking on a task inside a pipeline type.</summary>
    public static readonly DiagnosticDescriptor Rule = new(
        id: "RVX0010",
        title: "Pipeline code blocks on a task",
        messageFormat: "'{0}' blocks on a task with '{1}'; await it instead",
        category: "Relevox",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        helpLinkUri: "https://github.com/tunahanaliozturk/relevox/blob/main/docs/diagnostics.md#rvx0010",
        description: "Handlers, behaviours and processors all return tasks, so the task can be awaited. Blocking on it holds a thread for as long as the work takes and can deadlock under a synchronisation context.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            if (start.Compilation.GetTypeByMetadataName("Relevox.IBaseRequest") is null)
            {
                return;
            }

            ConcurrentDictionary<INamedTypeSymbol, bool> pipeline = new(SymbolEqualityComparer.Default);
            start.RegisterOperationAction(
                operation =>
                {
                    IPropertyReferenceOperation reference = (IPropertyReferenceOperation)operation.Operation;
                    if (reference.Property.Name == "Result" && IsTask(reference.Property.ContainingType))
                    {
                        Report(operation, pipeline, reference.Instance, ".Result", reference.Syntax);
                    }
                },
                OperationKind.PropertyReference);
            start.RegisterOperationAction(
                operation =>
                {
                    IInvocationOperation invocation = (IInvocationOperation)operation.Operation;
                    IMethodSymbol method = invocation.TargetMethod;
                    if (method.Name == "Wait" && !method.IsStatic && IsTask(method.ContainingType) && !IsPoll(invocation))
                    {
                        Report(operation, pipeline, invocation.Instance, ".Wait()", invocation.Syntax);
                    }
                    else if (method.Name == "GetResult" && IsAwaiter(method.ContainingType))
                    {
                        Report(operation, pipeline, Awaited(invocation.Instance), ".GetAwaiter().GetResult()", invocation.Syntax);
                    }
                },
                OperationKind.Invocation);
        });
    }

    private static void Report(OperationAnalysisContext context, ConcurrentDictionary<INamedTypeSymbol, bool> pipeline, IOperation? task, string form, SyntaxNode syntax)
    {
        INamedTypeSymbol? type = context.ContainingSymbol as INamedTypeSymbol ?? context.ContainingSymbol.ContainingType;
        if (type is null || !pipeline.GetOrAdd(type, IsPipelineType) || IsDone(task))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, NameLocation(syntax), type.Name, form));
    }

    /// <summary>
    /// Whether the task is plainly finished already: made with <c>Task.FromResult</c>, <c>Task.CompletedTask</c>,
    /// <c>ValueTask.FromResult</c> or a <c>ValueTask</c> constructed from a value, or the task a <c>ContinueWith</c>
    /// continuation is given. Reading those cannot block.
    /// </summary>
    private static bool IsDone(IOperation? task)
    {
        while (task is IConversionOperation { IsImplicit: true } conversion)
        {
            task = conversion.Operand;
        }

        return task switch
        {
            IInvocationOperation { TargetMethod: { Name: "FromResult" or "FromCanceled" or "FromException", IsStatic: true } method } => IsTask(method.ContainingType),
            IPropertyReferenceOperation { Property: { Name: "CompletedTask", IsStatic: true } property } => IsTask(property.ContainingType),
            IObjectCreationOperation { Constructor: { } constructor } => IsTask(constructor.ContainingType) && constructor.ContainingType.Name == "ValueTask"
                && constructor.Parameters.Length == 1 && !IsTask(constructor.Parameters[0].Type as INamedTypeSymbol),
            IParameterReferenceOperation { Parameter.ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } } parameter => IsContinuation(parameter),
            _ => false,
        };
    }

    /// <summary>Whether the parameter is the task a lambda passed to <c>ContinueWith</c> is given, read at any depth inside it.</summary>
    private static bool IsContinuation(IParameterReferenceOperation parameter)
    {
        for (IOperation? parent = parameter.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is IAnonymousFunctionOperation lambda && SymbolEqualityComparer.Default.Equals(lambda.Symbol, parameter.Parameter.ContainingSymbol))
            {
                return lambda.Parent is IDelegateCreationOperation { Parent: IArgumentOperation { Parent: IInvocationOperation { TargetMethod.Name: "ContinueWith" } } };
            }
        }

        return false;
    }

    /// <summary>
    /// <c>Wait(0)</c> or <c>Wait(TimeSpan.Zero)</c>, which only asks whether the task has finished. Awaiting
    /// instead would change what the code does.
    /// </summary>
    private static bool IsPoll(IInvocationOperation wait)
    {
        IOperation? timeout = wait.Arguments.FirstOrDefault(static argument => argument.Parameter?.Ordinal == 0)?.Value;
        return timeout switch
        {
            null => false,
            { Type.SpecialType: SpecialType.System_Int32, ConstantValue: { HasValue: true, Value: 0 } } => true,
            IFieldReferenceOperation { Field: { Name: "Zero", ContainingType.Name: "TimeSpan" } } => true,
            _ => false,
        };
    }

    /// <summary>The task an awaiter came from, for <c>task.GetAwaiter()</c> and <c>task.ConfigureAwait(x).GetAwaiter()</c>.</summary>
    private static IOperation? Awaited(IOperation? awaiter)
    {
        if (awaiter is not IInvocationOperation { TargetMethod.Name: "GetAwaiter" } getAwaiter)
        {
            return null;
        }

        return getAwaiter.Instance is IInvocationOperation { TargetMethod.Name: "ConfigureAwait" } configured
            ? configured.Instance
            : getAwaiter.Instance;
    }

    private static bool IsPipelineType(INamedTypeSymbol type) =>
        type.AllInterfaces.Any(static contract =>
            InRelevox(contract.ContainingNamespace)
            && contract.Name is "IRequestHandler" or "INotificationHandler" or "IStreamRequestHandler"
                or "IPipelineBehavior" or "IStreamPipelineBehavior"
                or "IRequestPreProcessor" or "IRequestPostProcessor"
                or "IRequestExceptionHandler" or "IRequestExceptionAction");

    private static bool InRelevox(INamespaceSymbol space) =>
        space is { Name: "Relevox", ContainingNamespace.IsGlobalNamespace: true }
        || space is { Name: "Pipeline", ContainingNamespace: { Name: "Relevox", ContainingNamespace.IsGlobalNamespace: true } };

    private static bool IsTask(INamedTypeSymbol? type) =>
        type is { Name: "Task" or "ValueTask" } && InTasks(type.ContainingNamespace);

    private static bool IsAwaiter(INamedTypeSymbol? type) =>
        type is not null
        && (type.Name is "TaskAwaiter" or "ValueTaskAwaiter" && type.ContainingNamespace is { Name: "CompilerServices", ContainingNamespace: { Name: "Runtime", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } }
            || type.Name is "ConfiguredTaskAwaiter" or "ConfiguredValueTaskAwaiter" && IsAwaiterHost(type.ContainingType));

    private static bool IsAwaiterHost(INamedTypeSymbol? type) =>
        type is { Name: "ConfiguredTaskAwaitable" or "ConfiguredValueTaskAwaitable" } && type.ContainingNamespace is { Name: "CompilerServices" };

    private static bool InTasks(INamespaceSymbol? space) =>
        space is { Name: "Tasks", ContainingNamespace: { Name: "Threading", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } };

    private static Location NameLocation(SyntaxNode syntax) => syntax switch
    {
        MemberAccessExpressionSyntax access => access.Name.GetLocation(),
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } => access.Name.GetLocation(),
        _ => syntax.GetLocation(),
    };
}
