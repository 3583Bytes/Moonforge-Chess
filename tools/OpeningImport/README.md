# OpeningImport

Turns the Lichess `chess-openings` tables into the sharded static JSON that ChessBin's
opening explorer reads. Run it by hand when you want to refresh the set; the output is
committed, so nothing fetches this data at build or run time.

This project is deliberately **not** in `ChessCore.sln`, so it never builds or runs in
the Pages and release workflows.

## Refreshing the opening data

```bash
mkdir -p /tmp/eco && cd /tmp/eco
for f in a b c d e; do curl -sLO "https://raw.githubusercontent.com/lichess-org/chess-openings/master/$f.tsv"; done

cd -   # back to the repo root
dotnet run --project tools/OpeningImport -c Release -- \
  --tsv /tmp/eco \
  --out ChessBin.Web/wwwroot/openings
```

Then run the tests — `ChessBin.Web.Tests/OpeningDataTests.cs` replays every committed move
through the engine and checks the sharding, so a bad import fails CI before it can deploy.

Then regenerate the static pages too — `tools/OpeningPages` is built on this tool's output:

```bash
dotnet run --project tools/OpeningPages -c Release -- --out ChessBin.Web/wwwroot
```

Options: `--book` (default `ChessCoreEngine/Book.cs`), `--shards` (64), `--line-shards` (64).

## Source and licence

`a.tsv`–`e.tsv` from [lichess-org/chess-openings](https://github.com/lichess-org/chess-openings),
released under **CC0 1.0**. Three columns: ECO code, name, and the line in SAN. About 3,810
named openings.

Popularity comes from a second source: the engine's own opening book (`ChessCoreEngine/Book.cs`),
which carries real frequency counts (`e2e4{3820}`). It is parsed as **text** rather than
referenced, because `OpeningMove` is internal to the engine and this is the only consumer that
wants the raw counts.

## Why no win/draw/loss

The obvious source is the Lichess opening explorer API, which would give results by rating
band as well as popularity. It answers `401` without credentials, so it is deliberately not a
dependency here. If that changes, the shape to add is a `wdl` field per move — the page already
treats popularity as optional (`weight: 0` renders as "rare"), so the same slot takes results.

## Output layout

```
wwwroot/openings/manifest.json          positions, named, lines, shards, and how sharding works
wwwroot/openings/shard-000.json         one JSON array per shard, one position per line
wwwroot/openings/lines/manifest.json    lines, shards, and the slug rule
wwwroot/openings/lines/shard-00.json    one JSON array per shard, one named opening per line
```

Positions are keyed by the **first four FEN fields** — placement, side to move, castling,
en passant — so two move orders that reach the same position share one entry and
transpositions resolve on their own. The halfmove and fullmove counters are dropped.

A shard is chosen by `FNV-1a 32-bit(key) % shards`, so a lookup knows which single file to
fetch without an index. That hash exists twice: here, and in `ChessBin.Web/Openings.cs`, because
a console tool cannot reference a Blazor WebAssembly project. `OpeningDataTests` asserts the two
agree on every key that shipped — a stronger guarantee than sharing the code would give.

One position per line keeps git diffs readable. Reruns on the same input are byte-identical —
there is no timestamp in the output and positions are sorted ordinally within each shard — so a
regeneration that changes nothing shows no diff.

## What gets emitted

Every named line is replayed through `Engine` and dropped if it will not play, so a move that
ships is a move that provably works. Each position records the moves leaving it, each with the
name and ECO code of the position it *leads to* — denormalised on purpose, so the explorer can
label the whole list without a second fetch per row.

Moves are ordered most-played first, then by notation, so the UI does not have to sort.

## The line index

`lines/` is a second, smaller output keyed by opening *name* rather than by position: the slug
its page lives at, the ECO code, the move order in both notations, and the position the line
ends on. Sharded the same way by `FNV-1a 32-bit(slug) % shards`, so a deep link fetches one
~17 KB file rather than a 980 KB table.

It exists because the move order an opening is known by **cannot be recovered from the position
graph**. Transpositions merge move orders there, so the shortest path to a named position is
frequently not its line — walking the graph reaches *Queen's Gambit Declined* via
`1.d4 Nf6 2.c4 e6 3.Nf3 d5` rather than `1.d4 d5 2.c4 e6`, and 1,302 of the 3,810 named
positions have more than one shortest path. The tables' `pgn` column is the only canonical
answer, and it used to be read and thrown away.

Where a name appears on more than one row the first wins, the same rule the position's name
follows, and rows are read in ECO order a→e, so the choice is stable across runs. 3,815 rows
carry 3,174 distinct names, so that is how many entries ship.

Two consumers read it: `ChessBin.Web/Pages/Openings.razor`, which replays a line when someone
opens `/openings/<slug>`, and `tools/OpeningPages`, which generates that page in the first
place.
