# OpeningPages

Writes the static pages the site is *read* by: one per named chess opening, the seven boot
pages for the app's own routes, and the sitemap that lists them all. Run it by hand; the
output is committed, so nothing is generated at build time.

This project is deliberately **not** in `ChessCore.sln`, so it never builds or runs in the
Pages and release workflows.

## Regenerating the pages

```bash
dotnet run --project tools/OpeningPages -c Release -- --out ChessBin.Web/wwwroot
```

It reads only what is already committed under `wwwroot/openings`, so unlike `OpeningImport`
it needs no network. Run `OpeningImport` first if the opening data itself has changed —
`wwwroot/openings/lines/` is what this tool is built on, and older imports do not have it.

Then run the tests: `ChessBin.Web.Tests/OpeningPageTests.cs` replays every committed line
through the engine, checks every slug against the reader's own copy of the rule, and fails on
a page that links nowhere, a page nothing links to, or a sitemap that disagrees with what is
on disk.

Options: `--all` writes a page for every named line instead of the curated set.

## Why these pages exist

Crawlers and link previews never execute WebAssembly, so a page's pre-boot markup is the only
thing they ever see. Before this the whole site was seven near-identical shells of about ninety
words each — Google crawled them and declined to index a single one — while the opening data
shipped 3,174 named lines that nothing outside the app could read.

Each page carries the move order an opening is actually known by, the position on a board, its
ECO code, every named continuation as a link, and the line it is a variation of. That is a
genuine reference page rather than a templated stub, which is the difference between being
indexed and sitting in "Crawled – currently not indexed".

## Which lines get a page

By default the **families** (a name with no colon, e.g. `Sicilian Defense`) and the lines the
engine's own opening book covers — as far as mainline theory goes — plus every ancestor of
anything selected, so no page's breadcrumb points at a route that was never written. That is
about 560 pages out of 3,174.

The rest are held back on purpose. A six-week-old site that drops three thousand templated
pages at once looks exactly like the bulk content Google declines to index, and the whole
problem being solved here is not being indexed. `--all` lifts the filter once the first set
is indexing.

Note the tool deletes page directories it no longer writes, so narrowing the selection does not
leave unreachable pages behind.

## The move order comes from the import, not the graph

A page states the moves an opening is reached by, and that cannot be recovered from the
position graph. Transpositions merge move orders there, so the shortest path to a named
position is frequently not the line the opening is known by: walking the graph reaches
*Queen's Gambit Declined* via `1.d4 Nf6 2.c4 e6 3.Nf3 d5`, and 1,302 of the named positions
have more than one shortest path. The tables' own move order is the only canonical one, so
`OpeningImport` captures it into `wwwroot/openings/lines/` and this tool reads it from there.

## Output layout

```
wwwroot/index.html                     the seven app boot pages, from AppPages.cs
wwwroot/{play-online,puzzle,...}/index.html
wwwroot/openings/<slug>/index.html     one per selected opening
wwwroot/sitemap.xml                    every route above
```

A slug is the opening's name, lowercased, with accents folded, apostrophes dropped, and every
other run of characters collapsed to a single dash — `King's Indian Defense` becomes
`kings-indian-defense`. Collision-free across all 3,174 names. That rule exists twice, here and
in `ChessBin.Web/Openings.cs`, because a console tool cannot reference a Blazor WebAssembly
project; `OpeningPageTests` asserts the two agree on every name that shipped.

Re-running on unchanged input rewrites every file byte-identically — there is no timestamp in
the output — so a regeneration that changes nothing shows no diff. **The seven app boot pages
are part of that guarantee**: if the generator's chrome template is right, regenerating them
produces no diff at all, which is the check that it still renders the site's own chrome.

## Two things to know before changing a page

`ChessBin.Web/Pages/Openings.razor` declares `@page "/openings/{Slug}"`. Without it the app
would boot over a pre-rendered page and replace it with the not-found page, so a new URL shape
needs both ends changed together.

`wwwroot/service-worker.published.js` excludes these pages from precaching. There are hundreds
of them, `addAll` is atomic, and one bad hash out of hundreds would reject the install and
leave every visitor with no offline mode.
