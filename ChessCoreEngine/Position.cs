using System;
using System.Collections.Generic;

namespace ChessEngine.Engine
{
    // Bitboard board state for the new move generator and (eventually) search.
    //
    // Representation:
    //   * Pieces[12] — one bitboard per (color, type). Index = color*6 + type,
    //     using ChessPieceColor (White=0, Black=1) and ChessPieceType
    //     (King=0..Pawn=5). So White King = 0, White Pawn = 5, Black King = 6, …
    //   * OccByColor[2], OccAll — occupancy unions, kept in lockstep.
    //   * PieceOn[64] — piece code on each square, or EMPTY. A cheap mailbox view
    //     so evaluation / FEN / UI can read pieces without scanning bitboards.
    //   * CastleRights — 4-bit KQkq mask (WK=1, WQ=2, BK=4, BQ=8). Note this is a
    //     finer-grained right than the old Board's combined per-side bool.
    //   * EpSquare — en-passant *target* square (where a capturing pawn lands),
    //     or -1. KingSq[2] — cached king squares.
    //
    // Square indexing matches the engine: 0 = a8 .. 63 = h1 (see Bitboards.cs).
    //
    // The Zobrist hash is maintained incrementally in MakeMove and restored from
    // the undo stack in UnmakeMove. ComputeHash() recomputes from scratch and is
    // used to assert the incremental hash stays correct (see perft validation).
    internal sealed class Position
    {
        internal const byte EMPTY = 255;

        // Castle-right bits.
        internal const int WK = 1, WQ = 2, BK = 4, BQ = 8;

        // Home squares (a8=0 indexing).
        private const int WhiteKingStart = 60, BlackKingStart = 4;
        private const int WhiteRookKingside = 63, WhiteRookQueenside = 56;
        private const int BlackRookKingside = 7, BlackRookQueenside = 0;

        internal readonly ulong[] Pieces = new ulong[12];
        internal readonly ulong[] OccByColor = new ulong[2];
        internal ulong OccAll;
        internal readonly byte[] PieceOn = new byte[64];
        internal readonly int[] KingSq = new int[2];

        // Whether each side has castled (or started with no castling rights). Mirrors
        // the old Board's {White,Black}Castled, which the evaluator rewards (+50) and
        // which differs from "still has rights" — needed for exact eval reproduction.
        internal readonly bool[] Castled = new bool[2];

        internal int SideToMove; // 0 = White, 1 = Black
        internal int CastleRights;
        internal int EpSquare = -1;
        internal int HalfmoveClock;
        internal int FullMove = 1;
        internal ulong Hash;

        private struct Undo
        {
            internal byte CapturedCode;
            internal int CastleRights;
            internal int EpSquare;
            internal int HalfmoveClock;
            internal int FullMove;
            internal ulong Hash;
            internal bool PrevCastledUs;
        }

        private readonly Stack<Undo> _undo = new Stack<Undo>(64);

        private Position()
        {
            for (int i = 0; i < 64; i++) PieceOn[i] = EMPTY;
        }

        internal static int PieceIndex(ChessPieceColor color, ChessPieceType type)
            => (int)color * 6 + (int)type;

        #region Raw board mutation (no hash side-effects)

        private void RawAdd(int code, int sq)
        {
            ulong b = 1UL << sq;
            Pieces[code] |= b;
            int color = code / 6;
            OccByColor[color] |= b;
            OccAll |= b;
            PieceOn[sq] = (byte)code;
            if (code % 6 == 0) KingSq[color] = sq; // King
        }

        private void RawRemove(int code, int sq)
        {
            ulong b = ~(1UL << sq);
            Pieces[code] &= b;
            int color = code / 6;
            OccByColor[color] &= b;
            OccAll &= b;
            PieceOn[sq] = EMPTY;
        }

        private void RawMove(int code, int from, int to)
        {
            RawRemove(code, from);
            RawAdd(code, to);
        }

        #endregion

        #region Make / Unmake

        internal void MakeMove(Move m)
        {
            int us = SideToMove;
            int them = us ^ 1;
            int from = m.From;
            int to = m.To;
            int moving = PieceOn[from];
            int movingType = moving % 6;

            // Resolve the captured piece (and its square) up front so it can be
            // recorded in the undo frame.
            int capSquare = -1;
            byte capturedCode = EMPTY;
            if (m.Flag == MoveFlag.EnPassant)
            {
                capSquare = us == 0 ? to + 8 : to - 8; // captured pawn sits behind the ep target
                capturedCode = PieceOn[capSquare];
            }
            else if (PieceOn[to] != EMPTY)
            {
                capSquare = to;
                capturedCode = PieceOn[to];
            }

            _undo.Push(new Undo
            {
                CapturedCode = capturedCode,
                CastleRights = CastleRights,
                EpSquare = EpSquare,
                HalfmoveClock = HalfmoveClock,
                FullMove = FullMove,
                Hash = Hash,
                PrevCastledUs = Castled[us]
            });

            // Strip old castle/ep contributions from the hash; re-add the new ones
            // at the end after they're updated.
            Hash ^= Zobrist.CastleKey(CastleRights);
            if (EpSquare != -1) Hash ^= Zobrist.EnPassantFileKey(EpSquare % 8);

            HalfmoveClock++;
            if (movingType == (int)ChessPieceType.Pawn) HalfmoveClock = 0;

            if (capturedCode != EMPTY)
            {
                Hash ^= Zobrist.PieceKey(them, capturedCode % 6, capSquare);
                RawRemove(capturedCode, capSquare);
                HalfmoveClock = 0;
            }

            // Move the piece (handle promotion: a different type lands on `to`).
            Hash ^= Zobrist.PieceKey(us, movingType, from);
            RawRemove(moving, from);
            if (m.IsPromotion)
            {
                int promoCode = PieceIndex((ChessPieceColor)us, m.Promotion);
                Hash ^= Zobrist.PieceKey(us, (int)m.Promotion, to);
                RawAdd(promoCode, to);
            }
            else
            {
                Hash ^= Zobrist.PieceKey(us, movingType, to);
                RawAdd(moving, to);
            }

            // Castling: move the rook too.
            if (m.Flag == MoveFlag.KingCastle)
            {
                int rookFrom = us == 0 ? WhiteRookKingside : BlackRookKingside;
                int rookTo = us == 0 ? 61 : 5;
                int rookCode = PieceIndex((ChessPieceColor)us, ChessPieceType.Rook);
                Hash ^= Zobrist.PieceKey(us, (int)ChessPieceType.Rook, rookFrom)
                      ^ Zobrist.PieceKey(us, (int)ChessPieceType.Rook, rookTo);
                RawMove(rookCode, rookFrom, rookTo);
            }
            else if (m.Flag == MoveFlag.QueenCastle)
            {
                int rookFrom = us == 0 ? WhiteRookQueenside : BlackRookQueenside;
                int rookTo = us == 0 ? 59 : 3;
                int rookCode = PieceIndex((ChessPieceColor)us, ChessPieceType.Rook);
                Hash ^= Zobrist.PieceKey(us, (int)ChessPieceType.Rook, rookFrom)
                      ^ Zobrist.PieceKey(us, (int)ChessPieceType.Rook, rookTo);
                RawMove(rookCode, rookFrom, rookTo);
            }

            if (m.Flag == MoveFlag.KingCastle || m.Flag == MoveFlag.QueenCastle)
                Castled[us] = true;

            // Update castling rights: king move clears that side; rook leaving or
            // being captured on a home square clears the matching right.
            CastleRights &= CastleRightsMask(from);
            CastleRights &= CastleRightsMask(to);

            // En-passant target: set only on a double pawn push.
            EpSquare = m.Flag == MoveFlag.DoublePawnPush ? (from + to) / 2 : -1;

            if (us == 1) FullMove++;

            // Re-add updated castle/ep contributions and flip side.
            Hash ^= Zobrist.CastleKey(CastleRights);
            if (EpSquare != -1) Hash ^= Zobrist.EnPassantFileKey(EpSquare % 8);
            Hash ^= Zobrist.SideKey();
            SideToMove = them;
        }

        internal void UnmakeMove(Move m)
        {
            Undo u = _undo.Pop();
            SideToMove ^= 1;
            int us = SideToMove;
            int from = m.From;
            int to = m.To;

            // Undo the moving piece (restore a pawn if this was a promotion).
            if (m.IsPromotion)
            {
                int promoCode = PieceIndex((ChessPieceColor)us, m.Promotion);
                RawRemove(promoCode, to);
                RawAdd(PieceIndex((ChessPieceColor)us, ChessPieceType.Pawn), from);
            }
            else
            {
                int moving = PieceOn[to];
                RawMove(moving, to, from);
            }

            // Undo castling rook move.
            if (m.Flag == MoveFlag.KingCastle)
            {
                int rookFrom = us == 0 ? WhiteRookKingside : BlackRookKingside;
                int rookTo = us == 0 ? 61 : 5;
                RawMove(PieceIndex((ChessPieceColor)us, ChessPieceType.Rook), rookTo, rookFrom);
            }
            else if (m.Flag == MoveFlag.QueenCastle)
            {
                int rookFrom = us == 0 ? WhiteRookQueenside : BlackRookQueenside;
                int rookTo = us == 0 ? 59 : 3;
                RawMove(PieceIndex((ChessPieceColor)us, ChessPieceType.Rook), rookTo, rookFrom);
            }

            // Restore a captured piece.
            if (u.CapturedCode != EMPTY)
            {
                int capSq = m.Flag == MoveFlag.EnPassant
                    ? (us == 0 ? to + 8 : to - 8)
                    : to;
                RawAdd(u.CapturedCode, capSq);
            }

            CastleRights = u.CastleRights;
            EpSquare = u.EpSquare;
            HalfmoveClock = u.HalfmoveClock;
            FullMove = u.FullMove;
            Hash = u.Hash;
            Castled[us] = u.PrevCastledUs;
        }

        // Null move: pass the turn (for null-move pruning). Clears the en-passant
        // target (no longer legitimately capturable after passing) and flips side.
        internal void MakeNullMove()
        {
            _undo.Push(new Undo
            {
                CapturedCode = EMPTY,
                CastleRights = CastleRights,
                EpSquare = EpSquare,
                HalfmoveClock = HalfmoveClock,
                FullMove = FullMove,
                Hash = Hash,
                PrevCastledUs = Castled[SideToMove]
            });

            if (EpSquare != -1) Hash ^= Zobrist.EnPassantFileKey(EpSquare % 8);
            EpSquare = -1;
            Hash ^= Zobrist.SideKey();
            SideToMove ^= 1;
        }

        internal void UnmakeNullMove()
        {
            Undo u = _undo.Pop();
            SideToMove ^= 1;
            CastleRights = u.CastleRights;
            EpSquare = u.EpSquare;
            HalfmoveClock = u.HalfmoveClock;
            FullMove = u.FullMove;
            Hash = u.Hash;
            Castled[SideToMove] = u.PrevCastledUs;
        }

        // Returns a mask to AND into CastleRights: clears the right(s) associated
        // with `sq` when a piece leaves it (king/rook home) or is captured on it.
        private static int CastleRightsMask(int sq)
        {
            switch (sq)
            {
                case WhiteKingStart: return ~(WK | WQ);
                case BlackKingStart: return ~(BK | BQ);
                case WhiteRookKingside: return ~WK;
                case WhiteRookQueenside: return ~WQ;
                case BlackRookKingside: return ~BK;
                case BlackRookQueenside: return ~BQ;
                default: return ~0;
            }
        }

        #endregion

        #region Hash

        internal ulong ComputeHash()
        {
            ulong h = 0;
            for (int sq = 0; sq < 64; sq++)
            {
                int code = PieceOn[sq];
                if (code == EMPTY) continue;
                h ^= Zobrist.PieceKey(code / 6, code % 6, sq);
            }
            if (SideToMove == 1) h ^= Zobrist.SideKey();
            h ^= Zobrist.CastleKey(CastleRights);
            if (EpSquare != -1) h ^= Zobrist.EnPassantFileKey(EpSquare % 8);
            return h;
        }

        #endregion

        #region FEN

        internal static Position FromFen(string fen)
        {
            FenParser.ParsedFen parsed = FenParser.Parse(fen);
            var p = new Position();
            for (int square = 0; square < parsed.PieceOn.Length; square++)
            {
                byte code = parsed.PieceOn[square];
                if (code != FenParser.Empty) p.RawAdd(code, square);
            }

            p.SideToMove = parsed.SideToMove == ChessPieceColor.White ? 0 : 1;
            p.CastleRights = parsed.CastleRights;

            // Matches Board(fen): {White,Black}Castled start true and are cleared
            // only when that side still has a castling right in the FEN.
            p.Castled[0] = (p.CastleRights & (WK | WQ)) == 0;
            p.Castled[1] = (p.CastleRights & (BK | BQ)) == 0;

            p.EpSquare = parsed.EnPassantSquare;
            p.HalfmoveClock = parsed.HalfmoveClock;
            p.FullMove = parsed.HasMoveCounters ? parsed.FullmoveNumber : 1;

            p.Hash = p.ComputeHash();
            return p;
        }

        #endregion
    }
}
