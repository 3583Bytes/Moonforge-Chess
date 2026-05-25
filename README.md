# Moonforge Chess

UCI-compatible chess engine written in C# / .NET 10.

Moonforge Chess implements the [Universal Chess Interface](https://backscattering.de/chess/uci/) protocol on stdin/stdout, so it plugs into any modern chess GUI — [Arena](http://www.playwitharena.de/), [Cute Chess](https://cutechess.com/), [BanksiaGUI](https://banksiagui.com/), [Nibbler](https://github.com/rooklift/nibbler), the ChessBase family, [lichess-bot](https://github.com/lichess-bot-devs/lichess-bot), etc. Earlier versions spoke the XBoard/WinBoard protocol; that has been replaced by UCI as of v1.1.

## What's inside the engine

The engine is a deliberately readable implementation — single-threaded, no
bitboards, no machine-learned evaluation. Code is small enough that every
component can be traced end-to-end.

**Search** (`Search.cs`)

- Negamax alpha-beta with iterative deepening at the root
- Killer-move heuristic (two slots per ply) for move ordering
- Check extension at the search horizon
- Null move pruning (R=2), skipped in check / at low depth / in low-piece endings (zugzwang)
- Quiescence search with stand-pat + SEE pruning on captures
- Quiescence also considers non-capture *knight* checks at its first ply — catches the knight-fork class of horizon tactic
- Transposition table (~1M entries, ~24 MB), depth-preferred replacement, TT-move first in ordering
- Mate detection with depth-adjusted scores (shorter mates score higher)

**Evaluation** (`Evaluation.cs`)

- Material + piece-square tables (separate king PST for endgame)
- Pawn structure: chains, doubled, isolated, passed (rank-weighted)
- King safety: pawn-wall shelter, open / half-open file penalty, king-zone attack pressure (convex penalty for multiple attackers in the 3×3 box around the king)
- Bishop pair, knight endgame discount, queen-out-early penalty
- Mobility (valid-move count), hanging-piece double-penalty
- Tempo bonus, castled bonus, lost-castling-rights penalty
- Insufficient-material draw detection (handles KNvKN, KBvKB)

**Move generation** (`PieceMoves.cs` / `PieceValidMoves.cs`)

- Precomputed direction tables per piece type (built once at engine startup)
- Per-piece valid-move lists + per-color attack boards rebuilt after each board mutation
- Validated by perft from the start position through depth 5 (4,865,609 nodes)

**Position state** (`Board.cs`, `Zobrist.cs`)

- 64-square `Square[]` board with castling / en-passant / clock / king-position state
- 64-bit Zobrist hash recomputed from scratch at the end of every `MovePiece` (cheaper than threading incremental updates through every special-case path)
- 50-move rule, 3-fold repetition

**Time controls and opening book**

- `go wtime/btime/winc/binc/movestogo` is mapped to a search ply at the start of the search; the engine does not poll a deadline mid-search
- Bundled opening book (~thousands of positions) consulted before search; book hits return in microseconds

For reproducible search-performance numbers and a record of which changes
moved them, see [`BASELINE.md`](BASELINE.md).

## Download a prebuilt binary

Each tagged release on the [Releases page](https://github.com/3583Bytes/ChessCore/releases) ships with prebuilt single-file executables for Windows, Linux, and macOS (both Intel and Apple Silicon). Two flavors per platform:

- **`moonforge-chess-<platform>-self-contained.{zip,tar.gz}`** — bundles the .NET 10 runtime inside the binary (~60–70 MB). Nothing else needed on the target machine; download, extract, run. The archive contains a single executable named `moonforge-chess` (`moonforge-chess.exe` on Windows).
- **`moonforge-chess-<platform>-framework-dependent.{zip,tar.gz}`** — small (~5 MB) but requires the .NET 10 runtime already installed on the target machine. Use this if disk space matters or you already have .NET.

A `SHA256SUMS.txt` is published alongside each release so you can verify downloads.

> macOS users: binaries are not Apple-codesigned (no Developer ID). On first launch, right-click the file and choose **Open** to bypass Gatekeeper.

## Quick start (build from source)

```powershell
dotnet build ChessCore.sln -nologo
dotnet run --project ChessCore
```

You should see no output at startup — the engine waits silently for the GUI to speak first. Type `uci` and press enter; the engine will reply with its identification and `uciok`.

A minimal sanity check from the shell:

```powershell
"uci`nposition startpos moves e2e4`ngo depth 4`nquit" | dotnet run --project ChessCore
```

Expected output: an `id name` / `id author` / `uciok` block, an `info` line, then `bestmove <coords>`.

## Playing the engine in a GUI

For real play you want a graphical board, a clock, and move-list panes. Moonforge Chess is a standard UCI engine, so any UCI-compatible chess GUI will host it. Two free, well-supported options:

- **[Arena](http://www.playwitharena.de/)** — Windows-first, beginner-friendly. Recommended if you're on Windows.
- **[Cute Chess](https://cutechess.com/)** — cross-platform (Windows / macOS / Linux), good for engine-vs-engine matches.

The setup is the same idea in either: publish Moonforge Chess as a single executable, then point the GUI at it.

### Step 1 — Publish a single-file engine binary

From the repository root:

```powershell
# Windows
dotnet publish ChessCore -c Release -r win-x64   --self-contained -p:PublishSingleFile=true

# Linux
dotnet publish ChessCore -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true

# macOS (Apple Silicon: use osx-arm64)
dotnet publish ChessCore -c Release -r osx-x64   --self-contained -p:PublishSingleFile=true
```

The resulting binary lives at:

```
ChessCore/bin/Release/net10.0/<rid>/publish/ChessCore[.exe]
```

(`<rid>` is `win-x64`, `linux-x64`, `osx-x64`, etc.) Copy this file somewhere stable — your `~/Engines/` folder or `C:\Engines\Moonforge\`, say — because the GUI will remember its path.

> Why publish instead of `dotnet run`? GUIs invoke the engine path many times per second and expect a single executable they can spawn quickly. `dotnet run` rebuilds and has noisy first-line output; published binaries start clean and fast.

### Step 2a — Hooking it into Arena

1. Open Arena. From the menu bar, choose **Engines → Install New Engine…**.
2. In the file picker, navigate to `ChessCore.exe` (the binary produced by `dotnet publish` — releases downloaded from GitHub are named `moonforge-chess.exe`) and open it.
3. Arena asks **"Choose the type of the chess engine"** — select **UCI**.
4. Arena will spawn the engine briefly to read its `id` / `option` info, then add it to the engine list.
5. To play a game against it: **Engines → Manage…** → highlight Moonforge Chess → click **Player B** (or **A**) to assign it a side. Close the dialog, then **Game → New** to start.
6. To watch what the engine is doing while it thinks, open **View → Output (Engine 1/2)** — you'll see the `info depth … score cp … bestmove …` stream live.

### Step 2b — Hooking it into Cute Chess

1. Open Cute Chess. **Tools → Settings… → Engines** tab.
2. Click **Add…**.
3. Fill in:
    - **Name**: `Moonforge Chess`
    - **Command**: full path to the published binary (e.g. `C:\Engines\Moonforge\ChessCore.exe` or `/home/you/engines/ChessCore`; a release download would be `moonforge-chess.exe` / `moonforge-chess`)
    - **Working Directory**: same folder as the binary
    - **Protocol**: `UCI`
4. Click **OK** to save, then **OK** to close Settings.
5. **Game → New…** → set one side to **CPU** and pick `Moonforge Chess` from the engine dropdown. Click **OK** to start.

### Step 3 — Confirm it's working

When the engine is on the move, you should see:

- A clock tick / spinner in the GUI.
- An engine-output / log pane filling up with `info depth … score cp … nodes … bestmove …` lines.
- After a second or so (depending on time control), the GUI plays the engine's move on the board.

If nothing happens for more than a few seconds and no `info` lines appear, the GUI likely can't spawn the engine — double-check the path and that the file is executable (`chmod +x ChessCore` on Linux/macOS).

### Time controls and difficulty

Pick a time control in the GUI's "New Game" dialog rather than configuring depth here. Moonforge Chess reads `wtime` / `btime` / `winc` / `binc` from `go` and maps the remaining budget to a search depth (3–7 plies). Reasonable starting points:

- **Casual game**: 5 minutes per side with a 3-second increment.
- **Quick test**: 1 minute per side, no increment — you'll see Moonforge Chess at depth 3–4.
- **Engine-vs-engine match**: pair it against another lightweight engine in Cute Chess's tournament mode (**Tools → New Tournament…**).

### Known interaction quirks

- **"Stop" isn't honored mid-search.** Moonforge Chess runs search synchronously, so if you hit "Force Move" or "Move Now" in the GUI, the engine will finish its current iteration before returning. For short time controls this is usually invisible.
- **No `Hash` or `Threads` options.** Moonforge Chess advertises no UCI options at startup, so the engine-options pane in the GUI will be empty. There's nothing to tune.
- **No pondering.** The engine ignores `ponderhit` and `go ponder`. If your GUI has "Engine Pondering" enabled, leave it off — it won't hurt but it won't help either.

## Debug commands at the terminal

When running the engine by hand (no GUI attached), a few non-standard commands are useful. Their output is emitted as `info string` lines so GUIs silently ignore them — they stay UCI-compliant.

| Command | What it does |
|---|---|
| `show`, `d` | Render the current position as an ASCII board, plus FEN and side-to-move. |
| `fen` | Print just the FEN. |
| `eval` | Print the static evaluation (material + piece-square tables) from both White's and side-to-move's perspectives. |
| `bench` | Run a fixed search benchmark over six well-known test positions at depth 5 and report total nodes / NPS / time. Use this to spot performance regressions. |
| `flip` | Mirror the board top-to-bottom and swap every piece's color. Mostly useful for verifying evaluation symmetry. |
| `compiler` | Print the .NET runtime version, framework, OS, and process architecture. |
| `<move>` | A bare move like `e2e4` or `e7e8q` is applied to the engine's current board state without needing a full `position` command. This is a Moonforge Chess convenience and is not part of standard UCI; no GUI sends bare moves, so it only fires at a terminal. |

## Playing the engine manually from a terminal

You don't need a GUI to play. Start the engine with `dotnet run --project ChessCore` and type at it. The conversation is short: type a move, ask the engine for its reply, repeat.

### Move syntax

Moonforge Chess expects long algebraic notation — no piece letters, no `x`, no `+`, no `#`. Just source square + destination square.

| Move | What it looks like |
|---|---|
| Pawn / piece move | `e2e4`, `g1f3` |
| Capture | `e4d5` (same syntax — no `x`) |
| Castling | `e1g1` (kingside), `e1c1` (queenside) — encoded as the king's move |
| En passant | `e5d6` (same syntax as any pawn move) |
| Promotion | `e7e8q`, `e7e8r`, `e7e8b`, `e7e8n` (suffix is lowercase) |

### A worked example: playing as White

Start the engine:

```
dotnet run --project ChessCore
```

It will sit silently waiting for input. Then (your input has no prefix; engine output is prefixed `> `):

```
uci
> id name Moonforge Chess 1.1.0
> id author Adam Berent
> uciok

ucinewgame

e2e4
go depth 6
> info depth 6 score cp -5 nodes 12345 nps 41150 time 300
> bestmove c7c5

g1f3
go depth 6
> info depth 6 score cp 12 nodes 30210 nps 100700 time 300
> bestmove d7d6

show
> info string  ---------------------------------
> info string 8| r | n | b | q | k |   | n | r |
> ... (full board) ...
> info string Side to move: White
```

That's the entire loop: type your move (bare long-algebraic), type `go depth N`, read the engine's reply. The engine maintains its own board state, so you do **not** need to retype the full game history.

For strict UCI clients (or if you just prefer the formal form), the equivalent of `e2e4` is `position startpos moves e2e4`, and to continue you re-send the full move list each turn. That's the only form a real chess GUI uses; bare moves are a Moonforge Chess terminal-only shortcut.

When you're done, type `quit`.

### Tips

- Use `show` (or `d`) any time to print the current board, FEN, and side-to-move. Handy for catching typos.
- Difficulty is controlled by `go depth N`: `depth 3` is very fast and weak, `depth 5` is a reasonable casual opponent, `depth 6–7` is noticeably stronger but slower. `go movetime 5000` gives the engine roughly 5 seconds and picks a depth for you.
- The engine plays book moves instantly with `depth 0` reported — that's normal, not a bug.
- An illegal bare move (`e2e5` from the start position, say) is rejected with `info string illegal move: e2e5` and leaves the board untouched.

## Supported UCI commands

| Command | Notes |
|---|---|
| `uci`, `isready`, `ucinewgame`, `quit` | Handshake / lifecycle. |
| `position startpos [moves …]` | Set the start position; optional move list applied in order. |
| `position fen <FEN> [moves …]` | Set an arbitrary position; optional move list applied in order. |
| `go depth N` | Search to a fixed ply. |
| `go movetime ms` | Search for roughly that long (mapped to a ply depth). |
| `go wtime … btime … [winc …] [binc …] [movestogo …]` | Standard clock-based time control; budget mapped to a ply depth. |
| `go infinite` | Searches at the engine's deepest configured ply. |
| `stop`, `setoption`, `ponderhit`, `debug`, `register` | Accepted but currently no-ops. |

### Known limitations

- Search runs synchronously on the main thread, so `stop` cannot interrupt a search in progress and `isready` is not answered mid-search. Most GUIs handle this gracefully.
- Time controls are converted to a search ply depth up front rather than polled against a deadline mid-search; very short time controls may overshoot at higher depths.
- The engine never underpromotes — `AiPonderMove` is hardcoded to promote to Queen.

## Testing

The repository now includes a dedicated NUnit test project: `ChessCoreEngine.Tests`.

### Run all tests

This includes the depth-5 perft baseline and takes longer.

```powershell
dotnet test ChessCore.sln -nologo
```

### Run fast tests only

This excludes slow tests (currently depth-5 perft).

```powershell
dotnet test ChessCore.sln -nologo --filter "TestCategory!=Slow"
```

### Perft baselines

`PerftBaselineTests` validates move-generation against known start-position node counts:

- Depth 1: `20`
- Depth 2: `400`
- Depth 3: `8902`
- Depth 4: `197281`
- Depth 5: `4865609` (`Slow` category)

### Run perft tests only

Fast perft set (depth 1-4):

```powershell
dotnet test ChessCoreEngine.Tests/ChessCoreEngine.Tests.csproj -nologo --filter "FullyQualifiedName~PerftBaselineTests&FullyQualifiedName~InitialPosition_PerftMatchesKnownCounts"
```

Depth-5 perft only:

```powershell
dotnet test ChessCoreEngine.Tests/ChessCoreEngine.Tests.csproj -nologo --filter "FullyQualifiedName~PerftBaselineTests&FullyQualifiedName~InitialPosition_PerftDepth5_MatchesKnownCount"
```

All perft tests:

```powershell
dotnet test ChessCoreEngine.Tests/ChessCoreEngine.Tests.csproj -nologo --filter "FullyQualifiedName~PerftBaselineTests"
```

## CI Recommendation

For pull requests, run fast validation:

```powershell
dotnet build ChessCore.sln -nologo
dotnet test ChessCore.sln -nologo --filter "TestCategory!=Slow"
```

For scheduled/nightly runs, include full validation:

```powershell
dotnet test ChessCore.sln -nologo
```

## Cutting a release

The `.github/workflows/release.yml` workflow builds platform binaries automatically. To ship a new version:

1. Bump `<Version>` in `ChessCore/ChessCore.csproj` (this is the **only** place the version lives — `id name` in UCI and the `compiler` command both read it from assembly metadata at runtime).
2. Commit and push to `master`.
3. Tag the commit and push the tag:
    ```powershell
    git tag v1.2.0
    git push origin v1.2.0
    ```
4. The workflow builds Windows / Linux / macOS-x64 / macOS-arm64 binaries (each in self-contained and framework-dependent flavors), generates `SHA256SUMS.txt`, and publishes a GitHub Release with auto-generated notes.

To rerun the workflow against an existing tag without pushing a new one, use the **Run workflow** button on the Actions tab and supply the tag name.

# About

This project is a product of a rather rash decision in mid 2008 to learn to program my own Chess Game, hence began my journey into the art of computer chess.  The main goal of the original project was to learn about how computers play chess while producing a chess engine that is easily understood and well documented.  I feel that goal has now been achieved.  As the next step I decided to release the full source code for my chess engine under the MIT license to allow other developers to learn and contribute to further improve & extend my chess engine.

The documentation & tutorial on how to build a chess engine can be accessed in the following 2 formats:

PDF

http://www.adamberent.com/wp-content/uploads/2019/02/GuideToProgrammingChessEngine.pdf

Website

http://adamberent.com/home/chess/computer-chess/




