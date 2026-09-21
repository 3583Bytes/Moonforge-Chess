using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChessBin.Tools.OpeningPages;

/// <summary>
/// Writes the static pages the site is read by: one per named chess opening, plus the seven
/// boot pages for the app's own routes, plus the sitemap that lists them all.
/// <para>
/// Crawlers and link previews never execute WebAssembly, so a page's pre-boot markup is the
/// only thing they ever see. Until these existed the whole site was seven near-identical
/// shells of about ninety words each, which is not enough for Google to index — meanwhile the
/// opening data shipped 3,174 named lines that nothing outside the app could read.
/// </para>
/// <para>
/// Reads only what is already committed under <c>wwwroot</c>, so it needs no network and can
/// be re-run whenever the copy changes. Re-running on unchanged input rewrites every file
/// byte-identically.
/// </para>
/// </summary>
internal static class Program
{
    private const string Site = "https://chessbin.com";

    private static int Main(string[] args)
    {
        var opt = Options.Parse(args);
        if (opt is null) return 2;

        string openings = Path.Combine(opt.WebRoot, "openings");
        if (!Directory.Exists(Path.Combine(openings, "lines")))
        {
            Console.Error.WriteLine($"error: {Path.Combine(openings, "lines")} not found — run OpeningImport first");
            return 2;
        }

        Line[] lines = Load<Line>(Path.Combine(openings, "lines"), "shard-*.json");
        Pos[] positions = Load<Pos>(openings, "shard-*.json");
        Console.WriteLine($"read      {lines.Length:n0} named lines, {positions.Length:n0} positions");

        if (lines.Length == 0)
        {
            Console.Error.WriteLine("error: no lines found — was the import run with this version of OpeningImport?");
            return 1;
        }

        var byKey = positions.ToDictionary(p => p.K, StringComparer.Ordinal);
        var byName = lines.ToDictionary(l => l.N, StringComparer.Ordinal);

        Line[] chosen = Close(Select(lines, positions, opt.All), byName);
        Console.WriteLine($"selected  {chosen.Length:n0} of {lines.Length:n0} lines ({(opt.All ? "--all" : "families and the lines the book covers")})");

        // The families are the roots of the set — nothing above them links down, so the
        // explorer's own page carries the index that gives each of them an inbound link.
        var slugs = chosen.Select(l => l.G).ToHashSet(StringComparer.Ordinal);
        Line[] roots = [.. chosen
            .Where(l => Parent(l, byName) is not Line up || !slugs.Contains(up.G))
            .OrderBy(l => l.N, StringComparer.Ordinal)];

        int written = WriteAppPages(opt.WebRoot, roots);
        written += WriteOpeningPages(chosen, byKey, byName, opt.WebRoot);
        WriteSitemap(chosen, opt.WebRoot);

        Console.WriteLine($"wrote     {written:n0} pages + sitemap.xml to {opt.WebRoot}");
        return 0;
    }

    // ── Selection ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Which lines get a page. Everything ships eventually, but a brand-new site that drops
    /// three thousand templated pages at once looks exactly like the bulk content Google
    /// declines to index, so the first pass is the part a reader would actually search for:
    /// the opening families, and the lines the engine's own book covers — which is as far as
    /// mainline theory goes. <c>--all</c> lifts the filter once the first pass is indexing.
    /// </summary>
    private static Line[] Select(Line[] lines, Pos[] positions, bool all)
    {
        if (all) return [.. lines.OrderBy(l => l.G, StringComparer.Ordinal)];

        var booked = positions
            .SelectMany(p => p.M)
            .Where(m => m.N is not null && m.W > 0)
            .Select(m => m.N!)
            .ToHashSet(StringComparer.Ordinal);

        return [.. lines
            .Where(l => !l.N.Contains(':') || booked.Contains(l.N))
            .OrderBy(l => l.G, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Pulls in the line each selected line is a variation of, and so on up to the family, so
    /// every page's parent is a page too. Without this the breadcrumb at the foot of a page
    /// points at a route that was never generated — which is a 404 for a reader and a dead
    /// end for a crawler walking the set.
    /// </summary>
    private static Line[] Close(Line[] chosen, Dictionary<string, Line> byName)
    {
        var keep = chosen.ToDictionary(l => l.N, StringComparer.Ordinal);

        foreach (Line line in chosen)
        {
            for (Line? up = Parent(line, byName); up is not null; up = Parent(up, byName))
            {
                if (!keep.TryAdd(up.N, up)) break;   // already in, so its own ancestors are too
            }
        }

        return [.. keep.Values.OrderBy(l => l.G, StringComparer.Ordinal)];
    }

    // ── The app's own boot pages ────────────────────────────────────────────────

    private static int WriteAppPages(string webRoot, Line[] roots)
    {
        foreach (AppPage p in AppPages.All)
        {
            var ld = new StringBuilder();
            ld.Append("    {\n");
            ld.Append("      \"@context\": \"https://schema.org\",\n");
            ld.Append("      \"@type\": \"WebApplication\",\n");
            ld.Append($"      \"name\": \"{Json(p.LdName)}\",\n");
            ld.Append($"      \"url\": \"{Site}{p.Route}\",\n");
            ld.Append("      \"applicationCategory\": \"GameApplication\",\n");
            ld.Append("      \"operatingSystem\": \"Any modern web browser\",\n");
            if (p.LdBrowserRequirements is not null)
                ld.Append($"      \"browserRequirements\": \"{Json(p.LdBrowserRequirements)}\",\n");
            ld.Append("      \"isAccessibleForFree\": true,\n");
            ld.Append("      \"offers\": { \"@type\": \"Offer\", \"price\": \"0\", \"priceCurrency\": \"USD\" },\n");
            ld.Append($"      \"description\": \"{Json(p.LdDescription)}\"\n");
            ld.Append("    }");

            var body = new StringBuilder();
            body.Append($"""
                <section class="hero">
                    <div>
                        <p class="eyebrow">{p.Eyebrow}</p>
                        <h1>{p.H1}</h1>
                    </div>
                    <p class="intro">{p.Intro}</p>
                </section>

                <p class="boot-note">{p.Note}</p>

                <nav class="boot-links" aria-label="Go to">

                """);
            foreach ((string href, string text) in p.Links)
                body.Append($"    <a href=\"{href}\">{text}</a>\n");
            body.Append("</nav>\n\n");

            if (p.Route == "/openings/" && roots.Length > 0)
            {
                body.Append($"""
                    <div class="section-heading">
                        <h2>Every opening family</h2>
                        <span>{roots.Length} with a page</span>
                    </div>

                    <nav class="boot-links" aria-label="Opening families">

                    """);
                foreach (Line root in roots)
                    body.Append($"    <a href=\"/openings/{root.G}/\">{Html(root.N)}</a>\n");
                body.Append("</nav>\n\n");
            }

            body.Append(BootStatus());

            Write(Path.Combine(webRoot, p.File), Render(
                p.Comment, p.Description, p.Title, p.Route,
                p.OgTitle, p.OgDescription, p.OgImage, p.OgImageAlt,
                ld.ToString(), p.Active, body.ToString()));
        }

        return AppPages.All.Length;
    }

    // ── Opening pages ───────────────────────────────────────────────────────────

    private static int WriteOpeningPages(
        Line[] chosen,
        Dictionary<string, Pos> byKey,
        Dictionary<string, Line> byName,
        string webRoot)
    {
        var slugs = chosen.Select(l => l.G).ToHashSet(StringComparer.Ordinal);

        // Drop pages from an earlier run that this one no longer writes, so the committed
        // tree is exactly what the current selection produces and a narrowed run does not
        // leave unreachable pages behind in the sitemap's blind spot. "lines" is data, not
        // a page, so it stays.
        foreach (string dir in Directory.EnumerateDirectories(Path.Combine(webRoot, "openings")))
        {
            string name = Path.GetFileName(dir);
            if (name != "lines" && !slugs.Contains(name)) Directory.Delete(dir, recursive: true);
        }

        // The variations of each line, so a page can link down as well as up. Without this
        // a third of the set has no inbound link from any other page: a named line is only
        // reachable through a continuation when it sits exactly one move below its parent,
        // and most of them sit deeper.
        var children = new Dictionary<string, List<Line>>(StringComparer.Ordinal);
        foreach (Line l in chosen)
        {
            Line? up = Parent(l, byName);
            if (up is null || !slugs.Contains(up.G)) continue;
            if (!children.TryGetValue(up.N, out List<Line>? list)) children[up.N] = list = [];
            list.Add(l);
        }
        foreach (List<Line> list in children.Values) list.Sort((a, b) => string.CompareOrdinal(a.G, b.G));

        foreach (Line line in chosen)
        {
            byKey.TryGetValue(line.K, out Pos? pos);
            string family = Family(line.N);
            Line? parent = Parent(line, byName);

            // Every named continuation is shown, so the page tells the whole truth about the
            // position. Only the ones whose page was generated become links — the rest are
            // plain text rather than a link to a 404. A move that leads to another position
            // carrying this same name would link the page to itself, so it is dropped.
            var onward = (pos?.M ?? [])
                .Where(m => m.N is not null
                            && byName.TryGetValue(m.N, out Line? d)
                            && d.G != line.G)
                .ToArray();

            string ld = Breadcrumb(line, family, byName);
            string title = $"{line.N} — Chess Opening | ChessBin";
            string description = Clamp(
                $"{line.N}: the moves that reach it, the position, and what theory plays next. ECO {line.E}.", 157);

            Write(
                Path.Combine(webRoot, "openings", line.G, "index.html"),
                Render(
                    $"    Generated by tools/OpeningPages from wwwroot/openings — do not hand-edit.\n" +
                    $"    Page for {line.N} ({line.E}).",
                    description, title, $"/openings/{line.G}/",
                    $"ChessBin · {line.N}", description,
                    "brand/og-puzzle.png",
                    "The ChessBin mark above a rank of chessboard squares.",
                    ld, "/openings/",
                    OpeningBody(line, pos, parent, onward, byName, slugs, family,
                                children.GetValueOrDefault(line.N) ?? [])));
        }

        return chosen.Length;
    }

    /// <summary>
    /// The page a reader gets: what the line is called, the moves that reach it, the position
    /// on a board, and every named continuation as a link. The continuations are what make one
    /// opening page different from the next, and they are how a crawler walks the set at all —
    /// the app renders no crawlable links of its own.
    /// </summary>
    private static string OpeningBody(
        Line line, Pos? pos, Line? parent, Mv[] onward, Dictionary<string, Line> byName,
        HashSet<string> slugs, string family, List<Line> children)
    {
        var b = new StringBuilder();
        string moves = MoveText(line.P);
        int plies = line.P.Length;
        string eyebrow = line.N.Contains(':') ? $"ECO {line.E} · {Html(family)}" : $"ECO {line.E}";

        b.Append($"""
            <section class="hero">
                <div>
                    <p class="eyebrow">{eyebrow}</p>
                    <h1>{Html(line.N)}</h1>
                </div>
                <p class="intro">The position after {Html(moves)}.</p>
            </section>

            <section class="game-layout" aria-label="{Html(line.N)}">
                <div class="board-column">
                    <div class="player-strip opponent">
                        <div>
                            <strong>{Html(line.N)}</strong>
                            <span>{plies} half-moves in · {Continuations(onward.Length)}</span>
                        </div>
                        <span class="rating-chip" title="ECO classification">{line.E}</span>
                    </div>

                    <div class="board-wrap">

            """);

        b.Append(Board(line.K, line.N));

        b.Append("""
                    </div>

                    <p class="opening-line" aria-label="The moves that reach this position">

            """);
        for (int i = 0; i < line.P.Length; i++)
        {
            if (i % 2 == 0) b.Append($"            <span class=\"move-number\">{i / 2 + 1}.</span>\n");
            b.Append($"            <span class=\"opening-line-move\">{Html(line.P[i])}</span>\n");
        }
        b.Append("        </p>\n    </div>\n\n");

        b.Append($"""
                <aside class="control-panel puzzle-panel">
                    <div class="panel-tabs">
                        <button type="button">Continuations</button>
                        <span>{(onward.Length == 0 ? "—" : $"{onward.Length} named")}</span>
                    </div>

                    <section class="panel-section">

            """);

        if (onward.Length == 0)
        {
            b.Append("""
                        <p class="position-message">
                            Published theory stops here. From this position the game is on its own.
                        </p>

            """);
        }
        else
        {
            b.Append("            <ul class=\"opening-moves\">\n");
            foreach (Mv m in onward)
            {
                Line dest = byName[m.N!];
                bool linked = slugs.Contains(dest.G);
                string open = linked ? $"<a class=\"opening-move\" href=\"/openings/{dest.G}/\">" : "<span class=\"opening-move\">";
                string close = linked ? "</a>" : "</span>";
                b.Append($"""
                                <li>
                                    {open}
                                        <span class="opening-san">{Html(m.S)}</span>
                                        <span class="opening-name">{Html(dest.N)}</span>
                                        <span class="opening-weight">{Html(dest.E)}</span>
                                    {close}
                                </li>

                """);
            }
            b.Append("            </ul>\n");
        }

        b.Append("        </section>\n\n");
        b.Append("""
                    <section class="panel-section keep-going">
                        <a class="primary-button" href="/openings/">Open this line in the explorer</a>
                        <p class="position-message">Walks the same position move by move, and plays on against Moonforge.</p>
                    </section>
                </aside>
            </section>

            """);

        if (children.Count > 0)
        {
            b.Append($"""
                <div class="section-heading">
                    <h2>Variations of {Html(line.N)}</h2>
                    <span>{children.Count} named</span>
                </div>

                <nav class="boot-links" aria-label="Variations">

                """);
            foreach (Line child in children)
                b.Append($"    <a href=\"/openings/{child.G}/\">{Html(ShortName(child.N, line.N))}</a>\n");
            b.Append("</nav>\n\n");
        }

        var note = new StringBuilder();
        note.Append($"{Html(line.N)} is filed under ECO {line.E}");
        note.Append(parent is not null ? $", a variation of {Html(parent.N)}. " : ". ");
        note.Append($"It is the position after {Html(moves)} — {plies} half-moves in, with ");
        note.Append(SideToMove(line.K) == 'w' ? "White" : "Black");
        note.Append(" to move. ");
        note.Append(onward.Length switch
        {
            0 => "Named theory ends here; beyond this the position is yours to work out.",
            1 => $"From here theory names one continuation, {Html(onward[0].S)}.",
            _ => $"From here theory names {onward.Length} continuations: {Html(string.Join(", ", onward.Select(m => m.S)))}.",
        });

        b.Append($"<p class=\"boot-note\">{note}</p>\n\n");

        b.Append("<nav class=\"boot-links\" aria-label=\"Go to\">\n");
        if (parent is not null && slugs.Contains(parent.G))
            b.Append($"    <a href=\"/openings/{parent.G}/\">{Html(parent.N)}</a>\n");
        b.Append("    <a href=\"/openings/\">Opening explorer</a>\n");
        b.Append("    <a href=\"/puzzle/\">Today&#39;s puzzle</a>\n");
        b.Append("    <a href=\"/\">Play a game</a>\n");
        b.Append("</nav>\n\n");
        b.Append(BootStatus());
        return b.ToString();
    }

    /// <summary>
    /// The board as plain markup: the same grid of squares and Unicode glyphs the app renders,
    /// so it takes the site's stylesheet as it stands and costs no images. Only the placement
    /// field of the key is needed — it is the first of the four.
    /// </summary>
    private static string Board(string key, string name)
    {
        string placement = key.Split(' ')[0];
        var squares = new char[64];
        int i = 0;
        foreach (char c in placement)
        {
            if (c == '/') continue;
            if (c is >= '1' and <= '8') { i += c - '0'; continue; }
            if (i < 64) squares[i++] = c;
        }

        var b = new StringBuilder();
        b.Append($"            <div class=\"chessboard\" role=\"img\" aria-label=\"{Html(name)}\">\n");
        for (int s = 0; s < 64; s++)
        {
            int file = s % 8, row = s / 8;
            b.Append($"                <div class=\"square {((file + row) % 2 != 0 ? "dark" : "light")}\">");
            if (row == 7) b.Append($"<span class=\"coordinate file\">{(char)('a' + file)}</span>");
            if (file == 0) b.Append($"<span class=\"coordinate rank\">{8 - row}</span>");
            if (squares[s] != '\0')
                b.Append($"<span class=\"piece {(char.IsUpper(squares[s]) ? "white-piece" : "black-piece")}\">{Glyph(squares[s])}</span>");
            b.Append("</div>\n");
        }
        b.Append("            </div>\n");
        return b.ToString();
    }

    /// <summary>
    /// The same glyphs <c>ChessBin.Web/Pieces.cs</c> renders, keyed by placement character
    /// rather than by the engine's enums — a console tool cannot reference a Blazor
    /// WebAssembly project. <c>OpeningPageTests</c> asserts the two agree on every shipped
    /// board, the same guarantee OpeningDataTests gives the duplicated shard hash.
    /// </summary>
    private static string Glyph(char piece) => piece switch
    {
        'K' => "♔", 'Q' => "♕", 'R' => "♖", 'B' => "♗", 'N' => "♘", 'P' => "♙",
        'k' => "♚", 'q' => "♛", 'r' => "♜", 'b' => "♝", 'n' => "♞", 'p' => "♟",
        _ => "",
    };

    // ── Shared chrome ───────────────────────────────────────────────────────────

    /// <summary>
    /// Every nav item, once. The boot pages used to carry six hand-copied versions of this
    /// between them, which is what drifted. Each item has to render on a single line with
    /// <c>href</c> first and <c>class="active"</c> straight after it, because that is the
    /// shape BootPageTests matches.
    /// </summary>
    private static readonly (string Href, string Label, string Svg)[] Nav =
    [
        ("/", "Play",
            """<polygon points="7,4 20,12 7,20" />"""),
        ("/play-online/", "Online",
            """<circle cx="8" cy="8.4" r="3.1" /><circle cx="16" cy="8.4" r="3.1" /><path d="M2.8 19.4c0-2.7 2.3-4.6 5.2-4.6s5.2 1.9 5.2 4.6" /><path d="M13.4 15.2c.8-.3 1.7-.4 2.6-.4 2.9 0 5.2 1.9 5.2 4.6" />"""),
        ("/puzzle/", "Puzzles",
            """<rect x="3.5" y="5" width="17" height="15" rx="2" /><line x1="3.5" y1="9.5" x2="20.5" y2="9.5" /><line x1="8" y1="2.8" x2="8" y2="6.5" /><line x1="16" y1="2.8" x2="16" y2="6.5" /><circle class="nav-icon-fill" cx="12" cy="15" r="2.1" />"""),
        ("/openings/", "Openings",
            """<path d="M4 5.2h5.4A2.6 2.6 0 0 1 12 7.8v11a2.2 2.2 0 0 0-2.2-2.2H4z" /><path d="M20 5.2h-5.4A2.6 2.6 0 0 0 12 7.8v11a2.2 2.2 0 0 1 2.2-2.2H20z" />"""),
        ("/review/", "Analyse",
            """<circle cx="10.8" cy="10.8" r="6.6" /><line x1="15.6" y1="15.6" x2="20.5" y2="20.5" /><polyline points="8,12.2 10.3,9.4 12.4,11.4 14.2,8.6" />"""),
        ("/vote/", "Vote chess",
            """<rect x="3.6" y="4.6" width="16.8" height="14.8" rx="2" /><polyline points="7.8,12.2 10.8,15.2 16.4,8.8" />"""),
    ];

    private static string BootStatus() =>
        """
        <div class="boot-status" role="status">
            <svg class="loading-progress" viewBox="0 0 34 34" aria-hidden="true">
                <circle r="13" cx="17" cy="17" />
                <circle r="13" cx="17" cy="17" />
            </svg>
            <span class="loading-progress-text"></span>
        </div>

        """;

    private static string Render(
        string comment, string description, string title, string route,
        string ogTitle, string ogDescription, string ogImage, string ogImageAlt,
        string jsonLd, string active, string body)
    {
        var nav = new StringBuilder();
        nav.Append("            <nav class=\"site-nav\" aria-label=\"Sections\">\n");
        nav.Append("                <a class=\"nav-brand\" href=\"/\" aria-label=\"ChessBin home\">\n");
        nav.Append("                    <span class=\"brand-mark\" aria-hidden=\"true\">\n");
        nav.Append("                        <img src=\"brand/chessbin-mark-72.png\" width=\"72\" height=\"59\" alt=\"\" />\n");
        nav.Append("                    </span>\n");
        nav.Append("                    <span class=\"nav-wordmark\">ChessBin</span>\n");
        nav.Append("                </a>\n");
        nav.Append("                <ul class=\"nav-list\">\n");
        foreach ((string href, string label, string svg) in Nav)
        {
            string cls = href == active ? " class=\"active\"" : "";
            nav.Append($"                    <li><a href=\"{href}\"{cls}><svg class=\"nav-icon\" viewBox=\"0 0 24 24\" aria-hidden=\"true\">{svg}</svg><span>{label}</span></a></li>\n");
        }
        nav.Append("                </ul>\n");
        nav.Append("            </nav>\n");

        return $$"""
            <!DOCTYPE html>
            <html lang="en">

            <!--
            {{comment}}
            -->

            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <meta name="description" content="{{description}}" />
                <meta name="theme-color" content="#0d0f13" />
                <title>{{title}}</title>
                <base href="/" />
                <link rel="canonical" href="{{Site}}{{route}}" />
                <link rel="preload" id="webassembly" />
                <link rel="stylesheet" href="css/app.css" />
                <link rel="icon" href="favicon.ico" sizes="16x16 32x32 48x48" />
                <link rel="apple-touch-icon" href="brand/apple-touch-icon.png" />
                <link rel="manifest" href="site.webmanifest" />
                <meta property="og:type" content="website" />
                <meta property="og:site_name" content="ChessBin" />
                <meta property="og:title" content="{{ogTitle}}" />
                <meta property="og:description" content="{{ogDescription}}" />
                <meta property="og:url" content="{{Site}}{{route}}" />
                <meta property="og:image" content="{{Site}}/{{ogImage}}" />
                <meta property="og:image:width" content="1200" />
                <meta property="og:image:height" content="630" />
                <meta property="og:image:alt" content="{{ogImageAlt}}" />
                <meta name="twitter:card" content="summary_large_image" />
                <link href="ChessBin.Web.styles.css" rel="stylesheet" />
                <script type="importmap"></script>
                <script type="application/ld+json">
            {{jsonLd}}
                </script>
            </head>

            <body>
                <div id="app">
                    <div class="app-shell">
            {{nav.ToString().TrimEnd('\n')}}
                        <div class="app-main">
                            <a class="mobile-brand" href="/" aria-label="ChessBin home">
                                <span class="brand-mark" aria-hidden="true">
                                    <img src="brand/chessbin-mark-72.png" width="72" height="59" alt="" />
                                </span>
                                <span class="nav-wordmark">ChessBin</span>
                            </a>
                            <main class="page">
            {{Indent(body, 20)}}
                            </main>
                        </div>
                    </div>
                </div>
                <div id="blazor-error-ui">
                    ChessBin hit an unexpected error.
                    <a href="." class="reload">Reload</a>
                    <span class="dismiss">&#128473;</span>
                </div>
                <script src="js/chessbin.js"></script>
                <script src="_framework/blazor.webassembly#[.{fingerprint}].js"></script>
            </body>

            </html>

            """;
    }

    // ── Sitemap ─────────────────────────────────────────────────────────────────

    private static readonly (string Route, string Freq, string Priority)[] AppRoutes =
    [
        ("/", "monthly", "1.0"),
        ("/play-online/", "monthly", "0.9"),
        ("/puzzle/", "daily", "0.9"),
        ("/puzzle/practice/", "monthly", "0.7"),
        ("/openings/", "monthly", "0.8"),
        ("/review/", "monthly", "0.8"),
        ("/vote/", "daily", "0.8"),
    ];

    private static void WriteSitemap(Line[] chosen, string webRoot)
    {
        var b = new StringBuilder();
        b.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        b.Append("<!-- Generated by tools/OpeningPages. The app renders no crawlable links of its own,\n");
        b.Append("     so this and the links between the opening pages are how the set gets discovered. -->\n");
        b.Append("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");

        foreach ((string route, string freq, string priority) in AppRoutes)
            b.Append($"  <url>\n    <loc>{Site}{route}</loc>\n    <changefreq>{freq}</changefreq>\n    <priority>{priority}</priority>\n  </url>\n");

        foreach (Line line in chosen)
            b.Append($"  <url>\n    <loc>{Site}/openings/{line.G}/</loc>\n    <changefreq>monthly</changefreq>\n    <priority>{(line.N.Contains(':') ? "0.5" : "0.6")}</priority>\n  </url>\n");

        b.Append("</urlset>\n");
        Write(Path.Combine(webRoot, "sitemap.xml"), b.ToString());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Body content is written at column zero throughout, so nothing in it has to know how
    /// deep the chrome nests it. This puts it where <c>&lt;main class="page"&gt;</c> wants it.
    /// </summary>
    private static string Indent(string block, int columns)
    {
        string pad = new(' ', columns);
        return string.Join("\n", block.TrimEnd('\n').Split('\n')
            .Select(l => l.Length == 0 ? "" : pad + l));
    }

    /// <summary>
    /// "Sicilian Defense: Najdorf Variation" under "Sicilian Defense" reads as just "Najdorf
    /// Variation" — the parent's name is already the heading above the list.
    /// </summary>
    private static string ShortName(string name, string parent) =>
        name.StartsWith(parent, StringComparison.Ordinal) && name.Length > parent.Length
            ? name[parent.Length..].TrimStart(':', ',', ' ')
            : name;

    private static string Continuations(int n) => n switch
    {
        0 => "no named continuation",
        1 => "1 named continuation",
        _ => $"{n} named continuations",
    };

    private static string Family(string name) => name.Split(':')[0];

    /// <summary>
    /// The line this one is a variation of: drop the last comma-separated qualifier, then the
    /// variation altogether, and take the first of those the tables actually name.
    /// </summary>
    private static Line? Parent(Line line, Dictionary<string, Line> byName)
    {
        string name = line.N;
        while (true)
        {
            int cut = name.LastIndexOf(',');
            if (cut < 0) cut = name.LastIndexOf(':');
            if (cut < 0) return null;

            name = name[..cut].TrimEnd();
            if (byName.TryGetValue(name, out Line? found) && found.N != line.N) return found;
        }
    }

    private static string MoveText(string[] sans)
    {
        var b = new StringBuilder();
        for (int i = 0; i < sans.Length; i++)
        {
            if (i % 2 == 0) b.Append($"{(i > 0 ? " " : "")}{i / 2 + 1}.");
            else b.Append(' ');
            b.Append(sans[i]);
        }
        return b.ToString();
    }

    private static char SideToMove(string key)
    {
        string[] f = key.Split(' ');
        return f.Length > 1 && f[1].Length > 0 ? f[1][0] : 'w';
    }

    private static string Clamp(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    /// <summary>Opening names carry apostrophes and the odd ampersand; attributes carry quotes.</summary>
    private static string Html(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&#39;");

    private static string Json(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Breadcrumb(Line line, string family, Dictionary<string, Line> byName)
    {
        var b = new StringBuilder();
        b.Append("    {\n      \"@context\": \"https://schema.org\",\n      \"@type\": \"BreadcrumbList\",\n      \"itemListElement\": [\n");
        b.Append($"        {{ \"@type\": \"ListItem\", \"position\": 1, \"name\": \"Openings\", \"item\": \"{Site}/openings/\" }},\n");

        int position = 2;
        if (family != line.N && byName.TryGetValue(family, out Line? f))
        {
            b.Append($"        {{ \"@type\": \"ListItem\", \"position\": {position++}, \"name\": \"{Json(family)}\", \"item\": \"{Site}/openings/{f.G}/\" }},\n");
        }

        b.Append($"        {{ \"@type\": \"ListItem\", \"position\": {position}, \"name\": \"{Json(line.N)}\", \"item\": \"{Site}/openings/{line.G}/\" }}\n");
        b.Append("      ]\n    }");
        return b.ToString();
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static T[] Load<T>(string dir, string pattern)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return [.. Directory.EnumerateFiles(dir, pattern)
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(f => JsonSerializer.Deserialize<T[]>(File.ReadAllText(f), options) ?? [])];
    }

    // ── Types ───────────────────────────────────────────────────────────────────

    private sealed record Line(
        [property: JsonPropertyName("g")] string G,
        [property: JsonPropertyName("n")] string N,
        [property: JsonPropertyName("e")] string E,
        [property: JsonPropertyName("p")] string[] P,
        [property: JsonPropertyName("u")] string[] U,
        [property: JsonPropertyName("k")] string K);

    private sealed record Mv(
        [property: JsonPropertyName("s")] string S,
        [property: JsonPropertyName("u")] string U,
        [property: JsonPropertyName("w")] int W,
        [property: JsonPropertyName("n")] string? N,
        [property: JsonPropertyName("e")] string? E);

    private sealed record Pos(
        [property: JsonPropertyName("k")] string K,
        [property: JsonPropertyName("n")] string? N,
        [property: JsonPropertyName("e")] string? E,
        [property: JsonPropertyName("m")] Mv[] M);

    private sealed class Options
    {
        public string WebRoot = "";
        public bool All;

        public static Options? Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{a} needs a value");
                switch (a)
                {
                    case "--out": o.WebRoot = Next(); break;
                    case "--all": o.All = true; break;
                    default:
                        Console.Error.WriteLine($"unknown argument: {a}");
                        return null;
                }
            }

            if (o.WebRoot.Length == 0)
            {
                Console.Error.WriteLine("usage: OpeningPages --out <ChessBin.Web/wwwroot> [--all]");
                Console.Error.WriteLine("       --all drops the curated filter and writes a page for every named line");
                return null;
            }
            return o;
        }
    }
}
