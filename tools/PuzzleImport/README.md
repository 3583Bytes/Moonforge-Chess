# PuzzleImport

Turns the Lichess puzzle database into the sharded static JSON that ChessBin's daily
puzzle reads. Run it by hand when you want to refresh or resize the puzzle set; the
output is committed, so nothing fetches this data at build or run time.

This project is deliberately **not** in `ChessCore.sln`, so it never builds or runs in
the Pages and release workflows.

## Refreshing the puzzle data

```bash
# ~304 MB compressed, roughly 1 GB as CSV
curl -L https://database.lichess.org/lichess_db_puzzle.csv.zst | zstd -dc > /tmp/puzzles.csv

dotnet run --project tools/PuzzleImport -c Release -- \
  --csv /tmp/puzzles.csv \
  --out ChessBin.Web/wwwroot/puzzles
```

Then run the tests — `ChessBin.Web.Tests/PuzzleDataTests.cs` replays every committed
puzzle through the engine, so a bad import fails CI before it can deploy.

Options: `--count` (default 3650, ten years of dailies), `--shard` (128),
`--min-rating` / `--max-rating` (1000–1899), `--max-solver-moves` (3).

## Source and licence

`lichess_db_puzzle.csv` from [database.lichess.org](https://database.lichess.org/), released
under **CC0 1.0** — "use them for research, commercial purpose, publication, anything you
like… without asking for permission". No attribution is required; the `url` field is kept
per puzzle anyway so players can open the game it came from.

## The one thing to know about the source format

Lichess's `FEN` column is the position **before** the opponent's mistake, and `Moves[0]`
is that mistake. The solver's first move is `Moves[1]`.

The importer resolves this at build time: it applies `Moves[0]`, stores the resulting
position as `fen`, keeps the played move as `lastMove` for board highlighting, and stores
the remainder as `solution`. So at run time the solver's moves are simply the **even
indices** of `solution`, with the opponent's replies at the odd indices. No shifting
needed in the UI.

## Output layout

```
wwwroot/puzzles/manifest.json     count, shardSize, shards, ratingRange, source, licence
wwwroot/puzzles/shard-000.json    one JSON array per shard, one puzzle per line
```

One puzzle per line keeps git diffs readable when the set is regenerated. Reruns on the
same input are byte-identical — there is no timestamp in the output, and candidates are
sorted by puzzle id — so a regeneration that changes nothing shows no diff.

## Selection

Kept only if the rating sits in range, `RatingDeviation ≤ 90` (rating has settled),
`Popularity ≥ 90` (players liked it), `NbPlays ≥ 1000` (well tested), and the solver has
1–3 moves to find. Survivors are bucketed into 100-point rating bands and drawn
round-robin, so consecutive days step through easy-to-hard rather than shipping every
easy puzzle first.

Every emitted puzzle has been replayed through `Engine`, and any puzzle themed `mateIn*`
must actually end in checkmate according to Moonforge or it is dropped.

## Known limitation

Because the daily index walks the array in order, one fetched shard contains the next
~128 days of puzzles. Anyone reading network traffic can see upcoming puzzles — the same
property early Wordle had. Fine at this scale; if it ever matters, shuffle the emitted
order with a committed seed so shard contents no longer sit next to each other in time.
