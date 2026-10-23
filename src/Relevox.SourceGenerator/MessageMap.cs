using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Relevox.SourceGenerator;

/// <summary>Which list of <c>RelevoxOptions</c> a registration read from an <c>AddRelevox</c> lambda goes into.</summary>
internal enum OptionKind
{
    Behavior,
    StreamBehavior,
    PreProcessor,
    PostProcessor,
}

/// <summary>One registration read from an <c>AddRelevox</c> options lambda.</summary>
/// <param name="Kind">The options list it goes into.</param>
/// <param name="Service">The closed service, such as <c>global::Relevox.IPipelineBehavior&lt;global::Shop.Ping, string&gt;</c>; null when open.</param>
/// <param name="Implementation">The class; unbound, such as <c>global::Shop.Logging&lt;,&gt;</c>, when open.</param>
internal sealed record OptionRegistration(OptionKind Kind, string? Service, string Implementation);

/// <summary>What one <c>AddRelevox</c> call's options lambda registers, as far as the build can read it (ADR 0015).</summary>
/// <param name="Registrations">Behaviours and processors, in the order the lambda adds them.</param>
/// <param name="Unread">Calls whose types the build could not read, as written.</param>
/// <param name="AutoRegisterRequestProcessors">Whether the lambda sets <c>AutoRegisterRequestProcessors = true</c>.</param>
/// <param name="AddsPreProcessors">Whether it adds any pre-processor, read or not; only then do pre-processors run.</param>
/// <param name="AddsPostProcessors">The same for post-processors.</param>
/// <param name="Call">The <c>AddRelevox</c> call as written, when it passes options; null when it passes none.</param>
/// <param name="Foreign">Whether the call is another library's method of the same name, which the map ignores.</param>
internal sealed record PipelineOptions(
    EquatableArray<OptionRegistration> Registrations,
    EquatableArray<string> Unread,
    bool AutoRegisterRequestProcessors,
    bool AddsPreProcessors,
    bool AddsPostProcessors,
    string? Call,
    bool Foreign = false);

/// <summary>
/// Writes the message map: what this project's mediator dispatches and what runs around it, as JSON, carried out
/// of the build inside the compiled assembly (ADR 0014). <c>docs/message-map.md</c> describes every field.
/// </summary>
/// <remarks>
/// A generator may not write files, so the map goes into the compilation as a constant and an MSBuild task in
/// the package copies it to disk after the build. Built only when the project sets <c>RelevoxMessageMap</c>.
/// </remarks>
internal static class MessageMap
{
    /// <summary>The MSBuild property that turns the map on, as the compiler passes it to analyzers.</summary>
    public const string Switch = "build_property.RelevoxMessageMap";

    // The options methods, by name: the list they add to, whether they take an open generic, and the contract a
    // closed implementation is registered under, as its metadata name.
    private static readonly Dictionary<string, (OptionKind Kind, bool Open, string Contract)> Methods = new(System.StringComparer.Ordinal)
    {
        ["AddBehavior"] = (OptionKind.Behavior, false, "Relevox.IPipelineBehavior`2"),
        ["AddOpenBehavior"] = (OptionKind.Behavior, true, "Relevox.IPipelineBehavior`2"),
        ["AddOpenBehaviors"] = (OptionKind.Behavior, true, "Relevox.IPipelineBehavior`2"),
        ["AddStreamBehavior"] = (OptionKind.StreamBehavior, false, "Relevox.IStreamPipelineBehavior`2"),
        ["AddOpenStreamBehavior"] = (OptionKind.StreamBehavior, true, "Relevox.IStreamPipelineBehavior`2"),
        ["AddRequestPreProcessor"] = (OptionKind.PreProcessor, false, "Relevox.Pipeline.IRequestPreProcessor`1"),
        ["AddOpenRequestPreProcessor"] = (OptionKind.PreProcessor, true, "Relevox.Pipeline.IRequestPreProcessor`1"),
        ["AddRequestPostProcessor"] = (OptionKind.PostProcessor, false, "Relevox.Pipeline.IRequestPostProcessor`2"),
        ["AddOpenRequestPostProcessor"] = (OptionKind.PostProcessor, true, "Relevox.Pipeline.IRequestPostProcessor`2"),
    };

    /// <summary>
    /// Reads the options lambda of one <c>AddRelevox</c> call: the types named in <c>typeof</c> and in type
    /// arguments, which bind before the generator has written the method the lambda is passed to.
    /// </summary>
    /// <remarks>
    /// Anything else, such as a <c>Type</c> held in a variable or a method passed instead of a lambda, is listed as
    /// unread rather than guessed at. Behaviours registered with the container directly are not seen at all.
    /// </remarks>
    public static PipelineOptions Read(InvocationExpressionSyntax call, SemanticModel model, CancellationToken cancellationToken)
    {
        // This project's AddRelevox does not exist until the generator writes it, so a call that binds already is
        // to another library's method of the same name, or to a referenced project's generated one (RVX0006). That
        // configures the referenced project's mediator, unless the call is inside the namespace this project's own
        // AddRelevox then goes into, where it finds that one first (Planner.RegistrationNamespaceFor).
        if (model.GetSymbolInfo(call, cancellationToken).Symbol is IMethodSymbol bound
            && (bound.ContainingType.Name != "RelevoxServiceCollectionExtensions"
                || !Inside(model.GetEnclosingSymbol(call.SpanStart, cancellationToken)?.ContainingNamespace, Planner.ToNamespace(model.Compilation.AssemblyName ?? ""))))
        {
            return new PipelineOptions(EquatableArray<OptionRegistration>.Empty, EquatableArray<string>.Empty, false, false, false, null, Foreign: true);
        }

        List<OptionRegistration> registrations = [];
        List<string> unread = [];
        bool auto = false, pre = false, post = false;

        AnonymousFunctionExpressionSyntax? lambda = call.ArgumentList.Arguments
            .Select(static argument => argument.Expression)
            .OfType<AnonymousFunctionExpressionSyntax>()
            .FirstOrDefault();
        // The service collection is an argument too when the call is written as a static one, with or without the
        // class name; anything else is options.
        int passed = call.ArgumentList.Arguments.Count(argument => !IsServices(model.GetTypeInfo(argument.Expression, cancellationToken).Type));
        string? written = passed > 0 ? Text(call) : null;
        if (lambda is null)
        {
            if (written is not null)
            {
                unread.Add(written);
            }

            return new PipelineOptions(EquatableArray<OptionRegistration>.Empty, unread.ToEquatableArray(), false, false, false, written);
        }

        string[] parameters = lambda switch
        {
            SimpleLambdaExpressionSyntax simple => [simple.Parameter.Identifier.ValueText],
            ParenthesizedLambdaExpressionSyntax parenthesized => [.. parenthesized.ParameterList.Parameters.Select(static parameter => parameter.Identifier.ValueText)],
            AnonymousMethodExpressionSyntax { ParameterList: { } list } => [.. list.Parameters.Select(static parameter => parameter.Identifier.ValueText)],
            _ => [],
        };

        // Chained calls nest the first one innermost, so source order of the method names is the order they run in.
        // Nested lambdas and local functions are not walked into: they may run later or never.
        foreach (SyntaxNode node in lambda.Body
            .DescendantNodesAndSelf(static node => node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .OrderBy(static node => Anchor(node)))
        {
            if (node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
            {
                if (node.DescendantNodes().OfType<IdentifierNameSyntax>().Any(identifier => IsOptions(identifier, parameters)))
                {
                    unread.Add(Text(node));
                }

                continue;
            }

            // The options used other than through a member or as a call's argument, such as var pipeline = options,
            // can be registered through where the build does not follow.
            if (node is IdentifierNameSyntax identifier && IsOptions(identifier, parameters) && !Followed(identifier))
            {
                unread.Add(Text(identifier.FirstAncestorOrSelf<StatementSyntax>() ?? identifier.Parent!));
                continue;
            }

            if (node is AssignmentExpressionSyntax { Left: var left } assignment
                && Member(left) == "AutoRegisterRequestProcessors"
                && IsOptions(Owner(left), parameters))
            {
                // Only a plain = with a literal says what the value is; |=, &= and the like depend on what it was.
                if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    && (assignment.Right.IsKind(SyntaxKind.TrueLiteralExpression) || assignment.Right.IsKind(SyntaxKind.FalseLiteralExpression)))
                {
                    auto = assignment.Right.IsKind(SyntaxKind.TrueLiteralExpression);
                }
                else
                {
                    unread.Add(Text(assignment));
                }

                continue;
            }

            if (node is not InvocationExpressionSyntax invocation)
            {
                continue;
            }

            // A method of the same name on something else, such as helper.AddBehavior(...), is not an options call.
            if (MethodName(invocation) is not { } name || !Methods.TryGetValue(name.Identifier.ValueText, out var method)
                || !IsOptions(Receiver(invocation), parameters))
            {
                // A call handed the options, such as Configure(options), may register anything.
                if (Receives(invocation, parameters))
                {
                    unread.Add(Text(invocation));
                }

                continue;
            }

            // The options a call returns, kept as in var pipeline = options.AddBehavior(...), can be registered
            // through where the build does not follow.
            if (Kept(invocation))
            {
                unread.Add(Text((SyntaxNode?)invocation.FirstAncestorOrSelf<StatementSyntax>() ?? invocation));
            }

            // RelevoxOptions' own methods take only types and a lifetime. Any other argument picks an extension
            // method of the same name, which need not register anything.
            if (invocation.ArgumentList.Arguments.Any(argument => !IsTypes(argument.Expression)
                && model.GetTypeInfo(argument.Expression, cancellationToken).Type?.ToDisplayString() != "Microsoft.Extensions.DependencyInjection.ServiceLifetime"))
            {
                unread.Add(Text(invocation));
                continue;
            }

            pre |= method.Kind == OptionKind.PreProcessor;
            post |= method.Kind == OptionKind.PostProcessor;
            List<ITypeSymbol?> types = name is GenericNameSyntax generic
                ? [.. generic.TypeArgumentList.Arguments.Select(argument => Bind(argument, model, cancellationToken))]
                : [.. invocation.ArgumentList.Arguments
                    // A named implementationType goes after the service type whatever its position, as C# binds it.
                    .OrderBy(static argument => argument.NameColon?.Name.Identifier.ValueText == "implementationType" ? 1 : 0)
                    .Select(static argument => argument.Expression)
                    // The lifetime is the one argument that is not a type.
                    .Where(IsTypes)
                    .SelectMany(expression => TypesIn(expression, model, cancellationToken))];

            int before = registrations.Count;
            bool read = types.Count > 0 && types.All(static type => type is INamedTypeSymbol)
                && Add(registrations, method, [.. types.Cast<INamedTypeSymbol>()], name.Identifier.ValueText == "AddOpenBehaviors");
            if (!read)
            {
                registrations.RemoveRange(before, registrations.Count - before);
                unread.Add(Text(invocation));
            }
        }

        return new PipelineOptions(registrations.ToEquatableArray(), unread.ToEquatableArray(), auto, pre, post, written);
    }

    private static bool IsTypes(ExpressionSyntax expression) => expression is TypeOfExpressionSyntax or ArrayCreationExpressionSyntax
        or ImplicitArrayCreationExpressionSyntax or CollectionExpressionSyntax or BaseObjectCreationExpressionSyntax;

    // Whether what an options call returns, the options again, is used other than by the next call of a chain, as a
    // statement or as the options lambda's body.
    private static bool Kept(InvocationExpressionSyntax invocation)
    {
        SyntaxNode result = invocation;
        while (result.Parent is ConditionalAccessExpressionSyntax conditional && conditional.WhenNotNull == result)
        {
            result = conditional;
        }

        return result.Parent switch
        {
            ExpressionStatementSyntax or AnonymousFunctionExpressionSyntax => false,
            MemberAccessExpressionSyntax access => !(access.Expression == result && access.Parent is InvocationExpressionSyntax outer && outer.Expression == access),
            _ => true,
        };
    }

    // Whether the call is passed the options, or made on them (an extension method, or a change to one of the
    // lists such as options.BehaviorsToRegister).
    private static bool Receives(InvocationExpressionSyntax invocation, string[] parameters) =>
        invocation.ArgumentList.Arguments.Any(argument => IsOptions(argument.Expression, parameters))
        || Receiver(invocation) is { } receiver
            && (IsOptions(receiver, parameters)
                || receiver is MemberAccessExpressionSyntax { Expression: var list, Name.Identifier.ValueText: var member } && member.EndsWith("ToRegister", System.StringComparison.Ordinal) && IsOptions(list, parameters));

    // What a call is made on, after any chain of known options calls, which return the options.
    private static ExpressionSyntax? Receiver(InvocationExpressionSyntax invocation)
    {
        ExpressionSyntax? receiver = Owner(invocation.Expression);
        while (receiver is InvocationExpressionSyntax inner && Member(inner.Expression) is { } name && Methods.ContainsKey(name))
        {
            receiver = Owner(inner.Expression);
        }

        return receiver;
    }

    // What a member is used on: options in options.X and in options?.X.
    private static ExpressionSyntax? Owner(ExpressionSyntax member) => member switch
    {
        MemberAccessExpressionSyntax access => access.Expression,
        MemberBindingExpressionSyntax binding => binding.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>()?.Expression,
        _ => null,
    };

    private static string? Member(ExpressionSyntax member) => member switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => null,
    };

    private static bool IsServices(ITypeSymbol? type) =>
        type is not null && (IsServiceCollection(type) || type.AllInterfaces.Any(IsServiceCollection));

    private static bool IsServiceCollection(ITypeSymbol type) => type.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.IServiceCollection";

    private static bool Inside(INamespaceSymbol? space, string name) =>
        space?.ToDisplayString() is { } enclosing
        && (enclosing == name || enclosing.StartsWith(name + ".", System.StringComparison.Ordinal));

    private static bool IsOptions(ExpressionSyntax? expression, string[] parameters) =>
        expression is IdentifierNameSyntax { Identifier.ValueText: var identifier } name
        && parameters.Contains(identifier)
        // In x.options, x?.options and Call(options: x) the name is something else.
        && !(name.Parent is MemberAccessExpressionSyntax access && access.Name == name)
        && name.Parent is not (MemberBindingExpressionSyntax or NameColonSyntax);

    // Uses of the options this reader follows: options.Member, options?.Member, and options as a call's argument,
    // which Receives lists as unread.
    private static bool Followed(IdentifierNameSyntax options) => options.Parent switch
    {
        MemberAccessExpressionSyntax access => access.Expression == options,
        ConditionalAccessExpressionSyntax conditional => conditional.Expression == options,
        ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax } => true,
        _ => false,
    };

    private static int Anchor(SyntaxNode node) => node is InvocationExpressionSyntax invocation && MethodName(invocation) is { } name ? name.SpanStart : node.SpanStart;

    private static SimpleNameSyntax? MethodName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax access => access.Name,
        MemberBindingExpressionSyntax binding => binding.Name,
        _ => null,
    };

    private static bool Add(List<OptionRegistration> registrations, (OptionKind Kind, bool Open, string Contract) method, INamedTypeSymbol[] types, bool many)
    {
        if (method.Open)
        {
            if (!many && types.Length != 1)
            {
                return false;
            }

            foreach (INamedTypeSymbol type in types)
            {
                if (!type.IsUnboundGenericType)
                {
                    return false;
                }

                registrations.Add(new OptionRegistration(method.Kind, null, type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            }

            return true;
        }

        if (types.Any(static type => type.IsUnboundGenericType))
        {
            return false;
        }

        if (types.Length == 2)
        {
            registrations.Add(new OptionRegistration(method.Kind, Plain(types[0]), Plain(types[1])));
            return true;
        }

        // One class goes in under every closing of the contract it implements, as RelevoxOptions does at run time.
        INamedTypeSymbol[] contracts = [.. types[0].AllInterfaces.Where(contract => MetadataName(contract.OriginalDefinition) == method.Contract)];
        foreach (INamedTypeSymbol contract in contracts)
        {
            registrations.Add(new OptionRegistration(method.Kind, Plain(contract), Plain(types[0])));
        }

        return types.Length == 1 && contracts.Length > 0;
    }

    // typeof(X), or the typeof in each element of an array or collection, or new OpenBehavior(typeof(X)). A null
    // stands for anything the build cannot read.
    private static IEnumerable<ITypeSymbol?> TypesIn(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken)
    {
        IEnumerable<ExpressionSyntax> elements = expression switch
        {
            TypeOfExpressionSyntax => [expression],
            ArrayCreationExpressionSyntax array => array.Initializer?.Expressions ?? [],
            ImplicitArrayCreationExpressionSyntax array => array.Initializer.Expressions,
            CollectionExpressionSyntax collection => collection.Elements.Select(static element => element is ExpressionElementSyntax item ? item.Expression : null!),
            BaseObjectCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions,
            BaseObjectCreationExpressionSyntax creation => [creation],
            _ => [null!],
        };

        foreach (ExpressionSyntax? element in elements)
        {
            yield return element switch
            {
                TypeOfExpressionSyntax type => Bind(type.Type, model, cancellationToken),
                BaseObjectCreationExpressionSyntax { ArgumentList.Arguments: { Count: > 0 } arguments } when arguments[0].Expression is TypeOfExpressionSyntax type => Bind(type.Type, model, cancellationToken),
                _ => null,
            };
        }
    }

    // Speculatively, as a type: the lambda's parameter has no type until the generator writes AddRelevox, and some
    // compilers leave everything under an unbound call unbound.
    private static ITypeSymbol? Bind(TypeSyntax syntax, SemanticModel model, CancellationToken cancellationToken)
    {
        ITypeSymbol? type = model.GetTypeInfo(syntax, cancellationToken).Type;
        if (type is null or IErrorTypeSymbol)
        {
            type = model.GetSpeculativeTypeInfo(syntax.SpanStart, syntax, SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
        }

        return type is IErrorTypeSymbol ? null : type;
    }

    private static string Text(SyntaxNode node) => Regex.Replace(node.ToString(), @"\s+", " ");

    private static string Plain(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string MetadataName(INamedTypeSymbol type) =>
        type.ContainingType is { } outer
            ? MetadataName(outer) + "+" + type.MetadataName
            : type.ContainingNamespace is { IsGlobalNamespace: false } space ? space.ToDisplayString() + "." + type.MetadataName : type.MetadataName;

    /// <summary>The map for this plan, pretty-printed so a committed copy diffs line by line.</summary>
    /// <remarks>
    /// The plan's names are bound back to symbols where the map needs more than the name: for each type's project,
    /// and to check an open behaviour's constraints against each request, as the container checks them when it
    /// closes the behaviour.
    /// </remarks>
    public static string Json(EmitPlan plan, ImmutableArray<PipelineOptions> calls, Compilation compilation)
    {
        Names names = new(compilation);
        calls = [.. calls.Where(static call => !call.Foreign)];

        // The generated AddRelevox returns early when it has already run, so only the first call to run applies its
        // options, and the build cannot tell which that is. With more than one call, the ones passing options are
        // listed as unread instead.
        List<object?> unread = calls.Length > 1
            ? [.. calls.Where(static call => call.Call is not null).Select(static call => call.Call)]
            : [.. calls.SelectMany(static call => call.Unread)];
        if (calls.Length > 1)
        {
            calls = [];
        }

        // TryAddEnumerable registers a service and implementation pair once, however often it is added.
        OptionRegistration[] options = [.. calls.SelectMany(static call => call.Registrations).Distinct()];
        bool auto = calls.Any(static call => call.AutoRegisterRequestProcessors);
        bool pre = calls.Any(static call => call.AddsPreProcessors);
        bool post = calls.Any(static call => call.AddsPostProcessors);

        List<object?> requests = [];
        // An abstract request can never be the runtime type, so the mediator has no dispatch for it either.
        foreach (RequestEntry entry in plan.Requests.Where(static entry => !entry.Request.IsAbstract))
        {
            ITypeSymbol? request = names.Bind(entry.Request.Type);
            ITypeSymbol? response = names.Bind(entry.ResponseType ?? "global::Relevox.Unit");
            string req = entry.Request.Type;
            string resp = entry.ResponseType ?? "global::Relevox.Unit";

            List<object?> preProcessors = [];
            List<object?> postProcessors = [];
            if (pre)
            {
                Collect(preProcessors, $"global::Relevox.Pipeline.IRequestPreProcessor<{req}>", "IRequestPreProcessor<>", OptionKind.PreProcessor, [request]);
            }

            if (post)
            {
                Collect(postProcessors, $"global::Relevox.Pipeline.IRequestPostProcessor<{req}, {resp}>", "IRequestPostProcessor<,>", OptionKind.PostProcessor, [request, response]);
            }

            requests.Add(Object(
                ("type", Display(req)),
                ("response", entry.ResponseType is null ? null : Display(resp)),
                ("project", Names.Project(request)),
                ("handler", Component(names, entry.HandlerType)),
                ("behaviors", Behaviors(OptionKind.Behavior, $"global::Relevox.IPipelineBehavior<{req}, {resp}>", request, response)),
                ("preProcessors", preProcessors),
                ("postProcessors", postProcessors),
                ("exceptionActions", Exceptions($"global::Relevox.Pipeline.IRequestExceptionAction<{req}, ", "IRequestExceptionAction<,>", [request])),
                ("exceptionHandlers", Exceptions($"global::Relevox.Pipeline.IRequestExceptionHandler<{req}, {resp}, ", "IRequestExceptionHandler<,,>", [request, response]))));

            void Collect(List<object?> into, string service, string open, OptionKind kind, ITypeSymbol?[] arguments)
            {
                List<(string Type, bool Open)> found = [];
                if (auto)
                {
                    found.AddRange(plan.Processors.Where(registration => registration.ServiceType == service).Select(static registration => (registration.ImplementationType, false)));
                    found.AddRange(OpenClosings(plan.OpenProcessors.Where(registration => registration.ServiceType.EndsWith(open, System.StringComparison.Ordinal)).Select(static registration => registration.ImplementationType), arguments).Select(static type => (type, true)));
                }

                found.AddRange(FromOptions(kind, service, arguments));

                // TryAddEnumerable keeps the first registration of a class under one service, as registered: an open
                // registration and a closed one for the same class are two descriptors, and both run.
                into.AddRange(found.Distinct().Select(step => Component(names, step.Type)));
            }

            List<object?> Exceptions(string prefix, string open, ITypeSymbol?[] arguments)
            {
                List<object?> found = [.. plan.ExceptionRegistrations
                    .Where(registration => registration.ServiceType.StartsWith(prefix, System.StringComparison.Ordinal))
                    .Select(registration => Exception(registration.ImplementationType, registration.ServiceType.Substring(prefix.Length, registration.ServiceType.Length - prefix.Length - 1)))];

                foreach (OpenRegistration registration in plan.OpenProcessors.Where(registration => registration.ServiceType.EndsWith(open, System.StringComparison.Ordinal)))
                {
                    string exception = registration.Exception ?? "global::System.Exception";
                    foreach (string closed in OpenClosings([registration.ImplementationType], [.. arguments, names.Bind(exception)]))
                    {
                        found.Add(Exception(closed, exception));
                    }
                }

                return found;
            }

            List<(string, object?)> Exception(string type, string exception)
            {
                List<(string, object?)> component = Component(names, type);
                component.Add(("exception", Display(exception)));
                return component;
            }
        }

        List<object?> streams = [];
        foreach (StreamEntry entry in plan.Streams.Where(static entry => !entry.Request.IsAbstract))
        {
            ITypeSymbol? request = names.Bind(entry.Request.Type);
            ITypeSymbol? item = names.Bind(entry.ResponseSyntax);
            string itemName = item is null ? entry.ResponseSyntax : Plain(item);
            streams.Add(Object(
                ("type", Display(entry.Request.Type)),
                ("response", Display(itemName)),
                ("project", Names.Project(request)),
                ("handler", Component(names, entry.HandlerType)),
                ("behaviors", Behaviors(OptionKind.StreamBehavior, $"global::Relevox.IStreamPipelineBehavior<{entry.Request.Type}, {itemName}>", request, item))));
        }

        List<object?> notifications = [];
        foreach (NotificationEntry entry in plan.Notifications)
        {
            ITypeSymbol? notification = names.Bind(entry.Notification.Type);
            notifications.Add(Object(
                ("type", Display(entry.Notification.Type)),
                ("project", Names.Project(notification)),
                ("handlers", entry.Targets.Select(target => Component(names, target.HandlerType)).ToList<object?>())));
        }

        StringBuilder json = new();
        Write(json, Object(
            ("schemaVersion", 1),
            ("project", compilation.AssemblyName),
            ("requests", Sorted(requests)),
            ("streams", Sorted(streams)),
            ("notifications", Sorted(notifications)),
            ("unread", unread)), 0);
        return json.Append('\n').ToString();

        List<object?> Behaviors(OptionKind kind, string service, ITypeSymbol? request, ITypeSymbol? response) =>
            [.. FromOptions(kind, service, [request, response]).Select(step => Component(names, step.Type))];

        // Each class with whether it was registered open, which keeps it apart from a closed registration of it.
        IEnumerable<(string Type, bool Open)> FromOptions(OptionKind kind, string service, ITypeSymbol?[] arguments)
        {
            foreach (OptionRegistration registration in options.Where(registration => registration.Kind == kind))
            {
                if (registration.Service is null)
                {
                    foreach (string closed in OpenClosings([registration.Implementation], arguments))
                    {
                        yield return (closed, true);
                    }
                }
                else if (registration.Service == service)
                {
                    yield return (registration.Implementation, false);
                }
            }
        }

        // The container closes an open generic over the service's type arguments, in order, and leaves it out when
        // that breaks a constraint.
        IEnumerable<string> OpenClosings(IEnumerable<string> definitions, ITypeSymbol?[] arguments)
        {
            if (arguments.Any(static argument => argument is null))
            {
                yield break;
            }

            foreach (string definition in definitions)
            {
                if (names.Bind(definition) is INamedTypeSymbol { OriginalDefinition: { } open }
                    && Discovery.Close(open, arguments!, compilation) is { } closed)
                {
                    yield return names.Remember(closed);
                }
            }
        }
    }

    // The plan's names are fully qualified, so dropping global:: gives the name the map uses without binding it.
    private static string Display(string name) => name.Replace("global::", "");

    private static List<object?> Sorted(List<object?> entries) =>
        [.. entries.OrderBy(static entry => (string?)((List<(string, object?)>)entry!)[0].Item2, System.StringComparer.Ordinal)];

    private static List<(string, object?)> Component(Names names, string type) =>
        Object(("type", Display(type)), ("project", Names.Project(names.Bind(type))));

    private static List<(string, object?)> Object(params (string, object?)[] fields) => [.. fields];

    // Just enough JSON: objects as ordered field lists, arrays, strings, ints and null, two-space indented.
    private static void Write(StringBuilder json, object? value, int depth)
    {
        switch (value)
        {
            case null:
                json.Append("null");
                break;
            case int number:
                json.Append(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case string text:
                json.Append('"');
                foreach (char character in text)
                {
                    json.Append(character switch
                    {
                        '"' => "\\\"",
                        '\\' => "\\\\",
                        < ' ' => "\\u" + ((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture),
                        _ => character.ToString(),
                    });
                }

                json.Append('"');
                break;
            case List<(string Name, object? Value)> fields:
                WriteAll(json, '{', '}', fields, depth, static (json, field, depth) =>
                {
                    Write(json, field.Name, depth);
                    json.Append(": ");
                    Write(json, field.Value, depth);
                });
                break;
            case List<object?> items:
                WriteAll(json, '[', ']', items, depth, static (json, item, depth) => Write(json, item, depth));
                break;
        }
    }

    private static void WriteAll<T>(StringBuilder json, char open, char close, List<T> items, int depth, System.Action<StringBuilder, T, int> write)
    {
        json.Append(open);
        for (int index = 0; index < items.Count; index++)
        {
            json.Append(index == 0 ? "\n" : ",\n").Append(' ', (depth + 1) * 2);
            write(json, items[index], depth + 1);
        }

        if (items.Count > 0)
        {
            json.Append('\n').Append(' ', depth * 2);
        }

        json.Append(close);
    }

    /// <summary>The generated file that carries <paramref name="json"/> into the assembly.</summary>
    /// <remarks>
    /// The task finds the map by this type's name and the field's, in whatever namespace the mediator is in, so
    /// both names are part of the contract with Relevox.Build.
    /// </remarks>
    public static string Source(string ns, string json) =>
        $$"""
        // <auto-generated/>
        namespace {{ns}}
        {
            /// <summary>The message map, read from this assembly by Relevox's build task after compilation.</summary>
            [global::System.CodeDom.Compiler.GeneratedCode("Relevox", "{{typeof(MessageMap).Assembly.GetName().Version}}")]
            internal static class RelevoxMessageMap
            {
                internal const string Json = {{SymbolDisplay.FormatLiteral(json, quote: true)}};
            }
        }

        """;

    /// <summary>The plan's fully qualified names, bound back to symbols.</summary>
    private sealed class Names(Compilation compilation)
    {
        private readonly SemanticModel? _model = compilation.SyntaxTrees.FirstOrDefault() is { } tree ? compilation.GetSemanticModel(tree) : null;
        private readonly Dictionary<string, ITypeSymbol?> _bound = new(System.StringComparer.Ordinal);

        public ITypeSymbol? Bind(string name)
        {
            if (!_bound.TryGetValue(name, out ITypeSymbol? type))
            {
                // Most names are a plain type in a namespace, found by metadata name far faster than by binding.
                // Anything else, such as a nested or generic type or a keyword, is bound as source would be.
                type = name.StartsWith("global::", System.StringComparison.Ordinal) && name.IndexOfAny(['<', '@']) < 0
                    ? compilation.GetTypeByMetadataName(name.Substring("global::".Length))
                    : null;
                type ??= _model?.GetSpeculativeTypeInfo(0, SyntaxFactory.ParseTypeName(name), SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
                _bound[name] = type = type is null or IErrorTypeSymbol ? null : type;
            }

            return type;
        }

        /// <summary>The name of a symbol in hand, kept so that naming it again does not bind it again.</summary>
        public string Remember(ITypeSymbol type)
        {
            string name = Plain(type);
            _bound[name] = type;
            return name;
        }

        public static string? Project(ITypeSymbol? type) => type?.OriginalDefinition.ContainingAssembly?.Name;
    }
}
