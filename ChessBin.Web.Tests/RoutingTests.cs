using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace ChessBin.Web.Tests;

/// <summary>
/// Blazor builds its route table on the first render, so a duplicate or malformed route
/// takes down every page, not just the offending one — and nothing else in this suite
/// touches the router. These tests assert the invariants RouteTableFactory checks, at
/// build time instead of in production.
/// </summary>
public sealed class RoutingTests
{
    private static (Type Component, string Template)[] Routes() =>
        typeof(PuzzleData).Assembly.GetTypes()
            .Where(t => typeof(ComponentBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>()
                              .Select(a => (Component: t, a.Template)))
            .ToArray();

    /// <summary>Blazor treats "/puzzle" and "/puzzle/" as one route, so both on one component is fatal.</summary>
    private static string Normalise(string template) => template.Trim('/');

    [Test]
    public void NoTwoRoutes_ResolveToTheSameTemplate()
    {
        var duplicates = Routes()
            .GroupBy(r => Normalise(r.Template), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"'{g.Key}' declared {g.Count()}x by {string.Join(", ", g.Select(r => r.Component.Name).Distinct())}")
            .ToArray();

        Assert.That(duplicates, Is.Empty,
            "ambiguous routes crash the whole app on first render: " + string.Join("; ", duplicates));
    }

    [Test]
    public void EveryRoute_IsWellFormed()
    {
        var routes = Routes();

        Assert.Multiple(() =>
        {
            Assert.That(routes, Is.Not.Empty, "no routable components found — is the assembly reference right?");

            foreach ((Type component, string template) in routes)
            {
                Assert.That(template, Does.StartWith("/"), $"{component.Name}: route must be rooted");
                Assert.That(template, Does.Not.Contain("//"), $"{component.Name}: '{template}' has an empty segment");
                if (template.Length > 1)
                    Assert.That(template, Does.Not.EndWith("/"),
                        $"{component.Name}: '{template}' has a trailing slash, which collides with the same route without one");
            }
        });
    }

    [Test]
    public void TheRoutesTheSiteLinksTo_AllExist()
    {
        var templates = Routes().Select(r => Normalise(r.Template)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Every internal destination the UI offers. A link to a route that doesn't exist
        // silently falls through to the not-found page.
        Assert.Multiple(() =>
        {
            Assert.That(templates, Does.Contain("puzzle"), "the nav links to /puzzle");
            Assert.That(templates, Does.Contain("puzzle/practice"), "the daily puzzle links to /puzzle/practice");
            Assert.That(templates, Does.Contain("openings"), "the nav links to /openings");
            // Every pre-rendered opening page is served at its own URL, and the app boots over
            // it. Without this route the router would answer with the not-found page a moment
            // after the page had already rendered correctly — which looks like a broken link
            // to a visitor and undoes the reason those pages exist.
            // Contains through the set, not Does.Contain, so the assertion does not depend on
            // how the route parameter happens to be capitalised.
            Assert.That(templates.Contains("openings/{slug}"), Is.True,
                "the generated opening pages live at /openings/<slug>");
            Assert.That(templates, Does.Contain("play-online"), "the nav links to /play-online");
            Assert.That(templates, Does.Contain("review"), "the nav links to /review");
            Assert.That(templates, Does.Contain("vote"), "the nav links to /vote");
            Assert.That(templates, Does.Contain(""), "the brand links to /");
        });
    }
}
