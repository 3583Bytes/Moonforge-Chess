using System.Text.RegularExpressions;

namespace ChessBin.Web.Tests;

/// <summary>
/// The boot pages under <c>wwwroot</c> hand-duplicate the site chrome, because crawlers and
/// link previews never execute WebAssembly and GitHub Pages needs a real directory index per
/// route. Nothing forces those six copies to agree with <c>Layout/SiteNav.razor</c>, and when
/// they drift the page visibly changes shape as Blazor takes over — which is exactly the kind
/// of thing nobody notices until a visitor mentions it. These tests make drift a build failure.
/// </summary>
public sealed class BootPageTests
{
    private static string WebRoot
    {
        get
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "ChessBin.Web", "wwwroot")))
                dir = dir.Parent;
            return Path.Combine(dir!.FullName, "ChessBin.Web");
        }
    }

    private static string Wwwroot => Path.Combine(WebRoot, "wwwroot");

    /// <summary>
    /// Boot page, the route it is the index for, and which nav item it should light up.
    /// Those last two differ for practice: it is reachable from the daily puzzle and from the
    /// home grid but is not a nav item of its own, because seven tabs do not fit on a phone —
    /// so it highlights the section it belongs to instead.
    /// </summary>
    private static readonly (string File, string Route, string Active)[] BootPages =
    [
        ("index.html", "/", "/"),
        ("play-online/index.html", "/play-online/", "/play-online/"),
        ("puzzle/index.html", "/puzzle/", "/puzzle/"),
        ("puzzle/practice/index.html", "/puzzle/practice/", "/puzzle/"),
        ("openings/index.html", "/openings/", "/openings/"),
        ("review/index.html", "/review/", "/review/"),
        ("vote/index.html", "/vote/", "/vote/"),
    ];

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Wwwroot, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The nav labels SiteNav.razor renders, in the order it renders them.</summary>
    private static string[] NavLabels()
    {
        string nav = File.ReadAllText(Path.Combine(WebRoot, "Layout", "SiteNav.razor"));
        return Regex.Matches(nav, @"<span>([^<]+)</span>").Select(m => m.Groups[1].Value).ToArray();
    }

    [Test]
    public void EveryRouteInTheSitemapHasABootPage()
    {
        string sitemap = Read("sitemap.xml");

        Assert.Multiple(() =>
        {
            foreach (Match loc in Regex.Matches(sitemap, @"<loc>https://chessbin\.com(?<path>[^<]*)</loc>"))
            {
                string path = loc.Groups["path"].Value;
                string expected = path == "/" ? "index.html" : path.Trim('/') + "/index.html";
                Assert.That(File.Exists(Path.Combine(Wwwroot, expected.Replace('/', Path.DirectorySeparatorChar))),
                    Is.True, $"sitemap lists {path} but there is no boot page at {expected}");
            }
        });
    }

    [Test]
    public void EveryBootPageCarriesTheSameNavigationAsTheApp()
    {
        string[] labels = NavLabels();
        Assert.That(labels, Is.Not.Empty, "could not read the nav out of SiteNav.razor");

        Assert.Multiple(() =>
        {
            foreach ((string file, _, _) in BootPages)
            {
                string html = Read(file);
                string[] found = Regex.Matches(html, @"<li><a href=""[^""]*""[^>]*>.*?<span>([^<]+)</span></a></li>")
                    .Select(m => m.Groups[1].Value)
                    .ToArray();

                Assert.That(found, Is.EqualTo(labels),
                    $"{file} nav is out of step with SiteNav.razor — regenerate it");
            }
        });
    }

    [Test]
    public void EveryBootPageMarksItsOwnRouteAsTheActiveOne()
    {
        Assert.Multiple(() =>
        {
            foreach ((string file, _, string expected) in BootPages)
            {
                string html = Read(file);
                var active = Regex.Matches(html, @"<li><a href=""(?<href>[^""]*)"" class=""active""")
                    .Select(m => m.Groups["href"].Value)
                    .ToArray();

                Assert.That(active, Has.Length.EqualTo(1), $"{file} must mark exactly one nav item active");
                Assert.That(active[0], Is.EqualTo(expected), $"{file} marks {active[0]} active, not {expected}");
            }
        });
    }

    [Test]
    public void EveryBootPageDeclaresItsOwnCanonicalUrlAndTitle()
    {
        var titles = new List<string>();

        Assert.Multiple(() =>
        {
            foreach ((string file, string route, _) in BootPages)
            {
                string html = Read(file);

                Match canonical = Regex.Match(html, @"<link rel=""canonical"" href=""(?<url>[^""]+)""");
                Assert.That(canonical.Success, Is.True, $"{file} has no canonical link");
                Assert.That(canonical.Groups["url"].Value, Is.EqualTo("https://chessbin.com" + route),
                    $"{file} points its canonical at another page, which de-indexes it");

                Match title = Regex.Match(html, @"<title>(?<t>[^<]+)</title>");
                Assert.That(title.Success, Is.True, $"{file} has no title");
                titles.Add(title.Groups["t"].Value);
            }

            Assert.That(titles.Distinct().Count(), Is.EqualTo(titles.Count),
                "two boot pages share a title, so search results cannot tell them apart");
        });
    }

    /// <summary>
    /// The audience change is easy to undo by accident, because the implementation detail is
    /// always the most interesting thing to whoever is writing the copy. These are the phrases
    /// that were removed; they should not come back on a page meant for chess players.
    /// </summary>
    [Test]
    public void NoBootPageExplainsTheImplementationToThePlayer()
    {
        string[] banned = ["WebAssembly", "neural network", "the repository", "open source"];

        Assert.Multiple(() =>
        {
            foreach ((string file, _, _) in BootPages)
            {
                // Only the visible body copy: <head> legitimately mentions WebAssembly in the
                // schema.org browserRequirements field, and the boot script tag names the file.
                Match body = Regex.Match(Read(file), @"<main class=""page"">(?<copy>.*?)</main>", RegexOptions.Singleline);
                Assert.That(body.Success, Is.True, $"{file} has no page content");

                foreach (string phrase in banned)
                {
                    Assert.That(body.Groups["copy"].Value,
                        Does.Not.Contain(phrase).IgnoreCase,
                        $"{file} explains \"{phrase}\" to an audience that came to play chess");
                }
            }
        });
    }
}
