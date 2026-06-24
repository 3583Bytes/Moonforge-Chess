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
- `Engine.Difficulty GameDifficulty` — `Easy` … `VeryHard` (maps to search depth).
- `string FEN` — current position as FEN.
- `GetMoveHistory()` — move list (`MoveContent`, with notation helpers).

## License

MIT © Adam Berent. Source: https://github.com/3583Bytes/ChessCore
