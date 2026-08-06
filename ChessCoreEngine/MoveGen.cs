using System;
using System.Collections.Generic;

namespace ChessEngine.Engine
{
    // Bitboard move generation for the new core. Produces pseudo-legal moves, then
    // filters to legal ones with a make / king-safety-check / unmake pass. This is
    // the simplest correct strategy; perft validates it. (A pin-aware fully-legal
    // generator is a possible later optimization.)
    internal static class MoveGen
    {
        // White-pawn start rank = row 6 (a8=0 indexing) = squares 48..55;
        // Black-pawn start rank = row 1 = squares 8..15.
        private const ulong Rank2 = 0x00FF000000000000UL; // squares 48..55
        private const ulong Rank7 = 0x000000000000FF00UL; // squares 8..15

        // Is `sq` attacked by any piece of color `by`, given current occupancy?
        internal static bool IsSquareAttacked(Position p, int sq, int by)
        {
            int baseIdx = by * 6;
            // A `by` pawn attacks `sq` iff it stands on one of the squares that the
            // opposite-color pawn-attack pattern from `sq` points to.
            if ((Bitboards.PawnAttacks[by ^ 1][sq] & p.Pieces[baseIdx + (int)ChessPieceType.Pawn]) != 0) return true;
            if ((Bitboards.KnightAttacks[sq] & p.Pieces[baseIdx + (int)ChessPieceType.Knight]) != 0) return true;
            if ((Bitboards.KingAttacks[sq] & p.Pieces[baseIdx + (int)ChessPieceType.King]) != 0) return true;

            ulong bishopsQueens = p.Pieces[baseIdx + (int)ChessPieceType.Bishop] | p.Pieces[baseIdx + (int)ChessPieceType.Queen];
            if ((Bitboards.BishopAttacks(sq, p.OccAll) & bishopsQueens) != 0) return true;

            ulong rooksQueens = p.Pieces[baseIdx + (int)ChessPieceType.Rook] | p.Pieces[baseIdx + (int)ChessPieceType.Queen];
            if ((Bitboards.RookAttacks(sq, p.OccAll) & rooksQueens) != 0) return true;

            return false;
        }

        internal static bool InCheck(Position p, int color)
            => IsSquareAttacked(p, p.KingSq[color], color ^ 1);

        // --- Static Exchange Evaluation ---
        // Material values by ChessPieceType (King=0..Pawn=5) for SEE only.
        private static readonly int[] SeeVal = { 20000, 900, 500, 330, 320, 100 };

        // All pieces of both colors attacking `sq` given occupancy `occ`.
        private static ulong AttackersTo(Position p, int sq, ulong occ)
        {
            ulong att = (Bitboards.PawnAttacks[1][sq] & p.Pieces[(int)ChessPieceType.Pawn])          // white pawns
                      | (Bitboards.PawnAttacks[0][sq] & p.Pieces[6 + (int)ChessPieceType.Pawn]);     // black pawns
            att |= Bitboards.KnightAttacks[sq] & (p.Pieces[(int)ChessPieceType.Knight] | p.Pieces[6 + (int)ChessPieceType.Knight]);
            att |= Bitboards.KingAttacks[sq] & (p.Pieces[(int)ChessPieceType.King] | p.Pieces[6 + (int)ChessPieceType.King]);
            ulong bq = p.Pieces[(int)ChessPieceType.Bishop] | p.Pieces[(int)ChessPieceType.Queen]
                     | p.Pieces[6 + (int)ChessPieceType.Bishop] | p.Pieces[6 + (int)ChessPieceType.Queen];
            att |= Bitboards.BishopAttacks(sq, occ) & bq;
            ulong rq = p.Pieces[(int)ChessPieceType.Rook] | p.Pieces[(int)ChessPieceType.Queen]
                     | p.Pieces[6 + (int)ChessPieceType.Rook] | p.Pieces[6 + (int)ChessPieceType.Queen];
            att |= Bitboards.RookAttacks(sq, occ) & rq;
            return att & occ;
        }

        // Static exchange evaluation of capture `m`: net material (centipawns) for the
        // side to move if the capture sequence on the target square is played out with
        // least-valuable-attacker recaptures. Negative => the capture loses material.
        internal static int See(Position p, Move m)
        {
            int to = m.To, from = m.From, us = p.SideToMove;
            bool ep = m.Flag == MoveFlag.EnPassant;
            int targetType = ep ? (int)ChessPieceType.Pawn : p.PieceOn[to] % 6;

            Span<int> gain = stackalloc int[32];
            int d = 0;
            gain[0] = SeeVal[targetType];
            int aType = p.PieceOn[from] % 6;

            ulong occ = p.OccAll ^ Bitboards.Bit[from];
            if (ep) occ ^= Bitboards.Bit[us == 0 ? to + 8 : to - 8];
            ulong attackers = AttackersTo(p, to, occ);

            ulong bq = p.Pieces[(int)ChessPieceType.Bishop] | p.Pieces[(int)ChessPieceType.Queen]
                     | p.Pieces[6 + (int)ChessPieceType.Bishop] | p.Pieces[6 + (int)ChessPieceType.Queen];
            ulong rq = p.Pieces[(int)ChessPieceType.Rook] | p.Pieces[(int)ChessPieceType.Queen]
                     | p.Pieces[6 + (int)ChessPieceType.Rook] | p.Pieces[6 + (int)ChessPieceType.Queen];

            int side = us ^ 1;
            while (true)
            {
                d++;
                gain[d] = SeeVal[aType] - gain[d - 1];

                // Least-valuable attacker of `side` still on the board.
                ulong sideAtt = attackers & p.OccByColor[side];
                if (sideAtt == 0) break;
                ulong lvaBit = 0;
                for (int t = (int)ChessPieceType.Pawn; t >= (int)ChessPieceType.King; t--)
                {
                    ulong piecesT = sideAtt & p.Pieces[side * 6 + t];
                    if (piecesT != 0) { aType = t; lvaBit = piecesT & (~piecesT + 1); break; }
                }
                occ ^= lvaBit; // remove the recapturing piece
                // X-ray: removing it may reveal sliders behind it.
                attackers |= (Bitboards.BishopAttacks(to, occ) & bq) | (Bitboards.RookAttacks(to, occ) & rq);
                attackers &= occ;
                side ^= 1;
            }

            while (--d > 0) gain[d - 1] = -Math.Max(-gain[d - 1], gain[d]);
            return gain[0];
        }

        internal static void GenerateLegal(Position p, List<Move> moves)
        {
            var pseudo = new List<Move>(64);
            GenerateLegal(p, moves, pseudo);
        }

        // Search supplies a per-ply scratch list so legal move generation does
        // not allocate a temporary pseudo-legal list at every node.
        internal static void GenerateLegal(Position p, List<Move> moves, List<Move> pseudo)
        {
            pseudo.Clear();
            GeneratePseudoLegal(p, pseudo);

            int us = p.SideToMove;
            foreach (Move m in pseudo)
            {
                p.MakeMove(m);
                // After MakeMove, p.SideToMove is the opponent; the mover's king
                // must not be attacked by them.
                if (!IsSquareAttacked(p, p.KingSq[us], p.SideToMove))
                    moves.Add(m);
                p.UnmakeMove(m);
            }
        }

        internal static void GeneratePseudoLegal(Position p, List<Move> moves)
        {
            int us = p.SideToMove;
            ulong ownOcc = p.OccByColor[us];
            ulong notOwn = ~ownOcc;
            int baseIdx = us * 6;

            GeneratePawnMoves(p, us, moves);

            ulong knights = p.Pieces[baseIdx + (int)ChessPieceType.Knight];
            while (knights != 0)
            {
                int s = Bitboards.PopLsb(ref knights);
                EmitTargets(moves, s, Bitboards.KnightAttacks[s] & notOwn);
            }

            ulong bishops = p.Pieces[baseIdx + (int)ChessPieceType.Bishop];
            while (bishops != 0)
            {
                int s = Bitboards.PopLsb(ref bishops);
                EmitTargets(moves, s, Bitboards.BishopAttacks(s, p.OccAll) & notOwn);
            }

            ulong rooks = p.Pieces[baseIdx + (int)ChessPieceType.Rook];
            while (rooks != 0)
            {
                int s = Bitboards.PopLsb(ref rooks);
                EmitTargets(moves, s, Bitboards.RookAttacks(s, p.OccAll) & notOwn);
            }

            ulong queens = p.Pieces[baseIdx + (int)ChessPieceType.Queen];
            while (queens != 0)
            {
                int s = Bitboards.PopLsb(ref queens);
                EmitTargets(moves, s, Bitboards.QueenAttacks(s, p.OccAll) & notOwn);
            }

            int kingSq = p.KingSq[us];
            EmitTargets(moves, kingSq, Bitboards.KingAttacks[kingSq] & notOwn);
            GenerateCastles(p, us, moves);
        }

        private static void EmitTargets(List<Move> moves, int from, ulong targets)
        {
            while (targets != 0)
            {
                int to = Bitboards.PopLsb(ref targets);
                moves.Add(new Move((byte)from, (byte)to));
            }
        }

        private static void GeneratePawnMoves(Position p, int us, List<Move> moves)
        {
            ulong pawns = p.Pieces[us * 6 + (int)ChessPieceType.Pawn];
            ulong enemy = p.OccByColor[us ^ 1];
            int forward = us == 0 ? -8 : 8;
            ulong startRank = us == 0 ? Rank2 : Rank7;

            while (pawns != 0)
            {
                int s = Bitboards.PopLsb(ref pawns);
                int one = s + forward;

                // Pushes.
                if (one >= 0 && one < 64 && p.PieceOn[one] == Position.EMPTY)
                {
                    if (IsPromotionRank(one)) AddPromotions(moves, s, one);
                    else
                    {
                        moves.Add(new Move((byte)s, (byte)one));
                        // Double push from the start rank.
                        if ((Bitboards.Bit[s] & startRank) != 0)
                        {
                            int two = one + forward;
                            if (p.PieceOn[two] == Position.EMPTY)
                                moves.Add(new Move((byte)s, (byte)two, MoveFlag.DoublePawnPush));
                        }
                    }
                }

                // Captures (incl. promotion captures).
                ulong caps = Bitboards.PawnAttacks[us][s] & enemy;
                while (caps != 0)
                {
                    int to = Bitboards.PopLsb(ref caps);
                    if (IsPromotionRank(to)) AddPromotions(moves, s, to);
                    else moves.Add(new Move((byte)s, (byte)to));
                }

                // En passant.
                if (p.EpSquare != -1 && (Bitboards.PawnAttacks[us][s] & Bitboards.Bit[p.EpSquare]) != 0)
                    moves.Add(new Move((byte)s, (byte)p.EpSquare, MoveFlag.EnPassant));
            }
        }

        private static bool IsPromotionRank(int sq) => sq < 8 || sq > 55;

        private static void AddPromotions(List<Move> moves, int from, int to)
        {
            moves.Add(new Move((byte)from, (byte)to, MoveFlag.Normal, ChessPieceType.Queen));
            moves.Add(new Move((byte)from, (byte)to, MoveFlag.Normal, ChessPieceType.Rook));
            moves.Add(new Move((byte)from, (byte)to, MoveFlag.Normal, ChessPieceType.Bishop));
            moves.Add(new Move((byte)from, (byte)to, MoveFlag.Normal, ChessPieceType.Knight));
        }

        private static void GenerateCastles(Position p, int us, List<Move> moves)
        {
            int them = us ^ 1;
            if (us == 0)
            {
                // White king on e1 (60).
                if ((p.CastleRights & Position.WK) != 0 &&
                    p.PieceOn[61] == Position.EMPTY && p.PieceOn[62] == Position.EMPTY &&
                    !IsSquareAttacked(p, 60, them) && !IsSquareAttacked(p, 61, them) && !IsSquareAttacked(p, 62, them))
                {
                    moves.Add(new Move(60, 62, MoveFlag.KingCastle));
                }
                if ((p.CastleRights & Position.WQ) != 0 &&
                    p.PieceOn[59] == Position.EMPTY && p.PieceOn[58] == Position.EMPTY && p.PieceOn[57] == Position.EMPTY &&
                    !IsSquareAttacked(p, 60, them) && !IsSquareAttacked(p, 59, them) && !IsSquareAttacked(p, 58, them))
                {
                    moves.Add(new Move(60, 58, MoveFlag.QueenCastle));
                }
            }
            else
            {
                // Black king on e8 (4).
                if ((p.CastleRights & Position.BK) != 0 &&
                    p.PieceOn[5] == Position.EMPTY && p.PieceOn[6] == Position.EMPTY &&
                    !IsSquareAttacked(p, 4, them) && !IsSquareAttacked(p, 5, them) && !IsSquareAttacked(p, 6, them))
                {
                    moves.Add(new Move(4, 6, MoveFlag.KingCastle));
                }
                if ((p.CastleRights & Position.BQ) != 0 &&
                    p.PieceOn[3] == Position.EMPTY && p.PieceOn[2] == Position.EMPTY && p.PieceOn[1] == Position.EMPTY &&
                    !IsSquareAttacked(p, 4, them) && !IsSquareAttacked(p, 3, them) && !IsSquareAttacked(p, 2, them))
                {
                    moves.Add(new Move(4, 2, MoveFlag.QueenCastle));
                }
            }
        }

        #region Perft

        internal static long Perft(Position p, int depth)
        {
            if (depth == 0) return 1;

            var moves = new List<Move>(64);
            GenerateLegal(p, moves);

            if (depth == 1) return moves.Count;

            long nodes = 0;
            foreach (Move m in moves)
            {
                p.MakeMove(m);
                nodes += Perft(p, depth - 1);
                p.UnmakeMove(m);
            }
            return nodes;
        }

        // Per-root-move node counts, for debugging perft mismatches against
        // published "divide" output.
        internal static Dictionary<string, long> PerftDivide(Position p, int depth)
        {
            var result = new Dictionary<string, long>();
            var moves = new List<Move>(64);
            GenerateLegal(p, moves);

            foreach (Move m in moves)
            {
                p.MakeMove(m);
                result[m.ToString()] = depth <= 1 ? 1 : Perft(p, depth - 1);
                p.UnmakeMove(m);
            }
            return result;
        }

        #endregion
    }
}
