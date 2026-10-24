using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Relevox.Migration;

/// <summary>
/// Checks that notifications of a configured kind are published only from the layers allowed to publish them.
/// </summary>
/// <remarks>
/// <para>
/// Layers and the rule live in <c>.editorconfig</c> or a global config, so a project without them gets nothing:
/// </para>
/// <code>
/// relevox.layer.domain = MyApp.Domain
/// relevox.layer.infrastructure = MyApp.Infrastructure
/// relevox.publish_from.infrastructure = MyApp.Domain.Common.BaseEvent
/// </code>
/// <para>
/// A layer is one or more namespaces, each with everything below it; code belongs to the layer with the longest
/// matching namespace. Keys are case-insensitive in <c>.editorconfig</c>, which lower-cases them, so layer names are
/// too, and the types go in the values. A notification of several configured kinds must satisfy each. A setting
/// that cannot be used is reported as RVX0012, not ignored.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PublishLayerAnalyzer : DiagnosticAnalyzer
{
    private const string LayerPrefix = "relevox.layer.";
    private const string PublishPrefix = "relevox.publish_from.";

    /// <summary>A notification published from a layer its kind may not be published from.</summary>
    public static readonly DiagnosticDescriptor WrongLayer = new(
        id: "RVX0011",
        title: "Notification published from the wrong layer",
        messageFormat: "'{0}' is published from {1}, but a '{2}' may only be published from {3}",
        category: "Relevox",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        helpLinkUri: "https://github.com/tunahanaliozturk/relevox/blob/main/docs/diagnostics.md#rvx0011",
        description: "The project's settings say which layers may publish notifications of a given kind, such as domain events published only by the code that saves the entities raising them. Nothing is reported without such settings.");

    /// <summary>A layer setting that cannot be used.</summary>
    public static readonly DiagnosticDescriptor BadSetting = new(
        id: "RVX0012",
        title: "Layer setting cannot be used",
        messageFormat: "The setting '{0}' cannot be used: {1}",
        category: "Relevox",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: [WellKnownDiagnosticTags.CompilationEnd],
        helpLinkUri: "https://github.com/tunahanaliozturk/relevox/blob/main/docs/diagnostics.md#rvx0012",
        description: "A warning, unlike the architecture rules, because it is only ever reported to a project that wrote the setting, and a rule that silently checks nothing is worse than no rule.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [WrongLayer, BadSetting];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            if (start.Compilation.GetTypeByMetadataName("Relevox.INotification") is null)
            {
                return;
            }

            // Option sets are shared between the files that have the same settings, so they are read once each.
            ConcurrentDictionary<AnalyzerConfigOptions, Settings> read = new();
            Settings For(SyntaxTree tree) =>
                read.GetOrAdd(start.Options.AnalyzerConfigOptionsProvider.GetOptions(tree), options => Settings.Read(options, start.Compilation));

            start.RegisterOperationAction(operation => AnalyzePublish(operation, For(operation.Operation.Syntax.SyntaxTree)), OperationKind.Invocation);
            start.RegisterCompilationEndAction(end =>
            {
                foreach (SyntaxTree tree in end.Compilation.SyntaxTrees)
                {
                    For(tree);
                }

                foreach ((string key, string problem) in read.Values.SelectMany(static settings => settings.Problems).Distinct().OrderBy(static problem => problem.Key, StringComparer.Ordinal))
                {
                    end.ReportDiagnostic(Diagnostic.Create(BadSetting, Location.None, key, problem));
                }
            });
        });
    }

    private static void AnalyzePublish(OperationAnalysisContext context, Settings settings)
    {
        IInvocationOperation invocation = (IInvocationOperation)context.Operation;
        if (settings.Rules.Length == 0
            || invocation.TargetMethod is not { Name: "Publish" } method
            || !IsPublisher(method.ContainingType))
        {
            return;
        }

        IOperation? value = invocation.Arguments.FirstOrDefault(static argument => argument.Parameter?.Ordinal == 0)?.Value;
        while (value is IConversionOperation { IsImplicit: true } conversion)
        {
            value = conversion.Operand;
        }

        if (value?.Type is not INamedTypeSymbol notification)
        {
            return;
        }

        string? layer = settings.LayerOf(context.ContainingSymbol.ContainingNamespace?.ToDisplayString() ?? "");
        foreach (Rule rule in settings.Rules)
        {
            if (!rule.Layers.Contains(layer ?? "") && IsA(notification, rule.Kind))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    WrongLayer,
                    NameLocation(invocation),
                    notification.ToDisplayString(),
                    layer is null ? "code in no layer" : $"layer '{layer}'",
                    rule.Kind.ToDisplayString(),
                    string.Join(" or ", rule.Layers.Select(static allowed => $"'{allowed}'"))));
                return;
            }
        }
    }

    /// <summary>Whether the notification is the kind, derives from it or implements it, generic definitions compared.</summary>
    private static bool IsA(INamedTypeSymbol notification, INamedTypeSymbol kind)
    {
        for (INamedTypeSymbol? type = notification; type is not null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, kind))
            {
                return true;
            }
        }

        return notification.AllInterfaces.Any(contract => SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, kind));
    }

    private static bool IsPublisher(INamedTypeSymbol? type) =>
        type is not null && (IsContract(type) || type.AllInterfaces.Any(IsContract));

    private static bool IsContract(INamedTypeSymbol type) =>
        type.ContainingNamespace is { Name: "Relevox", ContainingNamespace.IsGlobalNamespace: true } && type.Name is "IPublisher" or "IMediator";

    private static Location NameLocation(IInvocationOperation invocation) => invocation.Syntax switch
    {
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } => access.Name.GetLocation(),
        InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax binding } => binding.Name.GetLocation(),
        _ => invocation.Syntax.GetLocation(),
    };

    private sealed class Rule(INamedTypeSymbol kind, ImmutableArray<string> layers)
    {
        public INamedTypeSymbol Kind { get; } = kind;

        public ImmutableArray<string> Layers { get; } = layers;
    }

    /// <summary>The layers and rules one set of options describes, and what in them could not be used.</summary>
    private sealed class Settings
    {
        private readonly (string Namespace, string Layer)[] _namespaces;

        private Settings((string Namespace, string Layer)[] namespaces, ImmutableArray<Rule> rules, ImmutableArray<(string Key, string Problem)> problems)
        {
            _namespaces = namespaces;
            Rules = rules;
            Problems = problems;
        }

        public ImmutableArray<Rule> Rules { get; }

        public ImmutableArray<(string Key, string Problem)> Problems { get; }

        public static Settings Read(AnalyzerConfigOptions options, Compilation compilation)
        {
            List<(string Namespace, string Layer)> namespaces = [];
            Dictionary<string, List<string>> kinds = new(StringComparer.Ordinal);
            List<(string Key, string Problem)> problems = [];
            HashSet<string> layers = new(StringComparer.Ordinal);

            foreach (string key in options.Keys.Where(static key => key.StartsWith(LayerPrefix, StringComparison.Ordinal)))
            {
                string layer = key.Substring(LayerPrefix.Length);
                string[] values = Split(options, key);
                if (layer.Length == 0 || values.Length == 0)
                {
                    problems.Add((key, layer.Length == 0 ? "it names no layer" : "it lists no namespace"));
                    continue;
                }

                layers.Add(layer);
                namespaces.AddRange(values.Select(space => (space, layer)));
            }

            foreach (string key in options.Keys.Where(static key => key.StartsWith(PublishPrefix, StringComparison.Ordinal)))
            {
                string layer = key.Substring(PublishPrefix.Length);
                string[] values = Split(options, key);
                if (!layers.Contains(layer))
                {
                    problems.Add((key, $"no '{LayerPrefix}{layer}' setting defines the layer '{layer}'"));
                    continue;
                }

                if (values.Length == 0)
                {
                    problems.Add((key, "it lists no notification type"));
                }

                foreach (string kind in values)
                {
                    if (!kinds.TryGetValue(kind, out List<string>? allowed))
                    {
                        kinds[kind] = allowed = [];
                    }

                    allowed.Add(layer);
                }
            }

            // A settings file usually sits at the root and reaches every project, including ones that cannot see the
            // domain. A type it cannot find is only worth reporting where the project has code in one of the layers.
            bool inLayers = namespaces.Any(entry => Declares(compilation.Assembly.GlobalNamespace, entry.Namespace));
            ImmutableArray<Rule>.Builder rules = ImmutableArray.CreateBuilder<Rule>();
            foreach (KeyValuePair<string, List<string>> kind in kinds.OrderBy(static kind => kind.Key, StringComparer.Ordinal))
            {
                if (compilation.GetTypeByMetadataName(kind.Key) is { } type)
                {
                    rules.Add(new Rule(type, [.. kind.Value.Distinct().OrderBy(static layer => layer, StringComparer.Ordinal)]));
                }
                else if (inLayers)
                {
                    foreach (string layer in kind.Value)
                    {
                        problems.Add((PublishPrefix + layer, $"the type '{kind.Key}' is not in this project or the projects it references; write its full name, with `1 for one generic parameter"));
                    }
                }
            }

            return new Settings([.. namespaces.OrderByDescending(static entry => entry.Namespace.Length)], rules.ToImmutable(), [.. problems]);
        }

        /// <summary>The layer of the longest namespace that contains the given one, or null.</summary>
        public string? LayerOf(string space)
        {
            foreach ((string prefix, string layer) in _namespaces)
            {
                if (space == prefix || (space.StartsWith(prefix, StringComparison.Ordinal) && space[prefix.Length] == '.'))
                {
                    return layer;
                }
            }

            return null;
        }

        private static bool Declares(INamespaceSymbol global, string name)
        {
            INamespaceSymbol? space = global;
            foreach (string part in name.Split('.'))
            {
                space = space?.GetNamespaceMembers().FirstOrDefault(member => member.Name == part);
            }

            return space is not null;
        }

        private static string[] Split(AnalyzerConfigOptions options, string key) =>
            options.TryGetValue(key, out string? value)
                ? [.. value.Split(',').Select(static part => part.Trim()).Where(static part => part.Length > 0)]
                : [];
    }
}
