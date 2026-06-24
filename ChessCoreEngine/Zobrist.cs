using System;

namespace ChessEngine.Engine
{
    // Zobrist hash — a 64-bit signature of a position. Two positions that are
    // identical in every search-relevant respect must produce the same hash, and
    // distinct positions should collide only with probability ~2^-64. Used as
    // the key for the transposition table.
    //
    // What's included in the hash:
    //   * piece-on-square (12 piece types × 64 squares)
    //   * side to move
    //   * castling rights, encoded as a 4-bit mask
    //   * en-passant target file (only when EnPassantPosition != 0)
    //
    // What's NOT included:
    //   * the halfmove clock — two positions that differ only in the clock are
    //     considered the same for TT lookup. This is the standard choice and is
    //     safe because the search itself checks HalfMoveClock for the 50-move
    //     rule before doing anything else.
    //   * the repetition counter — same reasoning.
    //
    // Implementation note: we recompute the hash from scratch at the end of
    // every MovePiece rather than maintaining it incrementally. The cost is
    // ~64 array reads + ~67 XORs per move, which is negligible next to the
    // PieceValidMoves.GenerateValidMoves call that happens on the same move.
    // The win is correctness — incremental Zobrist gets subtle bugs around
    // castling, promotion, and en-passant captures that only show up as TT
    // poisoning in unusual positions.
    internal static class Zobrist
    {
        // [color (0=W,1=B), piece type 0..5, square 0..63]
        private static readonly ulong[,,] PieceHash = new ulong[2, 6, 64];
        private static readonly ulong SideToMoveHash;
        // Indexed by a 4-bit castling-rights mask: bit0=WK, bit1=WQ, bit2=BK, bit3=BQ.
        // ChessCore only tracks {White,Black}CanCastle as booleans (queenside/kingside
        // not separated), so we use a 2-bit mask in practice — but the 16-slot table
        // costs nothing and futureproofs the encoding.
        private static readonly ulong[] CastlingHash = new ulong[16];
        // One per file (0..7) when an en-passant target exists.
        private static readonly ulong[] EnPassantFileHash = new ulong[8];

        static Zobrist()
        {
            // Fixed seed so hashes are deterministic across runs — makes
            // debugging TT issues sane and lets bench numbers stay stable.
            var rng = new Random(0xC0FFEE);
            for (int c = 0; c < 2; c++)
                for (int p = 0; p < 6; p++)
                    for (int s = 0; s < 64; s++)
                        PieceHash[c, p, s] = NextUlong(rng);
            SideToMoveHash = NextUlong(rng);
            for (int i = 0; i < 16; i++) CastlingHash[i] = NextUlong(rng);
            for (int i = 0; i < 8; i++) EnPassantFileHash[i] = NextUlong(rng);
        }

        private static ulong NextUlong(Random rng)
        {
            var buf = new byte[8];
            rng.NextBytes(buf);
            return BitConverter.ToUInt64(buf, 0);
        }

        internal static ulong ComputeHash(Board board)
        {
            ulong h = 0UL;

            for (byte sq = 0; sq < 64; sq++)
            {
                Piece p = board.Squares[sq].Piece;
                if (p == null) continue;
                int colorIdx = p.PieceColor == ChessPieceColor.White ? 0 : 1;
                // ChessPieceType: King=0, Queen=1, Rook=2, Bishop=3, Knight=4, Pawn=5, None=6.
                // Cast directly; None never appears on the board.
                int typeIdx = (int)p.PieceType;
                h ^= PieceHash[colorIdx, typeIdx, sq];
            }

            if (board.WhoseMove == ChessPieceColor.Black)
                h ^= SideToMoveHash;

            int castleMask = 0;
            // ChessCore tracks "can castle" as one bool per side (not split into
            // kingside/queenside). Map to the low two bits of the mask; the
            // upper bits stay 0 today but the table is wide enough for a future
            // refinement that splits the flags.
            if (board.WhiteCanCastle) castleMask |= 1;
            if (board.BlackCanCastle) castleMask |= 4;
            h ^= CastlingHash[castleMask];

            if (board.EnPassantPosition != 0)
            {
                int file = board.EnPassantPosition % 8;
                h ^= EnPassantFileHash[file];
            }

            return h;
        }

        // Raw sub-key accessors for the bitboard core's incremental hashing.
        // The bitboard `Position` maintains its hash by XOR-ing these in/out as
        // pieces move, rather than recomputing from scratch. Castling rights there
        // use a full 4-bit KQkq mask (the 16-entry table accommodates it).
        internal static ulong PieceKey(int colorIdx, int typeIdx, int sq) => PieceHash[colorIdx, typeIdx, sq];
        internal static ulong SideKey() => SideToMoveHash;
        internal static ulong CastleKey(int mask) => CastlingHash[mask];
        internal static ulong EnPassantFileKey(int file) => EnPassantFileHash[file];
    }
}
