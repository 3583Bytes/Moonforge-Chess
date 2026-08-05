# Moonforge Chess Engine

A bitboard chess engine as an embeddable .NET library. Alpha-beta search with a
transposition table, null-move / late-move pruning, quiescence search, and a
positional evaluation — wrapped behind a small `Engine` API. Self-contained: the
opening book is code-generated, and there are no external dependencies.

Targets `net10.0` and `netstandard2.1` (works in .NET apps and Unity 2021.2+).

## Install

```bash
dotnet add package MoonforgeChess.Engine
```

## Quick start

```csharp
using ChessEngine.Engine;

// New game from the standard start position (or pass any FEN).
var engine = new Engine("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");

// Play a human move in long algebraic notation.
engine.MovePieceAN("e2e4");

// Pick search strength, then let the engine reply.
engine.GameDifficulty = Engine.Difficulty.Hard;
engine.AiPonderMove();

// Inspect the resulting position.
Console.WriteLine(engine.FEN);
foreach (var m in engine.GetMoveHistory())
    Console.WriteLine(m.GetPureCoordinateNotation());
```

## Useful members

- `new Engine()` / `new Engine(string fen)` — start a game.
- `bool MovePieceAN(string move)` — apply a move like `"e2e4"`, `"e1g1"` (castling), `"e7e8q"` (promotion).
- `bool IsValidMoveAN(string move)` — legality check without playing.
- `void AiPonderMove()` — search and play the engine's move.
- `EngineSearchResult SearchBestMove(...)` — search without changing the board; accepts a cancellation token and an optional callback for each completed iterative-deepening result.
- `Engine.Difficulty GameDifficulty` — `Easy` … `VeryHard` (maps to search depth).
- `string FEN` — current position as FEN.
- `GetMoveHistory()` — move list (`MoveContent`, with notation helpers).

For analysis or GUI integration, use the non-mutating search API:

```csharp
using System.Threading;

engine.PlyDepthSearched = 8;
EngineSearchResult result = engine.SearchBestMove(
    CancellationToken.None,
    info => Console.WriteLine($"depth {info.Depth}: {info.Score} cp"));

Console.WriteLine(result.HasMove ? result.BestMove : "(no legal move)");
```

`EngineSearchInfo.PrincipalVariation` contains the complete predicted line as
space-separated UCI moves. `IsMate` and `MateInMoves` distinguish mate scores from
ordinary centipawn evaluations. `AiPonderMove()` remains available when
search-and-play is the desired operation.

## License

MIT © Adam Berent. Source: https://github.com/3583Bytes/ChessCore
