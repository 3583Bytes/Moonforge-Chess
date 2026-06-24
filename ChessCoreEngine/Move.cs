namespace ChessEngine.Engine
{
    // A move in the bitboard core. Squares use the engine convention (0 = a8 ..
    // 63 = h1). `Promotion` is ChessPieceType.None for non-promotions, otherwise
    // the piece the pawn becomes. The captured piece is NOT stored here — it is
    // recorded in the undo stack at make time so unmake can restore it.
    internal enum MoveFlag : byte
    {
        Normal = 0,
        DoublePawnPush = 1, // sets the en-passant target square
        KingCastle = 2,     // O-O
        QueenCastle = 3,    // O-O-O
        EnPassant = 4       // pawn captures the en-passant pawn (on a different square than To)
    }

    internal readonly struct Move
    {
        internal readonly byte From;
        internal readonly byte To;
        internal readonly MoveFlag Flag;
        internal readonly ChessPieceType Promotion;

        internal Move(byte from, byte to, MoveFlag flag = MoveFlag.Normal,
                      ChessPieceType promotion = ChessPieceType.None)
        {
            From = from;
            To = to;
            Flag = flag;
            Promotion = promotion;
        }

        internal bool IsPromotion => Promotion != ChessPieceType.None;

        // Pure coordinate notation, e.g. "e2e4" / "e7e8q". Used for perft-divide
        // labelling and cross-checking against published divide output.
        public override string ToString()
        {
            string s = SquareName(From) + SquareName(To);
            if (IsPromotion)
            {
                s += Promotion switch
                {
                    ChessPieceType.Queen => "q",
                    ChessPieceType.Rook => "r",
                    ChessPieceType.Bishop => "b",
                    ChessPieceType.Knight => "n",
                    _ => ""
                };
            }
            return s;
        }

        // index 0 = a8, so file = sq % 8 (a..h), rank = 8 - sq / 8.
        private static string SquareName(int sq)
        {
            char file = (char)('a' + (sq % 8));
            int rank = 8 - (sq / 8);
            return $"{file}{rank}";
        }
    }
}
