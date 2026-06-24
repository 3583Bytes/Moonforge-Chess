using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ChessEngine.Engine
{
    // Negamax alpha-beta search over the bitboard Position (make/unmake + bitboard
    // move generation), scoring leaves via BitboardEval (which matches the legacy
    // fresh-load evaluation exactly).
    //
    // Mirrors the heuristic set of the legacy Search — transposition table, reverse
    // futility pruning, null-move pruning, late move reductions / pruning, killers,
    // history, check extension, and a quiescence search (captures + first-ply knight
    // checks). It orders moves natively from bitboards (MVV-LVA / killers / history),
    // so the search tree differs from the legacy one, but the leaf scores are the
    // legacy fresh-load scores.
    internal static class BitboardSearch
    {
        private const int Mate = 1_000_000;   // base mate score, depth-adjusted
        private const int Inf = 2_000_000;
        private const int MateThreshold = Mate - 1000;
        private const int MaxPly = 64;

        internal static long NodesSearched;
        internal static long NodesQuiescence;

        // Killer moves (2 per ply) and history heuristic, mirroring the legacy search.
        private static readonly Move[,] _killers = new Move[2, MaxPly];
        private const int HistoryMax = 512;
        private static readonly int[,,] _history = new int[2, 64, 64];

        // Time-abort plumbing (same approach as the legacy search).
        private const int AbortCheckInterval = 2048;
        private static long _deadlineTicks;
        private static bool _aborted;
        private static int _abortCounter;

        private static bool CheckAbort()
        {
            if (_aborted) return true;
            if (_deadlineTicks == 0) return false;
            if (++_abortCounter < AbortCheckInterval) return false;
            _abortCounter = 0;
            if (Stopwatch.GetTimestamp() >= _deadlineTicks) _aborted = true;
            return _aborted;
        }

        private static int Eval(Position p)
        {
            // Native bitboard evaluation (no Board projection, no GenerateValidMoves);
            // validated to match the legacy fresh-load score exactly.
            int raw = BitboardEvalNative.Score(p); // + = good for White
            return p.SideToMove == 0 ? raw : -raw;
        }

        // Convenience overload for fixed-depth searches (tests, bench).
        internal static Move FindBestMove(Position root, int maxDepth, out int score)
            => FindBestMove(root, maxDepth, 0, out score, out _);

        internal static Move FindBestMove(Position root, int maxDepth, long deadlineMs,
                                          out int score, out int depthReached)
        {
            NodesSearched = 0;
            NodesQuiescence = 0;
            _aborted = false;
            _abortCounter = 0;
            _deadlineTicks = deadlineMs > 0
                ? Stopwatch.GetTimestamp() + (long)(deadlineMs * Stopwatch.Frequency / 1000L)
                : 0;
            Array.Clear(_history, 0, _history.Length);
            Array.Clear(_killers, 0, _killers.Length);

            score = 0;
            depthReached = 0;

            var rootMoves = new List<Move>(64);
            MoveGen.GenerateLegal(root, rootMoves);
            if (rootMoves.Count == 0) return default;

            // Endgame / low-mobility depth boost, matching the legacy search's
            // ModifyDepth: small branching factor lets us afford 1-2 extra plies.
            maxDepth = ModifyDepth(maxDepth, rootMoves.Count, Bitboards.PopCount(root.OccAll));

            Move best = rootMoves[0];
            int bestScore = -Inf;

            for (int depth = 1; depth <= maxDepth; depth++)
            {
                int alpha = -Inf;
                Move iterBest = best;
                int iterBestScore = -Inf;
                bool completed = true;

                OrderMoves(root, rootMoves, best, 0);

                foreach (Move m in rootMoves)
                {
                    root.MakeMove(m);
                    int value = -AlphaBeta(root, depth - 1, -Inf, -alpha, 1, true, false);
                    root.UnmakeMove(m);

                    if (_aborted) { completed = false; break; }

                    if (value > iterBestScore)
                    {
                        iterBestScore = value;
                        iterBest = m;
                        if (value > alpha) alpha = value;
                    }
                }

                if (!completed) break; // discard partial iteration

                best = iterBest;
                bestScore = iterBestScore;
                depthReached = depth;

                if (bestScore >= MateThreshold) break; // forced mate found
            }

            score = bestScore;
            return best;
        }

        private static int AlphaBeta(Position p, int depth, int alpha, int beta, int ply,
                                     bool allowNull, bool extended)
        {
            NodesSearched++;
            if (CheckAbort()) return 0;
            if (p.HalfmoveClock >= 100) return 0; // 50-move draw

            bool inCheck = MoveGen.InCheck(p, p.SideToMove);

            // Check extension: don't drop into quiescence while in check (the reply
            // is forced and tactically sharp). Extend one ply, once.
            if (depth == 0)
            {
                if (inCheck && !extended) { depth = 1; extended = true; }
                else return Quiescence(p, alpha, beta, 0);
            }

            // Transposition table probe.
            ulong ttKey = p.Hash;
            int origAlpha = alpha;
            if (TranspositionTable.Probe(ttKey, (byte)depth, alpha, beta, out int ttScore, out byte ttSrc, out byte ttDst))
                return ttScore;

            // Reverse futility pruning (static null move).
            if (depth <= 6 && !inCheck && Math.Abs(beta) < 30000)
            {
                int staticEval = Eval(p);
                if (staticEval - 100 * depth >= beta) return beta;
            }

            // Null-move pruning.
            const int NullR = 2;
            if (allowNull && depth >= 1 + NullR && !inCheck && Math.Abs(beta) < 30000
                && HasNonPawnMaterial(p))
            {
                p.MakeNullMove();
                int nullScore = -AlphaBeta(p, depth - 1 - NullR, -beta, -beta + 1, ply + 1, false, extended);
                p.UnmakeNullMove();
                if (_aborted) return 0;
                if (nullScore >= beta) return beta;
            }

            var moves = new List<Move>(64);
            MoveGen.GenerateLegal(p, moves);

            if (moves.Count == 0)
                return inCheck ? -(Mate + depth) : 0; // checkmate or stalemate

            Move ttMove = (ttSrc != 0 || ttDst != 0) ? new Move(ttSrc, ttDst) : default;
            OrderMoves(p, moves, ttMove, ply);

            int us = p.SideToMove;
            byte bestSrc = 0, bestDst = 0;
            int legalCount = 0;

            foreach (Move m in moves)
            {
                bool isCapture = m.Flag == MoveFlag.EnPassant || p.PieceOn[m.To] != Position.EMPTY;
                bool isQuiet = !isCapture && !m.IsPromotion;

                p.MakeMove(m);
                bool givesCheck = MoveGen.InCheck(p, p.SideToMove);

                // Late move pruning: at shallow depth, skip late quiet moves entirely.
                if (depth <= 3 && !inCheck && isQuiet && !givesCheck
                    && legalCount >= 6 + depth * depth)
                {
                    p.UnmakeMove(m);
                    legalCount++;
                    continue;
                }

                // Late move reductions: search late quiet moves shallower first.
                int reduction = 0;
                if (depth >= 3 && legalCount >= 3 && isQuiet && !inCheck && !givesCheck)
                {
                    reduction = legalCount >= 6 ? 2 : 1;
                    if (reduction >= depth) reduction = depth - 1;
                }

                legalCount++;
                int value = -AlphaBeta(p, depth - 1 - reduction, -beta, -alpha, ply + 1, true, extended);

                // Re-search at full depth if a reduced search beat alpha.
                if (reduction > 0 && value > alpha)
                    value = -AlphaBeta(p, depth - 1, -beta, -alpha, ply + 1, true, extended);

                p.UnmakeMove(m);

                if (_aborted) return 0;

                if (value >= beta)
                {
                    if (isQuiet)
                    {
                        StoreKiller(m, ply);
                        BumpHistory(us, m, depth);
                    }
                    if (Math.Abs(beta) < MateThreshold)
                        TranspositionTable.Store(ttKey, beta, (byte)depth, TranspositionTable.FlagLower, m.From, m.To);
                    return beta;
                }
                if (value > alpha)
                {
                    alpha = value;
                    bestSrc = m.From;
                    bestDst = m.To;
                }
            }

            byte flag = alpha > origAlpha ? TranspositionTable.FlagExact : TranspositionTable.FlagUpper;
            if (Math.Abs(alpha) < MateThreshold)
                TranspositionTable.Store(ttKey, alpha, (byte)depth, flag, bestSrc, bestDst);
            return alpha;
        }

        private static int Quiescence(Position p, int alpha, int beta, int qsPly)
        {
            NodesQuiescence++;
            if (CheckAbort()) return 0;

            int standPat = Eval(p);
            if (standPat >= beta) return beta;
            if (standPat > alpha) alpha = standPat;

            var moves = new List<Move>(32);
            GenerateQMoves(p, qsPly, moves);
            OrderMoves(p, moves, default, 0);

            foreach (Move m in moves)
            {
                p.MakeMove(m);
                int value = -Quiescence(p, -beta, -alpha, qsPly + 1);
                p.UnmakeMove(m);
                if (_aborted) return 0;
                if (value >= beta) return beta;
                if (value > alpha) alpha = value;
            }

            return alpha;
        }

        // Quiescence move set: captures (incl. ep and capture-promotions) plus, at
        // the first qsearch ply, non-capture knight moves that give check (catches
        // knight-fork tactics, as the legacy search does).
        private static void GenerateQMoves(Position p, int qsPly, List<Move> outMoves)
        {
            var all = new List<Move>(64);
            MoveGen.GenerateLegal(p, all);
            int enemyKing = p.KingSq[p.SideToMove ^ 1];
            int knightBase = p.SideToMove * 6 + (int)ChessPieceType.Knight;

            foreach (Move m in all)
            {
                bool isCapture = m.Flag == MoveFlag.EnPassant || p.PieceOn[m.To] != Position.EMPTY;
                if (isCapture) { outMoves.Add(m); continue; }

                if (qsPly == 0 && p.PieceOn[m.From] == knightBase
                    && (Bitboards.KnightAttacks[enemyKing] & Bitboards.Bit[m.To]) != 0)
                {
                    outMoves.Add(m); // non-capture knight check
                }
            }
        }

        #region Move ordering

        private static void OrderMoves(Position p, List<Move> moves, Move tt, int ply)
        {
            int us = p.SideToMove;
            moves.Sort((a, b) => MoveScore(p, b, tt, us, ply).CompareTo(MoveScore(p, a, tt, us, ply)));
        }

        private static int MoveScore(Position p, Move m, Move tt, int us, int ply)
        {
            if (m.From == tt.From && m.To == tt.To && m.Promotion == tt.Promotion && (tt.From != 0 || tt.To != 0))
                return int.MaxValue;

            int victim = m.Flag == MoveFlag.EnPassant ? (int)ChessPieceType.Pawn
                       : p.PieceOn[m.To] == Position.EMPTY ? -1
                       : p.PieceOn[m.To] % 6;

            if (victim >= 0) // capture: MVV-LVA, ranked above quiets
            {
                int attacker = p.PieceOn[m.From] % 6;
                return 100_000 + Val((ChessPieceType)victim) * 16 - Val((ChessPieceType)attacker);
            }
            if (m.IsPromotion) return 95_000 + Val(m.Promotion);

            // Killers, then history for the remaining quiets.
            if (ply < MaxPly)
            {
                if (Same(m, _killers[0, ply])) return 90_000;
                if (Same(m, _killers[1, ply])) return 89_000;
            }
            return _history[us, m.From, m.To];
        }

        private static bool Same(Move a, Move b) => a.From == b.From && a.To == b.To && (a.From != 0 || a.To != 0);

        private static void StoreKiller(Move m, int ply)
        {
            if (ply >= MaxPly) return;
            if (Same(m, _killers[0, ply])) return;
            _killers[1, ply] = _killers[0, ply];
            _killers[0, ply] = m;
        }

        private static void BumpHistory(int color, Move m, int depth)
        {
            int v = _history[color, m.From, m.To] + depth * depth;
            _history[color, m.From, m.To] = v > HistoryMax ? HistoryMax : v;
        }

        #endregion

        // Mirrors the legacy Search.ModifyDepth: boost the ceiling when there are
        // few legal moves or few pieces (small tree, so deeper search is cheap).
        private static int ModifyDepth(int depth, int possibleMoves, int piecesRemaining)
        {
            if (possibleMoves <= 20 || piecesRemaining < 14)
            {
                if (possibleMoves <= 10 || piecesRemaining < 6) depth += 1;
                depth += 1;
            }
            return depth;
        }

        private static bool HasNonPawnMaterial(Position p)
        {
            int b = p.SideToMove * 6;
            return (p.Pieces[b + (int)ChessPieceType.Knight] | p.Pieces[b + (int)ChessPieceType.Bishop]
                  | p.Pieces[b + (int)ChessPieceType.Rook] | p.Pieces[b + (int)ChessPieceType.Queen]) != 0;
        }

        private static int Val(ChessPieceType t) => t switch
        {
            ChessPieceType.Pawn => 100,
            ChessPieceType.Knight => 320,
            ChessPieceType.Bishop => 325,
            ChessPieceType.Rook => 500,
            ChessPieceType.Queen => 975,
            ChessPieceType.King => 10000,
            _ => 0
        };
    }
}
