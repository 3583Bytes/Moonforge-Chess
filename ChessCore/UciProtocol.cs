using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ChessEngine.Engine;

namespace ChessCore
{
    internal sealed class UciProtocol
    {
        // Version is read from the assembly (set by <Version> in ChessCore.csproj) so the csproj
        // is the single source of truth. .NET 8 appends "+<commit-sha>" to InformationalVersion
        // when a .git folder is present — strip that so `id name` stays clean.
        private static readonly string Version = ReadAssemblyVersion();
        private static readonly string EngineName = "Moonforge Chess " + Version;
        private const string EngineAuthor = "Adam Berent";
        private const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

        private static string ReadAssemblyVersion()
        {
            var asm = typeof(UciProtocol).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? asm.GetName().Version?.ToString()
                       ?? "0.0.0";
            var plus = info.IndexOf('+');
            return plus < 0 ? info : info.Substring(0, plus);
        }

        private readonly Engine _engine = new Engine();

        public void Run()
        {
            string line;
            while ((line = Console.In.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;

                var space = line.IndexOf(' ');
                var cmd = space < 0 ? line : line.Substring(0, space);
                var rest = space < 0 ? string.Empty : line.Substring(space + 1).Trim();

                switch (cmd)
                {
                    case "uci":          HandleUci();              break;
                    case "isready":      Send("readyok");          break;
                    case "ucinewgame":   _engine.NewGame();        break;
                    case "position":     HandlePosition(rest);     break;
                    case "go":           HandleGo(rest);           break;
                    case "stop":         /* search is synchronous; nothing to stop */ break;
                    case "setoption":    /* no options advertised */ break;
                    case "ponderhit":    /* pondering not supported */ break;
                    case "debug":        /* ignored */             break;
                    case "register":     /* ignored */             break;
                    case "quit":         return;
                    // Non-standard extensions, emitted as `info string` so GUIs ignore them and
                    // a human running the engine at a terminal can still use them.
                    case "d":
                    case "show":         DrawBoard();              break;
                    case "fen":          Send("info string " + _engine.FEN); break;
                    case "eval":         HandleEval();             break;
                    case "bench":        HandleBench();            break;
                    case "flip":         HandleFlip();             break;
                    case "compiler":     HandleCompiler();         break;
                    default:
                        if (LooksLikeMove(cmd))
                        {
                            if (!ApplyUciMove(cmd))
                                Send("info string illegal move: " + cmd);
                        }
                        else
                        {
                            Send("info string unknown command: " + cmd);
                        }
                        break;
                }
            }
        }

        private static void Send(string s)
        {
            Console.Out.WriteLine(s);
            Console.Out.Flush();
        }

        private void HandleUci()
        {
            Send("id name " + EngineName);
            Send("id author " + EngineAuthor);
            Send("uciok");
        }

        // position [startpos | fen <fen>] [moves m1 m2 ...]
        private void HandlePosition(string args)
        {
            if (string.IsNullOrEmpty(args)) return;

            string fen;
            int movesIdx;

            if (args.StartsWith("startpos", StringComparison.Ordinal))
            {
                fen = StartFen;
                movesIdx = args.IndexOf("moves", StringComparison.Ordinal);
            }
            else if (args.StartsWith("fen", StringComparison.Ordinal))
            {
                var afterFen = args.Substring(3).TrimStart();
                movesIdx = afterFen.IndexOf("moves", StringComparison.Ordinal);
                fen = movesIdx < 0 ? afterFen : afterFen.Substring(0, movesIdx).Trim();
                if (movesIdx >= 0)
                {
                    // recompute movesIdx relative to original `args` so the slice below works
                    movesIdx = args.IndexOf("moves", StringComparison.Ordinal);
                }
            }
            else
            {
                Send("info string malformed position command");
                return;
            }

            _engine.InitiateBoard(fen);

            if (movesIdx < 0) return;
            var movesPart = args.Substring(movesIdx + "moves".Length).Trim();
            if (movesPart.Length == 0) return;

            foreach (var mv in movesPart.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!ApplyUciMove(mv))
                {
                    Send("info string rejected move " + mv + " in position command");
                    return;
                }
            }
        }

        // UCI move syntax: e2e4, or e7e8q for promotion (q/r/b/n lowercase).
        private bool ApplyUciMove(string mv)
        {
            if (mv.Length != 4 && mv.Length != 5) return false;

            _engine.PromoteToPieceType = mv.Length == 5
                ? PromotionPieceFromChar(mv[4])
                : ChessPieceType.Queen;

            return _engine.MovePieceAN(mv.Substring(0, 4));
        }

        private static ChessPieceType PromotionPieceFromChar(char c)
        {
            switch (char.ToLowerInvariant(c))
            {
                case 'q': return ChessPieceType.Queen;
                case 'r': return ChessPieceType.Rook;
                case 'b': return ChessPieceType.Bishop;
                case 'n': return ChessPieceType.Knight;
                default:  return ChessPieceType.Queen;
            }
        }

        // go [depth N | movetime ms | wtime ms btime ms [winc ms] [binc ms] [movestogo n] | infinite]
        private void HandleGo(string args)
        {
            int depth = -1;
            int movetimeMs = -1;
            int wtime = -1, btime = -1, winc = 0, binc = 0, movestogo = 0;
            bool infinite = false;

            var tokens = args.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                switch (tokens[i])
                {
                    case "depth":     if (i + 1 < tokens.Length) int.TryParse(tokens[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out depth); break;
                    case "movetime":  if (i + 1 < tokens.Length) int.TryParse(tokens[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out movetimeMs); break;
                    case "wtime":     if (i + 1 < tokens.Length) int.TryParse(tokens[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out wtime); break;
                    case "btime":     if (i + 1 < tokens.Length) int.TryParse(tokens[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out btime); break;
                    case "winc":      if (i + 1 < tokens.Length) int.TryParse(tokens[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out winc); break;
                    case "binc":      if (i + 1 < tokens.Length) int.TryParse(tokens[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out binc); break;
                    case "movestogo": if (i + 1 < tokens.Length) int.TryParse(tokens[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out movestogo); break;
                    case "infinite":  infinite = true; break;
                    case "ponder":    /* not supported */ break;
                    case "nodes":     if (i + 1 < tokens.Length) i++; break; // consume value, ignore
                    case "mate":      if (i + 1 < tokens.Length) i++; break;
                    case "searchmoves": i = tokens.Length;            break; // not supported, skip rest
                }
            }

            var (ceiling, deadlineMs) = PickBudget(depth, movetimeMs, wtime, btime, winc, binc, movestogo, infinite);
            _engine.PlyDepthSearched = ceiling;
            _engine.SearchDeadlineMs = deadlineMs;

            var sw = Stopwatch.StartNew();
            var movesBefore = _engine.GetMoveHistory().Count;
            _engine.AiPonderMove();
            sw.Stop();

            EmitInfo(sw.ElapsedMilliseconds);

            var movesAfter = _engine.GetMoveHistory().Count;
            if (movesAfter == movesBefore)
            {
                // No legal move was played (mate / stalemate at search root). UCI null move.
                Send("bestmove 0000");
                return;
            }

            var last = _engine.GetMoveHistory().Peek();
            Send("bestmove " + MoveToUci(last));
        }

        // Map UCI `go` arguments to a (depth ceiling, wall-clock deadline) pair for the engine.
        //
        // The engine's IterativeSearch is now real ID: it runs depth 1, 2, 3... up to `ceiling`,
        // stopping early if the deadline (in ms from search start) is exceeded. Two modes:
        //
        //   * Fixed depth (`go depth N`, `go infinite`, no time info): deadline = 0, ceiling
        //     bounds the search. Deterministic, reproducible — what bench and training mode want.
        //   * Time-bounded: deadline = computed budget, ceiling = very high (32). The search
        //     runs until time runs out and returns the best move from the last completed depth.
        //
        // Side-to-move comes from `_engine.WhoseMove` (set by the prior `position` command),
        // so this is non-static — fixes a real bug in the old PickDepth where unequal clocks
        // could pick the wrong side's time via Math.Max.
        private (byte ceiling, long deadlineMs) PickBudget(int depth, int movetimeMs, int wtime, int btime, int winc, int binc, int movestogo, bool infinite)
        {
            // Generous ceiling for time-bounded search — high enough that the deadline always
            // binds first on realistic positions. ModifyDepth can add +2; keep clear of byte max.
            const byte HighCeiling = 32;
            // Pulled-from-the-air safety margin so we abort before the GUI's hard clock runs
            // out. 50ms covers abort poll latency (~10ms at 200k nps), UCI overhead, and OS
            // scheduling jitter. Scaled at 5% of budget for very long TCs.
            const int SafetyMs = 50;

            if (depth > 0)   return ((byte)Math.Min(depth, HighCeiling), 0);
            if (infinite)    return (HighCeiling, 0);

            int budget;
            if (movetimeMs > 0)
            {
                budget = movetimeMs;
            }
            else if (wtime > 0 || btime > 0)
            {
                int myTime = _engine.WhoseMove == ChessPieceColor.White ? wtime : btime;
                int myInc  = _engine.WhoseMove == ChessPieceColor.White ? winc  : binc;
                if (myTime <= 0) myTime = Math.Max(wtime, btime); // defensive fallback
                int slices = movestogo > 0 ? Math.Min(movestogo, 40) : 30;
                budget = (myTime / slices) + (int)(myInc * 0.8);
            }
            else
            {
                return (5, 0); // no time info at all — Medium-ish fixed depth
            }

            int margin = Math.Max(SafetyMs, budget / 20);
            long deadline = Math.Max(10, budget - margin);
            return (HighCeiling, deadline);
        }

        private void EmitInfo(long elapsedMs)
        {
            // SearchScore is the alpha returned by the root search, already from the
            // searching side's POV in centipawns — exactly what UCI expects.
            int score = _engine.SearchScore;

            long nodes = (long)_engine.NodesSearched + _engine.NodesQuiessence;
            long nps = elapsedMs > 0 ? (nodes * 1000L) / elapsedMs : 0;

            // Mate scores in Search.cs are encoded as ±(32767 + remaining_depth), so a
            // larger absolute value means a shorter mate. Convert to UCI's "mate N" (N
            // full moves to mate) by counting plies from the score magnitude.
            string scoreField;
            if (score >= 32767)
            {
                int pliesToMate = Math.Max(1, score - 32767);
                int movesToMate = (pliesToMate + 1) / 2;
                scoreField = "mate " + movesToMate;
            }
            else if (score <= -32767)
            {
                int pliesToMate = Math.Max(1, -score - 32767);
                int movesToMate = (pliesToMate + 1) / 2;
                scoreField = "mate -" + movesToMate;
            }
            else
            {
                scoreField = "cp " + score;
            }

            // PV from the search (space-separated long-algebraic moves like "e2e4 e7e5 ...").
            // Empty when a book move was played — that's expected and UCI-legal.
            string pv = _engine.PvLine ?? "";
            string pvField = pv.Length > 0 ? " pv " + pv : "";

            Send(string.Format(CultureInfo.InvariantCulture,
                "info depth {0} score {1} nodes {2} nps {3} time {4}{5}",
                _engine.PlyDepthReached, scoreField, nodes, nps, elapsedMs, pvField));
        }

        private static bool LooksLikeMove(string s)
        {
            if (s.Length != 4 && s.Length != 5) return false;
            if (s[0] < 'a' || s[0] > 'h') return false;
            if (s[1] < '1' || s[1] > '8') return false;
            if (s[2] < 'a' || s[2] > 'h') return false;
            if (s[3] < '1' || s[3] > '8') return false;
            if (s.Length == 5)
            {
                var c = char.ToLowerInvariant(s[4]);
                if (c != 'q' && c != 'r' && c != 'b' && c != 'n') return false;
            }
            return true;
        }

        private void HandleEval()
        {
            int whitePov = _engine.EvaluateBoardScore();
            int stmPov = _engine.WhoseMove == ChessPieceColor.White ? whitePov : -whitePov;

            Send("info string FEN: " + _engine.FEN);
            Send(string.Format(CultureInfo.InvariantCulture,
                "info string Static evaluation (White's POV): {0} cp", whitePov));
            Send(string.Format(CultureInfo.InvariantCulture,
                "info string Static evaluation (side to move, {0}): {1} cp",
                _engine.WhoseMove, stmPov));
            Send("info string Note: includes material + piece-square tables (see Evaluation.cs).");
        }

        // Compact set of well-known test positions. Search runs synchronously; depth 5 keeps
        // total bench time under ~10s on a modern dev box while still exercising the search.
        private static readonly (string Name, string Fen)[] BenchPositions = new[]
        {
            ("Start",        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"),
            ("Kiwipete",     "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1"),
            ("Endgame",     "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1"),
            ("BK.01",        "1k1r4/pp1b1R2/3q2pp/4p3/2B5/4Q3/PPP2B1P/2K5 b - - 0 1"),
            ("KRk endgame",  "8/8/8/8/8/3k4/3r4/3K4 w - - 0 1"),
            ("Promotion",    "8/4P3/8/8/8/8/8/k1K5 w - - 0 1"),
        };

        private void HandleBench()
        {
            const byte depth = 5;
            long totalNodes = 0;
            var totalSw = Stopwatch.StartNew();

            Send("info string bench depth=" + depth + ", positions=" + BenchPositions.Length);

            byte savedDepth = _engine.PlyDepthSearched;
            long savedDeadline = _engine.SearchDeadlineMs;
            try
            {
                _engine.PlyDepthSearched = depth;
                _engine.SearchDeadlineMs = 0; // bench wants deterministic fixed-depth, no clock
                foreach (var (name, fen) in BenchPositions)
                {
                    _engine.InitiateBoard(fen);
                    var sw = Stopwatch.StartNew();
                    _engine.AiPonderMove();
                    sw.Stop();

                    long nodes = (long)_engine.NodesSearched + _engine.NodesQuiessence;
                    long nps = sw.ElapsedMilliseconds > 0 ? (nodes * 1000L) / sw.ElapsedMilliseconds : 0;
                    totalNodes += nodes;

                    Send(string.Format(CultureInfo.InvariantCulture,
                        "info string {0,-13} depth {1} nodes {2,9} time {3,5}ms nps {4,8}",
                        name, _engine.PlyDepthReached, nodes, sw.ElapsedMilliseconds, nps));
                }
            }
            finally
            {
                _engine.PlyDepthSearched = savedDepth;
                _engine.SearchDeadlineMs = savedDeadline;
                _engine.NewGame(); // bench mutates the board; reset so subsequent commands aren't surprising
            }

            totalSw.Stop();
            long totalNps = totalSw.ElapsedMilliseconds > 0
                ? (totalNodes * 1000L) / totalSw.ElapsedMilliseconds : 0;
            Send(string.Format(CultureInfo.InvariantCulture,
                "info string =============== total nodes {0} time {1}ms nps {2}",
                totalNodes, totalSw.ElapsedMilliseconds, totalNps));
        }

        // Stockfish-style flip: mirror the board top-to-bottom and swap every piece's color.
        // Useful for verifying eval symmetry (a flipped position should evaluate to -original).
        private void HandleFlip()
        {
            var parts = _engine.FEN.Split(' ');
            if (parts.Length < 1) return;

            var ranks = parts[0].Split('/');
            Array.Reverse(ranks);
            for (int i = 0; i < ranks.Length; i++)
            {
                var sb = new System.Text.StringBuilder(ranks[i].Length);
                foreach (var c in ranks[i])
                {
                    if (char.IsUpper(c))      sb.Append(char.ToLowerInvariant(c));
                    else if (char.IsLower(c)) sb.Append(char.ToUpperInvariant(c));
                    else                       sb.Append(c);
                }
                ranks[i] = sb.ToString();
            }
            parts[0] = string.Join("/", ranks);

            if (parts.Length > 1) parts[1] = parts[1] == "w" ? "b" : "w";
            if (parts.Length > 2)
            {
                // Castling rights: KQkq with case swapped (K->k, k->K, etc.)
                var sb = new System.Text.StringBuilder(parts[2].Length);
                foreach (var c in parts[2])
                {
                    if (char.IsUpper(c))      sb.Append(char.ToLowerInvariant(c));
                    else if (char.IsLower(c)) sb.Append(char.ToUpperInvariant(c));
                    else                       sb.Append(c);
                }
                parts[2] = sb.ToString();
            }
            if (parts.Length > 3 && parts[3] != "-")
            {
                // En passant target square: only the rank flips (3 <-> 6).
                var ep = parts[3];
                char rank = ep[1] == '3' ? '6' : ep[1] == '6' ? '3' : ep[1];
                parts[3] = ep[0].ToString() + rank;
            }

            _engine.InitiateBoard(string.Join(" ", parts));
            Send("info string flipped — new FEN: " + _engine.FEN);
        }

        private void HandleCompiler()
        {
            Send("info string Moonforge Chess " + Version);
            Send("info string .NET runtime: " + Environment.Version);
            Send("info string Framework: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            Send("info string OS: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            Send("info string Architecture: " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        }

        // Emit the board as a series of `info string` lines. Stockfish-style `d` also works.
        // GUIs ignore `info string` payloads, so this stays UCI-compliant when one is connected;
        // at a bare terminal it renders as a readable ASCII board.
        private void DrawBoard()
        {
            Send("info string  ---------------------------------");
            for (byte rank = 0; rank < 8; rank++)
            {
                var row = new System.Text.StringBuilder("info string " + (8 - rank));
                for (byte file = 0; file < 8; file++)
                {
                    byte idx = (byte)(rank * 8 + file);
                    var type  = _engine.GetPieceTypeAt(idx);
                    var color = _engine.GetPieceColorAt(idx);
                    char glyph = type switch
                    {
                        ChessPieceType.Pawn   => 'P',
                        ChessPieceType.Knight => 'N',
                        ChessPieceType.Bishop => 'B',
                        ChessPieceType.Rook   => 'R',
                        ChessPieceType.Queen  => 'Q',
                        ChessPieceType.King   => 'K',
                        _ => ' ',
                    };
                    if (type != ChessPieceType.None && color == ChessPieceColor.Black)
                        glyph = char.ToLowerInvariant(glyph);
                    row.Append("| ").Append(glyph).Append(' ');
                }
                row.Append('|');
                Send(row.ToString());
                Send("info string  ---------------------------------");
            }
            Send("info string    A   B   C   D   E   F   G   H");
            Send("info string FEN: " + _engine.FEN);
            Send("info string Side to move: " + _engine.WhoseMove);
        }

        private static string MoveToUci(MoveContent move)
        {
            byte srcCol = (byte)(move.MovingPiecePrimary.SrcPosition % 8);
            byte srcRow = (byte)(8 - (move.MovingPiecePrimary.SrcPosition / 8));
            byte dstCol = (byte)(move.MovingPiecePrimary.DstPosition % 8);
            byte dstRow = (byte)(8 - (move.MovingPiecePrimary.DstPosition / 8));

            var s = new string(new[]
            {
                (char)('a' + srcCol), (char)('0' + srcRow),
                (char)('a' + dstCol), (char)('0' + dstRow),
            });

            switch (move.PawnPromotedTo)
            {
                case ChessPieceType.Queen:  return s + "q";
                case ChessPieceType.Rook:   return s + "r";
                case ChessPieceType.Bishop: return s + "b";
                case ChessPieceType.Knight: return s + "n";
                default: return s;
            }
        }
    }
}
