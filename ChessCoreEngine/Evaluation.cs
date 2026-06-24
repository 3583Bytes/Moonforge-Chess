using System;

namespace ChessEngine.Engine
{
    internal static class Evaluation
    {
        internal static readonly short[] blackPawnCount = new short[8];
        internal static readonly short[] whitePawnCount = new short[8];
      
        internal static readonly short[] PawnTable = new short[]
        {
       	     0,  0,  0,  0,  0,  0,  0,  0,
            50, 50, 50, 50, 50, 50, 50, 50,
            20, 20, 30, 40, 40, 30, 20, 20,
             5,  5, 10, 30, 30, 10,  5,  5,
             0,  0,  0, 25, 25,  0,  0,  0,
             5, -5,-10,  0,  0,-10, -5,  5,
             5, 10, 10,-30,-30, 10, 10,  5,
             0,  0,  0,  0,  0,  0,  0,  0
        };

        internal static readonly short[] KnightTable = new short[]
        {
            -50,-40,-30,-30,-30,-30,-40,-50,
            -40,-20,  0,  0,  0,  0,-20,-40,
            -30,  0, 10, 15, 15, 10,  0,-30,
            -30,  5, 15, 20, 20, 15,  5,-30,
            -30,  0, 15, 20, 20, 15,  0,-30,
            -30,  5, 10, 15, 15, 10,  5,-30,
            -40,-20,  0,  5,  5,  0,-20,-40,
            -50,-30,-20,-30,-30,-20,-30,-50,
        };

        internal static readonly short[] BishopTable = new short[]
        {
            -20,-10,-10,-10,-10,-10,-10,-20,
            -10,  0,  0,  0,  0,  0,  0,-10,
            -10,  0,  5, 10, 10,  5,  0,-10,
            -10,  5,  5, 10, 10,  5,  5,-10,
            -10,  0, 10, 10, 10, 10,  0,-10,
            -10, 10, 10, 10, 10, 10, 10,-10,
            -10,  5,  0,  0,  0,  0,  5,-10,
            -20,-10,-40,-10,-10,-40,-10,-20,
        };

        internal static readonly short[] KingTable = new short[]
        {
          -30, -40, -40, -50, -50, -40, -40, -30,
          -30, -40, -40, -50, -50, -40, -40, -30,
          -30, -40, -40, -50, -50, -40, -40, -30,
          -30, -40, -40, -50, -50, -40, -40, -30,
          -20, -30, -30, -40, -40, -30, -30, -20,
          -10, -20, -20, -20, -20, -20, -20, -10, 
           20,  20,   0,   0,   0,   0,  20,  20,
           20,  30,  10,   0,   0,  10,  30,  20
        };

        internal static readonly short[] KingTableEndGame = new short[]
        {
            -50,-40,-30,-20,-20,-30,-40,-50,
            -30,-20,-10,  0,  0,-10,-20,-30,
            -30,-10, 20, 30, 30, 20,-10,-30,
            -30,-10, 30, 40, 40, 30,-10,-30,
            -30,-10, 30, 40, 40, 30,-10,-30,
            -30,-10, 20, 30, 30, 20,-10,-30,
            -30,-30,  0,  0,  0,  0,-30,-30,
            -50,-30,-30,-30,-30,-30,-30,-50
        };

        private static int EvaluatePieceScore(Board board, Square square, byte position, bool endGamePhase,
                                                ref byte knightCount, ref byte bishopCount, ref bool insufficientMaterial)
        {
            int score = 0;

            byte index = position;

            if (square.Piece.PieceColor == ChessPieceColor.Black)
            {
                // Mirror rank only (XOR with 0b111000). `63 - position` rotates both axes,
                // which happens to match today because every PST is file-symmetric — but
                // the moment an asymmetric table is introduced (e.g., king-side vs queen-side
                // shaping) the file-flip would silently invert it for Black.
                index = (byte)(position ^ 56);
            }

            //Calculate Piece Values
            score += square.Piece.PieceValue;
            score += square.Piece.DefendedValue;
            score -= square.Piece.AttackedValue;

            //Double Penalty for Hanging Pieces
            if (square.Piece.DefendedValue < square.Piece.AttackedValue)
            {
                score -= ((square.Piece.AttackedValue - square.Piece.DefendedValue)* 10);
            }

            //Add Points for Mobility
            if (square.Piece.ValidMoves != null)
            {
                score += square.Piece.ValidMoves.Count;
            }

            if (square.Piece.PieceType == ChessPieceType.Pawn)
            {
                insufficientMaterial = false;

                if (position % 8 == 0 || position % 8 == 7)
                {
                    //Rook Pawns are worth 15% less because they can only attack one way
                    score -= 15;
                }

                //Calculate Position Values
                score += PawnTable[index];

                // Pawn chain: small bonus if defended by a friendly pawn on an adjacent
                // file (one rank back). Pawns in a chain are hard to attack.
                score += PawnChainBonus(board, position, square.Piece.PieceColor);

                if (square.Piece.PieceColor == ChessPieceColor.White)
                {
                    if (whitePawnCount[position % 8] > 0)
                    {
                        //Doubled Pawn
                        score -= 15;
                    }

                    if (position >= 8 && position <= 15)
                    {
                        // White pawn on rank 7 — one step from promotion.
                        if (square.Piece.AttackedValue == 0)
                        {
                            whitePawnCount[position % 8] += 100;

                            if (square.Piece.DefendedValue != 0)
                                whitePawnCount[position % 8] += 50;
                        }
                    }
                    else if (position >= 16 && position <= 23)
                    {
                        // White pawn on rank 6.
                        if (square.Piece.AttackedValue == 0)
                        {
                            whitePawnCount[position % 8] += 50;

                            if (square.Piece.DefendedValue != 0)
                                whitePawnCount[position % 8] += 25;
                        }
                    }

                    whitePawnCount[position % 8]+=10;
                }
                else
                {
                    if (blackPawnCount[position % 8] > 0)
                    {
                        //Doubled Pawn
                        score -= 15;
                    }

                    if (position >= 48 && position <= 55)
                    {
                        // Black pawn on rank 2 (one step from promotion from black's POV).
                        if (square.Piece.AttackedValue == 0)
                        {
                            blackPawnCount[position % 8] += 100;

                            if (square.Piece.DefendedValue != 0)
                                blackPawnCount[position % 8] += 50;
                        }
                    }
                    else if (position >= 40 && position <= 47)
                    {
                        // Black pawn on rank 3.
                        if (square.Piece.AttackedValue == 0)
                        {
                            blackPawnCount[position % 8] += 50;

                            if (square.Piece.DefendedValue != 0)
                                blackPawnCount[position % 8] += 25;
                        }
                    }

                    blackPawnCount[position % 8] += 10;
                    
                }
            }
            else if (square.Piece.PieceType == ChessPieceType.Knight)
            {
                knightCount++;

                score += KnightTable[index];

                //In the end game remove a few points for Knights since they are worth less
                if (endGamePhase)
                {
                    score -= 10;
                }

            }
            else if (square.Piece.PieceType == ChessPieceType.Bishop)
            {
                bishopCount++;

                if (bishopCount == 2)
                {
                    //2 Bishops receive a bonus (fire exactly once on the pair-completing bishop;
                    //a 3rd+ bishop from underpromotion must not stack further bonuses).
                    score += 10;
                }

                //In the end game Bishops are worth more
                if (endGamePhase)
                {
                    score += 10;
                }

                score += BishopTable[index];
            }
            else if (square.Piece.PieceType == ChessPieceType.Rook)
            {
                insufficientMaterial = false;
            }
            else if (square.Piece.PieceType == ChessPieceType.Queen)
            {
                insufficientMaterial = false;

                if (square.Piece.Moved && !endGamePhase)
                {
                    score -= 10;
                }
            }
            else if (square.Piece.PieceType == ChessPieceType.King)
            {
                if (square.Piece.ValidMoves != null)
                {
                    if (square.Piece.ValidMoves.Count < 2)
                    {
                        score -= 5;
                    }
                }

                if (endGamePhase)
                {
                    score += KingTableEndGame[index];
                }
                else
                {
                    score += KingTable[index];
                }

                


            }

            return score;
        }

        internal static void EvaluateBoardScore(Board board)
        {
            //Black Score - 
            //White Score +
            board.Score = 0;

            bool insufficientMaterial = true;

            if (board.StaleMate)
            {
                return;
            }
            if (board.HalfMoveClock >= 100)
            {
                return;
            }
            if (board.RepeatedMove >= 3)
            {
                return;
            }
            // Mate scoring lives in Search.AlphaBeta (depth-adjusted via ±depth so faster
            // mates score higher). The static evaluator only ever sees boards whose mate
            // flags have not been set (Search runs SearchForMate after eval, and the
            // engine's MovePiece sets flags only after EvaluateBoardScore returns), so
            // returning a ±32767 sentinel here was dead code.
            if (board.BlackCheck)
            {
                board.Score += 70;
                if (board.EndGamePhase)
                    board.Score += 10;
            }
            else if (board.WhiteCheck)
            {
                board.Score -= 70;
                if (board.EndGamePhase)
                    board.Score -= 10;
            }
            if (board.BlackCastled)
            {
                board.Score -= 50;
            }
            if (board.WhiteCastled)
            {
                board.Score += 50;
            }
            //Add a small bonus for tempo (turn)
            if (board.WhoseMove == ChessPieceColor.White)
            {
                board.Score += 10;
            }
            else
            {
                board.Score -= 10;
            }

            byte blackBishopCount = 0;
            byte whiteBishopCount = 0;

            byte blackKnightCount = 0;
            byte whiteKnightCount = 0;

            Array.Clear(blackPawnCount, 0, 8);
            Array.Clear(whitePawnCount, 0, 8);

            for (byte x = 0; x < 64; x++)
            {
                Square square = board.Squares[x];

                if (square.Piece == null)
                    continue;


                if (square.Piece.PieceColor == ChessPieceColor.White)
                {
                    board.Score += EvaluatePieceScore(board, square, x, board.EndGamePhase,
                        ref whiteKnightCount, ref whiteBishopCount, ref insufficientMaterial);

                    if (square.Piece.PieceType == ChessPieceType.King)
                    {
                        // Skip pawn-wall scoring when the king is still on its starting square (e1);
                        // an uncastled king in the center doesn't meaningfully benefit from a shelter.
                        if (x != 60)
                        {
                            int pawnPos = x - 8;

                            board.Score += CheckPawnWall(board, pawnPos, x);

                            pawnPos = x - 7;

                            board.Score += CheckPawnWall(board, pawnPos, x);

                            pawnPos = x - 9;

                            board.Score += CheckPawnWall(board, pawnPos, x);
                        }
                    }
                }
                else if (square.Piece.PieceColor == ChessPieceColor.Black)
                {
                    board.Score -= EvaluatePieceScore(board, square, x, board.EndGamePhase,
                        ref blackKnightCount, ref blackBishopCount, ref insufficientMaterial);


                    if (square.Piece.PieceType == ChessPieceType.King)
                    {
                        // Skip pawn-wall scoring when the king is still on its starting square (e8).
                        if (x != 4)
                        {
                            int pawnPos = x + 8;

                            board.Score -= CheckPawnWall(board, pawnPos, x);

                            pawnPos = x + 7;

                            board.Score -= CheckPawnWall(board, pawnPos, x);

                            pawnPos = x + 9;

                            board.Score -= CheckPawnWall(board, pawnPos, x);
                        }

                    }
                   
                }

            }

            // Insufficient material to force mate (claim draw):
            //   pawns/rooks/queens already clear the flag in EvaluatePieceScore.
            //   What's left is K + minors. Force mate is impossible unless one side
            //   has 2+ minors (covers K+B+N, K+B+B, K+N+N — the last is debatable
            //   but matches the engine's prior behaviour of not claiming it as a draw).
            //   That leaves: K vs K, K vs K+minor, and K+minor vs K+minor — all insufficient,
            //   including KNvKN and KBvKB which the previous logic incorrectly flagged
            //   as not-insufficient.
            if (insufficientMaterial &&
                (whiteBishopCount + whiteKnightCount > 1 ||
                 blackBishopCount + blackKnightCount > 1))
            {
                insufficientMaterial = false;
            }

            if (insufficientMaterial)
            {
                board.Score = 0;
                board.StaleMate = true;
                board.InsufficientMaterial = true;
                return;
            }

            if (!board.EndGamePhase)
            {
                if (!board.WhiteCanCastle && !board.WhiteCastled)
                {
                    board.Score -= 50;
                }
                if (!board.BlackCanCastle && !board.BlackCastled)
                {
                    board.Score += 50;
                }

                // King-file openness + king-zone attack pressure. Skipped in
                // endgame because king activity is wanted there. The Board
                // tracks king positions directly so we avoid a 64-square scan.
                board.Score += EvaluateKingFileOpenness(board.WhiteKingPosition, ChessPieceColor.White)
                             + EvaluateKingZoneAttacks(board, board.WhiteKingPosition, ChessPieceColor.White);
                board.Score -= EvaluateKingFileOpenness(board.BlackKingPosition, ChessPieceColor.Black)
                             + EvaluateKingZoneAttacks(board, board.BlackKingPosition, ChessPieceColor.Black);
            }

            //Black Isolated Pawns
            if (blackPawnCount[0] >= 1 && blackPawnCount[1] == 0)
            {
                board.Score += 12;
            }
            if (blackPawnCount[1] >= 1 && blackPawnCount[0] == 0 &&
                blackPawnCount[2] == 0)
            {
                board.Score += 14;
            }
            if (blackPawnCount[2] >= 1 && blackPawnCount[1] == 0 &&
                blackPawnCount[3] == 0)
            {
                board.Score += 16;
            }
            if (blackPawnCount[3] >= 1 && blackPawnCount[2] == 0 &&
                blackPawnCount[4] == 0)
            {
                board.Score += 20;
            }
            if (blackPawnCount[4] >= 1 && blackPawnCount[3] == 0 &&
                blackPawnCount[5] == 0)
            {
                board.Score += 20;
            }
            if (blackPawnCount[5] >= 1 && blackPawnCount[4] == 0 &&
                blackPawnCount[6] == 0)
            {
                board.Score += 16;
            }
            if (blackPawnCount[6] >= 1 && blackPawnCount[5] == 0 &&
                blackPawnCount[7] == 0)
            {
                board.Score += 14;
            }
            if (blackPawnCount[7] >= 1 && blackPawnCount[6] == 0)
            {
                board.Score += 12;
            }

            //White Isolated Pawns
            if (whitePawnCount[0] >= 1 && whitePawnCount[1] == 0)
            {
                board.Score -= 12;
            }
            if (whitePawnCount[1] >= 1 && whitePawnCount[0] == 0 &&
                whitePawnCount[2] == 0)
            {
                board.Score -= 14;
            }
            if (whitePawnCount[2] >= 1 && whitePawnCount[1] == 0 &&
                whitePawnCount[3] == 0)
            {
                board.Score -= 16;
            }
            if (whitePawnCount[3] >= 1 && whitePawnCount[2] == 0 &&
                whitePawnCount[4] == 0)
            {
                board.Score -= 20;
            }
            if (whitePawnCount[4] >= 1 && whitePawnCount[3] == 0 &&
                whitePawnCount[5] == 0)
            {
                board.Score -= 20;
            }
            if (whitePawnCount[5] >= 1 && whitePawnCount[4] == 0 &&
                whitePawnCount[6] == 0)
            {
                board.Score -= 16;
            }
            if (whitePawnCount[6] >= 1 && whitePawnCount[5] == 0 &&
                whitePawnCount[7] == 0)
            {
                board.Score -= 14;
            }
            if (whitePawnCount[7] >= 1 && whitePawnCount[6] == 0)
            {
                board.Score -= 12;
            }

            // Passed pawns: no enemy pawn on the same file OR either adjacent file.
            //Black Passed Pawns
            if (blackPawnCount[0] >= 1 && whitePawnCount[0] == 0 && whitePawnCount[1] == 0)
            {
                board.Score -= blackPawnCount[0];
            }
            if (blackPawnCount[1] >= 1 && whitePawnCount[0] == 0 && whitePawnCount[1] == 0 && whitePawnCount[2] == 0)
            {
                board.Score -= blackPawnCount[1];
            }
            if (blackPawnCount[2] >= 1 && whitePawnCount[1] == 0 && whitePawnCount[2] == 0 && whitePawnCount[3] == 0)
            {
                board.Score -= blackPawnCount[2];
            }
            if (blackPawnCount[3] >= 1 && whitePawnCount[2] == 0 && whitePawnCount[3] == 0 && whitePawnCount[4] == 0)
            {
                board.Score -= blackPawnCount[3];
            }
            if (blackPawnCount[4] >= 1 && whitePawnCount[3] == 0 && whitePawnCount[4] == 0 && whitePawnCount[5] == 0)
            {
                board.Score -= blackPawnCount[4];
            }
            if (blackPawnCount[5] >= 1 && whitePawnCount[4] == 0 && whitePawnCount[5] == 0 && whitePawnCount[6] == 0)
            {
                board.Score -= blackPawnCount[5];
            }
            if (blackPawnCount[6] >= 1 && whitePawnCount[5] == 0 && whitePawnCount[6] == 0 && whitePawnCount[7] == 0)
            {
                board.Score -= blackPawnCount[6];
            }
            if (blackPawnCount[7] >= 1 && whitePawnCount[6] == 0 && whitePawnCount[7] == 0)
            {
                board.Score -= blackPawnCount[7];
            }

            //White Passed Pawns
            if (whitePawnCount[0] >= 1 && blackPawnCount[0] == 0 && blackPawnCount[1] == 0)
            {
                board.Score += whitePawnCount[0];
            }
            if (whitePawnCount[1] >= 1 && blackPawnCount[0] == 0 && blackPawnCount[1] == 0 && blackPawnCount[2] == 0)
            {
                board.Score += whitePawnCount[1];
            }
            if (whitePawnCount[2] >= 1 && blackPawnCount[1] == 0 && blackPawnCount[2] == 0 && blackPawnCount[3] == 0)
            {
                board.Score += whitePawnCount[2];
            }
            if (whitePawnCount[3] >= 1 && blackPawnCount[2] == 0 && blackPawnCount[3] == 0 && blackPawnCount[4] == 0)
            {
                board.Score += whitePawnCount[3];
            }
            if (whitePawnCount[4] >= 1 && blackPawnCount[3] == 0 && blackPawnCount[4] == 0 && blackPawnCount[5] == 0)
            {
                board.Score += whitePawnCount[4];
            }
            if (whitePawnCount[5] >= 1 && blackPawnCount[4] == 0 && blackPawnCount[5] == 0 && blackPawnCount[6] == 0)
            {
                board.Score += whitePawnCount[5];
            }
            if (whitePawnCount[6] >= 1 && blackPawnCount[5] == 0 && blackPawnCount[6] == 0 && blackPawnCount[7] == 0)
            {
                board.Score += whitePawnCount[6];
            }
            if (whitePawnCount[7] >= 1 && blackPawnCount[6] == 0 && blackPawnCount[7] == 0)
            {
                board.Score += whitePawnCount[7];
            }
        }

        private static int CheckPawnWall(Board board, int pawnPos, int kingPos)
        {

            if (kingPos % 8 == 7 && pawnPos % 8 == 0)
            {
                return 0;
            }

            if (kingPos % 8 == 0 && pawnPos % 8 == 7)
            {
                return 0;
            }

            if (pawnPos > 63 || pawnPos < 0)
            {
                return 0;
            }

            if (board.Squares[pawnPos].Piece != null)
            {
                if (board.Squares[pawnPos].Piece.PieceColor == board.Squares[kingPos].Piece.PieceColor)
                {
                    if (board.Squares[pawnPos].Piece.PieceType == ChessPieceType.Pawn)
                    {
                        return 10;
                    }
                }
            }

            return 0;
        }

        // Penalize open / half-open files near the king. Pawn-count arrays must be fully
        // populated (i.e., this is called after the main piece-scoring loop).
        // Returns a score in the king-owner's POV — caller applies sign for white/black.
        private static int EvaluateKingFileOpenness(byte kingPos, ChessPieceColor color)
        {
            int score = 0;
            int kingFile = kingPos % 8;

            for (int dFile = -1; dFile <= 1; dFile++)
            {
                int file = kingFile + dFile;
                if (file < 0 || file > 7) continue;

                short ourCount   = (color == ChessPieceColor.White) ? whitePawnCount[file] : blackPawnCount[file];
                short theirCount = (color == ChessPieceColor.White) ? blackPawnCount[file] : whitePawnCount[file];

                if (ourCount == 0)
                {
                    // Half-open file in front of our king — invitation to attack.
                    // Centre files (king's own and direct neighbours) hurt more than wings.
                    score -= (dFile == 0) ? 22 : 14;

                    if (theirCount == 0)
                    {
                        // Fully open file (no pawn either side) — even worse for the
                        // defender because heavy pieces have a clear lane to the king.
                        score -= 10;
                    }
                }
            }

            return score;
        }

        // King-zone attack pressure — count enemy-attacked squares in the 3x3 box
        // around the king (excluding the king's own square). Penalty grows
        // faster than linear because the 3rd+ attacker is when real mating
        // nets form — that's the signal the static eval was missing in games
        // 4, 7, and 9 (deep queen sorties + exposed king). Endgame is excluded;
        // king activity is wanted there. Returns a score in the king-owner's
        // POV — caller applies sign for white/black.
        internal static readonly short[] KingZoneAttackPenalty = { 0, -4, -12, -24, -40, -60, -80, -100 };

        private static int EvaluateKingZoneAttacks(Board board, byte kingPos, ChessPieceColor color)
        {
            bool[] enemyAttack = (color == ChessPieceColor.White) ? board.BlackAttackBoard : board.WhiteAttackBoard;

            int kingFile = kingPos % 8;
            int kingRank = kingPos / 8;
            int attacked = 0;

            for (int dr = -1; dr <= 1; dr++)
            {
                for (int df = -1; df <= 1; df++)
                {
                    if (dr == 0 && df == 0) continue;
                    int f = kingFile + df;
                    int r = kingRank + dr;
                    if (f < 0 || f > 7 || r < 0 || r > 7) continue;
                    if (enemyAttack[r * 8 + f]) attacked++;
                }
            }

            return KingZoneAttackPenalty[Math.Min(attacked, 7)];
        }

        // Pawn chains: a pawn defended by a friendly pawn on an adjacent file
        // (one rank back) is on a solid chain. AttackedValue/DefendedValue are
        // piece-value totals, so checking == 0 isn't enough — we want to know
        // specifically that a *pawn* is the defender. Easier to do the geometric
        // check directly. Returns chain bonus for one pawn (caller already
        // applied its sign via the per-piece loop).
        private static int PawnChainBonus(Board board, byte position, ChessPieceColor color)
        {
            int file = position % 8;
            // Square one rank back, one file over. For white, "back" means smaller
            // index (lower rank); for black, larger index.
            int backRankDelta = (color == ChessPieceColor.White) ? -8 : 8;

            int leftDefender  = position + backRankDelta - 1;
            int rightDefender = position + backRankDelta + 1;

            int score = 0;

            // Left defender (only if not wrapping around the board edge).
            if (file > 0 && leftDefender >= 0 && leftDefender < 64)
            {
                var p = board.Squares[leftDefender].Piece;
                if (p != null && p.PieceType == ChessPieceType.Pawn && p.PieceColor == color)
                    score += 5;
            }
            // Right defender.
            if (file < 7 && rightDefender >= 0 && rightDefender < 64)
            {
                var p = board.Squares[rightDefender].Piece;
                if (p != null && p.PieceType == ChessPieceType.Pawn && p.PieceColor == color)
                    score += 5;
            }
            return score;
        }
    }
}