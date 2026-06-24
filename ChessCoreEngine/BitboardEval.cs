namespace ChessEngine.Engine
{
    // Bridges the bitboard Position to the existing evaluator while preserving its
    // scores EXACTLY. Strategy: project a Position onto a mailbox Board whose
    // placement and flags match what Board(fen) would produce, then run the real
    // PieceValidMoves.GenerateValidMoves + Evaluation.EvaluateBoardScore. Because
    // the unchanged engine code computes the signals (attack/defend values,
    // mobility, attack boards) and the score, the result is identical by
    // construction.
    //
    // This is the Phase-2a scorer (correct, guaranteed-identical strength). It also
    // serves as the oracle for the Phase-2b optimization that reproduces the eval
    // signals directly from bitboards to avoid the per-eval GenerateValidMoves call.
    internal static class BitboardEval
    {
        internal static int ScoreViaLegacyGen(Position p)
        {
            Board b = ToBoard(p);
            PieceValidMoves.GenerateValidMoves(b);
            Evaluation.EvaluateBoardScore(b);
            return b.Score;
        }

        // Project a Position onto a fresh Board, reproducing Board(fen)'s state so
        // the downstream legacy generator + evaluator behave identically.
        internal static Board ToBoard(Position p)
        {
            Board b = new Board(); // CanCastle defaults true, attack boards fresh, squares empty

            for (int sq = 0; sq < 64; sq++)
            {
                int code = p.PieceOn[sq];
                if (code == Position.EMPTY) continue;

                var color = (ChessPieceColor)(code / 6);
                var type = (ChessPieceType)(code % 6);
                var piece = new Piece(type, color) { Moved = ComputeMoved(p, sq, color, type) };
                b.Squares[sq].Piece = piece;
            }

            b.WhoseMove = (ChessPieceColor)p.SideToMove;

            // {White,Black}CanCastle stay at their ctor default (true);
            // GenerateValidMoves recomputes them from the Moved flags, exactly as it
            // does for a freshly loaded Board.
            b.WhiteCastled = p.Castled[0];
            b.BlackCastled = p.Castled[1];

            b.HalfMoveClock = (byte)(p.HalfmoveClock > 255 ? 255 : p.HalfmoveClock);
            b.MoveCount = p.FullMove;

            if (p.EpSquare != -1)
            {
                b.EnPassantPosition = (byte)p.EpSquare;
                // EnPassantColor is the side that just double-pushed (the capturable
                // pawn) — the opponent of the side to move.
                b.EnPassantColor = p.SideToMove == 0 ? ChessPieceColor.Black : ChessPieceColor.White;
            }

            return b;
        }

        // Reproduces Board(fen)'s Moved-flag logic: every piece is "moved" except a
        // king/rook still sitting on its home square with the matching castle right.
        private static bool ComputeMoved(Position p, int sq, ChessPieceColor color, ChessPieceType type)
        {
            if (type == ChessPieceType.King)
            {
                if (color == ChessPieceColor.White && sq == 60 && (p.CastleRights & (Position.WK | Position.WQ)) != 0) return false;
                if (color == ChessPieceColor.Black && sq == 4 && (p.CastleRights & (Position.BK | Position.BQ)) != 0) return false;
                return true;
            }
            if (type == ChessPieceType.Rook)
            {
                if (sq == 63 && (p.CastleRights & Position.WK) != 0) return false;
                if (sq == 56 && (p.CastleRights & Position.WQ) != 0) return false;
                if (sq == 7 && (p.CastleRights & Position.BK) != 0) return false;
                if (sq == 0 && (p.CastleRights & Position.BQ) != 0) return false;
                return true;
            }
            return true;
        }
    }
}
