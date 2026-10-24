using System.Globalization;
using Microsoft.CodeAnalysis;
using Relevox.Migration;

namespace Relevox.SourceGenerator.Tests;

/// <summary>RVX0011, notifications published from a layer not allowed to, and RVX0012, a layer setting that cannot be used (#52).</summary>
public sealed class PublishLayerTests
{
    // The Clean Architecture shape: domain events raised in the domain, published by the infrastructure once the
    // entities are saved, and an API that publishes notifications of its own.
    private const string Shop = """
        using Relevox;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Shop.Domain.Common
        {
            public abstract record BaseEvent : INotification;
        }

        namespace Shop.Domain.Events
        {
            public sealed record OrderPlaced(int Id) : Shop.Domain.Common.BaseEvent;
        }

        namespace Shop.Application
        {
            public sealed record CacheCleared : INotification;
        }

        namespace Shop.Infrastructure.Data
        {
            public sealed class SaveInterceptor(IPublisher publisher)
            {
                public Task Dispatch(Shop.Domain.Common.BaseEvent domainEvent) => publisher.Publish(domainEvent);
            }
        }

        namespace Shop.Web.Endpoints
        {
            public sealed class Orders(IMediator mediator)
            {
                public Task Shortcut() => mediator.Publish(new Shop.Domain.Events.OrderPlaced(1));

                public Task Clear(CancellationToken cancellationToken) => mediator.Publish(new Shop.Application.CacheCleared(), cancellationToken);
            }
        }

        public sealed class Script(IPublisher publisher)
        {
            public Task Run() => publisher.Publish(new Shop.Domain.Events.OrderPlaced(2));
        }
        """;

    private static readonly Dictionary<string, string> Layers = new()
    {
        ["relevox.layer.domain"] = "Shop.Domain",
        ["relevox.layer.infrastructure"] = "Shop.Infrastructure",
        ["relevox.layer.web"] = "Shop.Web, Shop.Api",
        ["relevox.publish_from.infrastructure"] = "Shop.Domain.Common.BaseEvent",
    };

    [Fact]
    public void Domain_events_published_outside_the_allowed_layer_are_reported_and_the_rest_is_not()
    {
        Diagnostic[] diagnostics = Analyze(Layers, Shop);

        diagnostics.Select(static diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture)).ShouldBe(
        [
            "'Shop.Domain.Events.OrderPlaced' is published from layer 'web', but a 'Shop.Domain.Common.BaseEvent' may only be published from 'infrastructure'",
            "'Shop.Domain.Events.OrderPlaced' is published from code in no layer, but a 'Shop.Domain.Common.BaseEvent' may only be published from 'infrastructure'",
        ]);
        diagnostics.ShouldAllBe(static diagnostic => diagnostic.Id == "RVX0011" && diagnostic.Severity == DiagnosticSeverity.Info);
        diagnostics[0].Location.SourceTree!.GetText().ToString(diagnostics[0].Location.SourceSpan).ShouldBe("Publish");
    }

    [Fact]
    public void A_kind_can_be_allowed_from_two_layers()
    {
        Analyze(
            new()
            {
                ["relevox.layer.infrastructure"] = "Shop.Infrastructure",
                ["relevox.layer.web"] = "Shop.Web",
                ["relevox.publish_from.infrastructure"] = "Shop.Domain.Common.BaseEvent",
                ["relevox.publish_from.web"] = "Shop.Domain.Common.BaseEvent",
            },
            Shop).Select(static diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture)).ShouldBe(
        [
            "'Shop.Domain.Events.OrderPlaced' is published from code in no layer, but a 'Shop.Domain.Common.BaseEvent' may only be published from 'infrastructure' or 'web'",
        ]);
    }

    [Fact]
    public void An_interface_kind_covers_every_notification_that_implements_it_and_every_matching_rule_applies()
    {
        Analyze(
            new()
            {
                ["relevox.layer.infrastructure"] = "Shop.Infrastructure",
                ["relevox.layer.web"] = "Shop.Web",
                ["relevox.publish_from.web"] = "Relevox.INotification",
            },
            Shop).Select(static diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture)).ShouldBe(
        [
            "'Shop.Domain.Common.BaseEvent' is published from layer 'infrastructure', but a 'Relevox.INotification' may only be published from 'web'",
            "'Shop.Domain.Events.OrderPlaced' is published from code in no layer, but a 'Relevox.INotification' may only be published from 'web'",
        ]);
    }

    [Fact]
    public void Without_layer_settings_nothing_is_reported()
    {
        Analyze([], Shop).ShouldBeEmpty();
    }

    [Fact]
    public void Settings_that_cannot_be_used_are_reported_once_each()
    {
        Diagnostic[] diagnostics = Analyze(
            new()
            {
                ["relevox.layer.domain"] = "Shop.Domain",
                ["relevox.layer.empty"] = " , ",
                ["relevox.publish_from.infrastructure"] = "Shop.Domain.Common.BaseEvent",
                ["relevox.publish_from.domain"] = "Shop.Domain.Common.Missing",
            },
            Shop);

        diagnostics.ShouldAllBe(static diagnostic => diagnostic.Id == "RVX0012" && diagnostic.Severity == DiagnosticSeverity.Warning);
        diagnostics.Select(static diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture)).ShouldBe(
        [
            "The setting 'relevox.layer.empty' cannot be used: it lists no namespace",
            "The setting 'relevox.publish_from.domain' cannot be used: the type 'Shop.Domain.Common.Missing' is not in this project or the projects it references; write its full name, with `1 for one generic parameter",
            "The setting 'relevox.publish_from.infrastructure' cannot be used: no 'relevox.layer.infrastructure' setting defines the layer 'infrastructure'",
        ]);
    }

    [Fact]
    public void A_project_with_no_code_in_any_layer_is_not_told_it_cannot_see_a_type()
    {
        Analyze(
            new()
            {
                ["relevox.layer.domain"] = "Elsewhere.Domain",
                ["relevox.layer.infrastructure"] = "Elsewhere.Infrastructure",
                ["relevox.publish_from.infrastructure"] = "Elsewhere.Domain.Common.BaseEvent",
            },
            Shop).ShouldBeEmpty();
    }

    private static Diagnostic[] Analyze(Dictionary<string, string> settings, string source) =>
        [.. GeneratorHarness.Analyze(new PublishLayerAnalyzer(), settings, source)
            .OrderBy(static diagnostic => diagnostic.Location.SourceSpan.Start)
            .ThenBy(static diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparer.Ordinal)];
}
