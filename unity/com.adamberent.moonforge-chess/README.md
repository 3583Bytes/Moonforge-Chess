# Moonforge Chess Engine (Unity)

A bitboard chess engine as a Unity package: alpha-beta search with a transposition
table, null-move / late-move pruning, quiescence, and positional evaluation, behind
a small `Engine` API. Self-contained (code-generated opening book, no external
dependencies). Ships as a .NET Standard 2.1 managed plugin.

**Requires Unity 2021.2+** (the first version whose scripting runtime supports
.NET Standard 2.1).

## Install

**From disk:** Window → Package Manager → **+** → *Add package from disk…* → select
`unity/com.adamberent.moonforge-chess/package.json`.

**From git:** Package Manager → **+** → *Add package from git URL…* →
`https://github.com/3583Bytes/ChessCore.git?path=unity/com.adamberent.moonforge-chess`

The DLL under `Runtime/` is imported automatically as a managed plugin; Unity
generates its `.meta` on first import.

## Usage

```csharp
using UnityEngine;
using ChessEngine.Engine;

public class ChessController : MonoBehaviour
{
    private Engine _engine;

    void Start()
    {
        _engine = new Engine("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
    }

    // Call when the human has made a move in long algebraic notation, e.g. "e2e4".
    public void PlayHumanThenEngine(string humanMove)
    {
        if (!_engine.IsValidMoveAN(humanMove)) return;
        _engine.MovePieceAN(humanMove);

        _engine.GameDifficulty = Engine.Difficulty.Hard;
        _engine.AiPonderMove();          // searches and plays the engine's reply

        Debug.Log("Position: " + _engine.FEN);
    }
}
```

> `AiPonderMove()` runs the search synchronously. For higher difficulties, call it
> from a background thread (or a coroutine/job) to avoid stalling the main thread,
> then apply the result on the main thread.

## License

MIT © Adam Berent. Source & full engine: https://github.com/3583Bytes/ChessCore
