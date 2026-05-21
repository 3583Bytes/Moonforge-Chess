# Performance baseline

Reference numbers for detecting search-performance regressions. Update this file
whenever you intentionally change something that moves the totals.

## How to reproduce

Always build in Release. Debug is 5–10× slower and useless for measurement.

```powershell
dotnet build ChessCore.sln -c Release -nologo
1..5 | ForEach-Object {
    "bench`nquit" | dotnet run -c Release --no-build --project ChessCore
} | Tee-Object bench.log
```

Take the median of the **total** line across the runs. Single runs lie because of
JIT warmup and OS scheduling jitter; the median of five is stable enough.

## Current baseline

Captured on .NET 10.0.7 / Windows 11 x64 after adding king-zone-attack eval
and quiescence knight-check extension on top of `6c6b81a`:

- `Evaluation.cs`: added `EvaluateKingZoneAttacks`. For each king, count enemy
  attacks on the 8 surrounding squares using the existing `*AttackBoard`, then
  apply convex penalty `{0,-4,-12,-24,-40,-60,-80,-100}[count]` so the 3rd+
  attacker hurts disproportionately. Skipped in endgame (king activity wanted).
  Replaced the prior 64-square king-finding loop in the king-safety block with
  direct `Board.{White,Black}KingPosition` reads.
- `Search.cs`: quiescence now considers non-capture *knight* checks at the
  first qsearch ply only. New focused generator `EvaluateMovesQPlusKnightChecks`
  emits captures + non-capture knight moves landing on `KnightMoves[enemyKingPos]`
  (the squares from which a knight checks the enemy king). At `qsPly >= 1`
  qsearch stays captures-only. Slider/pawn checks excluded — cheap detection
  needs blocker walks and isn't worth the per-node cost yet.

```
total nodes  737_661    (deterministic across runs)
total time   ~7_600 ms  (median; range 6.8–9.2 s — variance dominated by OS jitter)
total NPS    ~97_000    (median)
```

Per-position breakdown from one representative run:

```
Start         depth 0 nodes      0 time   27ms nps        0  (book hit)
Kiwipete      depth 5 nodes 424726 time 4264ms nps    99607
Endgame       depth 6 nodes  38825 time  134ms nps   289738
BK.01         depth 5 nodes 270743 time 3201ms nps    84580
KRk endgame   depth 7 nodes    981 time    1ms (sub-ms — discard NPS)
Promotion     depth 7 nodes   2386 time    4ms (sub-ms — discard NPS)
```

Compared to the prior baseline (792_281 nodes / 3_300 ms / 243K NPS):

- **Nodes -7%** (737K vs 792K). King-safety eval prunes better, especially in
  BK.01 (-22%) where king-attack patterns are common.
- **Time +130%** (7.6 s vs 3.3 s). Per-node cost roughly doubled.
- **NPS -60%** (97K vs 243K). The trade is per-node accuracy for raw speed.

Per-node cost increase comes from, in order of impact:

1. Quiescence at `qsPly==0` now searches captures *plus* non-capture knight
   checks. Even with the focused generator the move list is bigger and each
   qsearch node costs more.
2. `EvaluateKingZoneAttacks` adds 16 attack-board lookups × 2 kings per eval.
   Small in isolation, but eval is called once per node.

Strength impact (vs the 0-10 baseline against Rybka@2000) is measured separately
— see `_match_rybka2000.log` after running `_run_match_rybka_2000.ps1`.

### Null move pruning (added on top of king-safety + qsearch knight checks)

`Search.AlphaBeta` now does a null move at internal nodes: hand the opponent a
free move and search with reduction `R=2` (so `depth - 1 - R` plies) using a
zero-window around `beta`. If the result still beats `beta`, return `beta` —
the opponent can't refute this position. Skipped when in check, when
`piecesRemaining ≤ 6` (zugzwang risk in pawn endgames), when the caller was
itself a null move (no double-null), and when `|beta| ≥ 30000` (unreliable
against mate scores and against the infinity bound at root).

Bench at fixed depth 5 barely moves (NMP only helps significantly at depth ≥ 8
where the saved subtrees are larger). But at the depths real moves are searched
in a match (depth 6, with iterative-deepening extensions), node reductions of
**17–25%** show up in actual positions:

| Position | Before NMP | With NMP | Δ |
|---|---|---|---|
| Game 1 critical (Nf3+ fork, depth 6) | 1,697K | 1,414K | -17% |
| Game 10 critical (Bxc2 line, depth 6) | 2,945K | 2,210K | -25% |

### Transposition table (added on top of NMP)

New `Zobrist.cs` (random per-(color,piece,square) tables + side-to-move + castling
+ en-passant file; deterministic seed). Hash recomputed from scratch at the end
of `Board.MovePiece` — cheap (~64 reads + XORs) and avoids the long tail of
incremental-update bugs around castling/promotion/EP captures. The `ZobristHash`
field on `Board` was previously declared but never populated; the `Zobrist` class
referenced in FileIO.cs was inside a `/* */` block and never compiled.

New `TranspositionTable.cs` (2²⁰ = ~1M-entry array, 24 MB). At each `AlphaBeta`
entry the table is probed: if a prior search at sufficient depth has a bound
that satisfies the current α/β window, return immediately. The stored best
move is captured and swapped to the front of the move list — this is where most
of the search-tree shrinkage actually comes from. At each `AlphaBeta` exit the
result is stored with the appropriate bound flag (`Exact` / `Lower` / `Upper`).

Two refinements were needed before TT actually beat baseline:

1. **Depth-preferred replacement.** A flood of depth-1 entries from the
   root-level mate-check loop in `IterativeSearch` was wiping out deep cuts
   under the always-replace policy. `Store` now refuses to overwrite a strictly
   deeper entry unless it's the same key.
2. **Move-hint depth gate.** A move stored from a depth-1 search is often a
   worse ordering hint than the static capture+killer scoring. `Probe` now only
   returns `bestSrc`/`bestDst` when the stored entry's depth is ≥ 2.

Without (1) and (2), Kiwipete actually regressed by +27% nodes. With them in
place, Kiwipete drops from 408K → 284K (-30%) and total bench from 741K → 527K
(-29% nodes, -21% time). Same per-position depth, same engine moves.

Mate scores deliberately not stored — they encode depth-from-root and would
be wrong when retrieved from a different position. Small strength loss on
mate puzzles; saves needing to thread a plies-from-root counter through the
whole search.

| Position | Before TT | With TT | Δ |
|---|---|---|---|
| Game 1 (Nf3+, depth 6) | 1,414K | 1,273K | -10% |
| Game 10 (Bxc2 line, depth 6) | 2,210K | 1,628K | -26% |
| Kiwipete bench (depth 5) | 405K | 284K | -30% |

> **Note**: per-position numbers are useful for spotting regressions in a *single*
> position type (e.g. endgames), but only the **total** is stable enough to gate
> changes against. Per-position depth varies because iterative deepening's
> `ModifyDepth` boosts depth in low-piece-count or low-mobility positions.

### Prior baselines

| Commit / state | Total nodes | Total time (median) |
|---|---|---|
| Post-search-fixes (`7ce9e74`) | 999,890 | ~9.5 s |
| Post-eval-fixes (`cdbe753`) | 920,896 | ~6.7 s |
| Post-eval-cleanup + book fixes (`6c6b81a`) | 792,281 | ~3.3 s |
| King-safety + qsearch knight checks | 737,661 | ~7.6 s |
| + null move pruning | 741,618 | ~7.2 s |
| + transposition table (current) | 527,110 | ~5.7 s |

The biggest chunk of the latest drop (~132K nodes) is the `Start` bench position
becoming a book hit instead of a depth-6 search — measured search performance on
the *other* positions actually moved by only a few thousand nodes total
(BK.01 +858, Kiwipete +2667, Endgame −35). The time-budget improvement (~6.7s →
~3.3s) reflects both the missing Start search and the per-eval allocation cleanup
removing `new short[8]` × 2 from the hot path.

Bench measures node count and raw speed, **not playing strength**: confirm with
a head-to-head Cute Chess match before treating the change as a strength gain.

## How to interpret comparisons

After a change, re-run the procedure above and compare to the baseline.

| What moved | Likely meaning |
|---|---|
| Total nodes ↑, time same | Search tree grew (worse move ordering, weaker pruning) — *bad* |
| Total nodes ↓, time same | Search tree shrank (better move ordering, stronger pruning) — *good* |
| Total nodes same, time ↑ | Raw per-node work got more expensive (allocations, codegen) — *bad* |
| Total nodes same, time ↓ | Raw per-node work got cheaper — *good* |
| Both ↑ together | Could be either: more nodes visited, AND each more expensive |
| Both ↓ together | Pure win |

Total-node changes that come with **deeper `depth` reached** in the per-position
breakdown are usually fine — iterative deepening just chose to search one ply
further. Look at `nodes / depth_reached` if you want depth-normalized comparison.

## What `bench` does *not* measure

- **Playing strength.** Two engines can hit identical bench numbers and yet one
  wins 70% of games. For real strength changes, run a head-to-head match in
  [Cute Chess](https://cutechess.com/) tournament mode (Tools → New Tournament).
  A few hundred games at a fixed short time control (60s + 0.6s increment is a
  reasonable starting point) gives a usable Elo delta.
- **Move-generation speed.** Bench mixes search + evaluation + quiescence + movegen.
  For movegen-only timing, time the perft tests:
  ```powershell
  dotnet test ChessCore.sln -c Release --filter "FullyQualifiedName~PerftBaselineTests"
  ```
  Depth-5 perft is 4,865,609 nodes of pure move generation.
- **Time-control behaviour.** Bench searches to a fixed depth, never to a time
  budget. If you change the wtime/btime → depth mapping in `UciProtocol.PickDepth`,
  bench won't see it. Test with `go movetime 3000` from a few positions instead.
