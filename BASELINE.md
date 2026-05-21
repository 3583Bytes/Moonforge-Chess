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
| King-safety + qsearch knight checks (current) | 737,661 | ~7.6 s |

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
