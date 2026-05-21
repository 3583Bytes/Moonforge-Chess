using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace ChessEngine.Engine
{
    internal static class Search
    {
        internal static int progress;
		
		private static int piecesRemaining;
		

        private struct Position
        {
            internal byte SrcPosition;
            internal byte DstPosition;
            internal int Score;
            //internal bool TopSort;
            internal string Move;

            public new string ToString()
            {
                return Move;
            }

        }

        private static readonly Position[,] KillerMove = new Position[3,20];
        private static int kIndex;

        private static int Sort(Position s2, Position s1)
        {
            return (s1.Score).CompareTo(s2.Score);
        }

        private static int Sort(Board s2, Board s1)
        {
            return (s1.Score).CompareTo(s2.Score);
        }

        private static int SideToMoveScore(int score, ChessPieceColor color)
        {
            if (color == ChessPieceColor.Black)
                return -score;

            return score;
        }

       

        internal static MoveContent IterativeSearch(Board examineBoard, byte depth, ref int nodesSearched, ref int nodesQuiessence, ref string pvLine, ref byte plyDepthReached, ref byte rootMovesSearched, List<OpeningMove> currentGameBook, out int searchScore)
        {
            List<Position> pvChild = new List<Position>();
            int alpha = -400000000;
            const int beta = 400000000;
            searchScore = 0;

            MoveContent bestMove = new MoveContent();

            //We are going to store our result boards here           
            ResultBoards succ = GetSortValidMoves(examineBoard);

            rootMovesSearched = (byte)succ.Positions.Count;

            if (rootMovesSearched == 1)
            {
                //I only have one move
                searchScore = succ.Positions[0].Score;
                return succ.Positions[0].LastMove;
            }

            //Can I make an instant mate?
            foreach (Board pos in succ.Positions)
            {
                int value = -AlphaBeta(pos, 1, -beta, -alpha, ref nodesSearched, ref nodesQuiessence, ref pvChild, true, true);

                if (value >= 32767)
                {
                    searchScore = value;
                    return pos.LastMove;
                }
            }

            int currentBoard = 0;

            alpha = -400000000;

            succ.Positions.Sort(Sort);

            depth--;

            plyDepthReached = ModifyDepth(depth, succ.Positions.Count);

            foreach (Board pos in succ.Positions)
            {
                currentBoard++;

				progress = (int)((currentBoard / (decimal)succ.Positions.Count) * 100);

                pvChild = new List<Position>();

                int value = -AlphaBeta(pos, depth, -beta, -alpha, ref nodesSearched, ref nodesQuiessence, ref pvChild, false, true);

                // Don't short-circuit on the first mate: alpha-beta with depth-adjusted
                // mate scores (see AlphaBeta:232) will naturally pick the shortest one
                // because faster mates have higher scores.

                if (examineBoard.RepeatedMove == 2)
                {
                    string fen = Board.Fen(true, pos);

                    foreach (OpeningMove move in currentGameBook)
                    {
                        if (move.EndingFEN == fen)
                        {
                            value = 0;
                            break;
                        }
                    }
                }

                pos.Score = value;

                //If value is greater then alpha this is the best board
                if (value > alpha || alpha == -400000000)
                {
                    pvLine = pos.LastMove.ToString();

                    foreach (Position pvPos in pvChild)
                    {
                        pvLine += " " + pvPos.ToString();
                    }

                    alpha = value;
                    bestMove = pos.LastMove;
                }
            }

            plyDepthReached++;
			progress=100;

            searchScore = alpha;
            return bestMove;
        }

        private static ResultBoards GetSortValidMoves(Board examineBoard)
        {
            ResultBoards succ = new ResultBoards
                                    {
                                        Positions = new List<Board>(30)
                                    };

            piecesRemaining = 0;

            for (byte x = 0; x < 64; x++)
            {
                Square sqr = examineBoard.Squares[x];

                //Make sure there is a piece on the square
                if (sqr.Piece == null)
                    continue;

                piecesRemaining++;

                //Make sure the color is the same color as the one we are moving.
                if (sqr.Piece.PieceColor != examineBoard.WhoseMove)
                    continue;

                //For each valid move for this piece
                foreach (byte dst in sqr.Piece.ValidMoves)
                {
                    //We make copies of the board and move so that we can move it without effecting the parent board
                    Board board = examineBoard.FastCopy();

                    //Make move so we can examine it
                    Board.MovePiece(board, x, dst, ChessPieceType.Queen);

                    //We Generate Valid Moves for Board
                    PieceValidMoves.GenerateValidMoves(board);

                    //Invalid Move
                    if (board.WhiteCheck && examineBoard.WhoseMove == ChessPieceColor.White)
                    {
                        continue;
                    }

                    //Invalid Move
                    if (board.BlackCheck && examineBoard.WhoseMove == ChessPieceColor.Black)
                    {
                        continue;
                    }

                    //We calculate the board score
                    Evaluation.EvaluateBoardScore(board);

                    //Invert Score to support Negamax
                    board.Score = SideToMoveScore(board.Score, board.WhoseMove);

                    succ.Positions.Add(board);
                }
            }

            succ.Positions.Sort(Sort);
            return succ;
        }

        private static int AlphaBeta(Board examineBoard, byte depth, int alpha, int beta, ref int nodesSearched, ref int nodesQuiessence, ref List<Position> pvLine, bool extended, bool allowNullMove)
        {
            nodesSearched++;

            if (examineBoard.HalfMoveClock >= 100 || examineBoard.RepeatedMove >= 3)
                return 0;

            //End Main Search with Quiescence
            if (depth == 0)
            {
                if (!extended && (examineBoard.BlackCheck || examineBoard.WhiteCheck))
                {
                    depth++;
                    extended = true;
                }
                else
                {
                    //Perform a Quiessence Search
                    return Quiescence(examineBoard, alpha, beta, ref nodesQuiessence, 0);
                }
            }

            // Null move pruning. Hand the opponent a free move; if even then the
            // search comes back >= beta, the position is too good for us to need
            // to search properly — the opponent can't refute it from here.
            // Skip when:
            //   * in check (a null leaves our king attacked, illegal)
            //   * depth too low for the reduction to pay off
            //   * caller was itself a null (avoids double-null which proves nothing)
            //   * few pieces left (zugzwang: "doing nothing" can genuinely be best
            //     in K+P endgames, so NMP gives wrong cutoffs)
            //   * |beta| near mate (cutoffs against mate scores are unreliable)
            const byte NullR = 2;
            if (allowNullMove
                && depth >= 1 + NullR
                && !(examineBoard.WhiteCheck || examineBoard.BlackCheck)
                && piecesRemaining > 6
                && Math.Abs(beta) < 30000)
            {
                Board nullBoard = examineBoard.FastCopy();
                nullBoard.WhoseMove = examineBoard.WhoseMove == ChessPieceColor.White
                    ? ChessPieceColor.Black
                    : ChessPieceColor.White;
                // The en-passant target was set up by our prior move's
                // pawn-push; if we pass instead, that target is no longer
                // legitimately capturable. Clear it so the null search doesn't
                // see an illegal en-passant capture as legal.
                nullBoard.EnPassantPosition = 0;
                PieceValidMoves.GenerateValidMoves(nullBoard);

                List<Position> nullPv = new List<Position>();
                int nullScore = -AlphaBeta(nullBoard, (byte)(depth - 1 - NullR), -beta, -beta + 1,
                    ref nodesSearched, ref nodesQuiessence, ref nullPv, extended, false);
                if (nullScore >= beta)
                    return beta;
            }

            List<Position> positions = EvaluateMoves(examineBoard, depth);

            if (examineBoard.WhiteCheck || examineBoard.BlackCheck || positions.Count == 0)
            {
                if (SearchForMate(examineBoard.WhoseMove, examineBoard, ref examineBoard.BlackMate, ref examineBoard.WhiteMate, ref examineBoard.StaleMate))
                {
                    if (examineBoard.BlackMate)
                    {
                        if (examineBoard.WhoseMove == ChessPieceColor.Black)
                            return -32767-depth;

                        return 32767 + depth;
                    }
                    if (examineBoard.WhiteMate)
                    {
                        if (examineBoard.WhoseMove == ChessPieceColor.Black)
                            return 32767 + depth;

                        return -32767 - depth;
                    }

                    //If Not Mate then StaleMate
                    return 0;
                }
            }

            positions.Sort(Sort);

            foreach (Position move in positions)
            {
                List<Position> pvChild = new List<Position>();

                //Make a copy
                Board board = examineBoard.FastCopy();

                //Move Piece
                Board.MovePiece(board, move.SrcPosition, move.DstPosition, ChessPieceType.Queen);

                //We Generate Valid Moves for Board
                PieceValidMoves.GenerateValidMoves(board);

                if (board.BlackCheck)
                {
                    if (examineBoard.WhoseMove == ChessPieceColor.Black)
                    {
                        //Invalid Move
                        continue;
                    }
                }

                if (board.WhiteCheck)
                {
                    if (examineBoard.WhoseMove == ChessPieceColor.White)
                    {
                        //Invalid Move
                        continue;
                    }
                }

                int value = -AlphaBeta(board, (byte)(depth - 1), -beta, -alpha, ref nodesSearched, ref nodesQuiessence, ref pvChild, extended, true);

                if (value >= beta)
                {
                    KillerMove[kIndex, depth].SrcPosition = move.SrcPosition;
                    KillerMove[kIndex, depth].DstPosition = move.DstPosition;

                    kIndex = ((kIndex + 1) % 2);

                    
                    return beta;
                }
                if (value > alpha)
                {
                    Position pvPos = new Position();

                    pvPos.SrcPosition = board.LastMove.MovingPiecePrimary.SrcPosition;
                    pvPos.DstPosition = board.LastMove.MovingPiecePrimary.DstPosition;
                    pvPos.Move = board.LastMove.ToString();

                    pvChild.Insert(0, pvPos);

                    pvLine = pvChild;

                    alpha = (int)value;
                }
            }

            return alpha;
        }

        private static int Quiescence(Board examineBoard, int alpha, int beta, ref int nodesSearched, int qsPly)
        {
            nodesSearched++;

            //Evaluate Score
            Evaluation.EvaluateBoardScore(examineBoard);

            //Invert Score to support Negamax
            examineBoard.Score = SideToMoveScore(examineBoard.Score, examineBoard.WhoseMove);

            if (examineBoard.Score >= beta)
                return beta;

            if (examineBoard.Score > alpha)
                alpha = examineBoard.Score;


            List<Position> positions;
            bool inCheck = examineBoard.WhiteCheck || examineBoard.BlackCheck;
            // At the first quiescence ply also consider non-capture *knight*
            // checks. This catches forking tactics like the Nf3+ pattern from
            // game 1 (knight at e5 -> f3, forks K+R) that captures-only qsearch
            // misses. Bounded to qsPly == 0 so the tree doesn't explode, and
            // bounded to knights so we can pre-filter cheaply with the
            // precomputed KnightMoves table — full slider-check detection
            // needs blocker walks and isn't worth the per-node cost yet.
            bool includeChecks = qsPly == 0 && !inCheck;

            byte enemyKingPos = examineBoard.WhoseMove == ChessPieceColor.White
                ? examineBoard.BlackKingPosition
                : examineBoard.WhiteKingPosition;

            if (inCheck)
            {
                positions = EvaluateMoves(examineBoard, 0);
            }
            else if (includeChecks)
            {
                positions = EvaluateMovesQPlusKnightChecks(examineBoard, enemyKingPos);
            }
            else
            {
                positions = EvaluateMovesQ(examineBoard);
            }

            if (positions.Count == 0)
            {
                return examineBoard.Score;
            }

            positions.Sort(Sort);

            foreach (Position move in positions)
            {
                bool isCapture = examineBoard.Squares[move.DstPosition].Piece != null;

                // Skip captures that look like material losses; keep equal/winning ones.
                // Non-captures emitted by the generators (inCheck evasions, knight
                // checks at qsPly==0) bypass SEE.
                if (isCapture && StaticExchangeEvaluation(examineBoard.Squares[move.DstPosition]) < 0)
                {
                    continue;
                }

                //Make a copy
                Board board = examineBoard.FastCopy();

                //Move Piece
                Board.MovePiece(board, move.SrcPosition, move.DstPosition, ChessPieceType.Queen);

                //We Generate Valid Moves for Board
                PieceValidMoves.GenerateValidMoves(board);

                if (board.BlackCheck)
                {
                    if (examineBoard.WhoseMove == ChessPieceColor.Black)
                    {
                        //Invalid Move
                        continue;
                    }
                }

                if (board.WhiteCheck)
                {
                    if (examineBoard.WhoseMove == ChessPieceColor.White)
                    {
                        //Invalid Move
                        continue;
                    }
                }

                int value = -Quiescence(board, -beta, -alpha, ref nodesSearched, qsPly + 1);

                if (value >= beta)
                {
                    KillerMove[2, 0].SrcPosition = move.SrcPosition;
                    KillerMove[2, 0].DstPosition = move.DstPosition;

                    return beta;
                }
                if (value > alpha)
                {
                    alpha = value;
                }
            }

            return alpha;
        }

        private static List<Position> EvaluateMoves(Board examineBoard, byte depth)
        {

            //We are going to store our result boards here           
            List<Position> positions = new List<Position>();

            //bool foundPV = false;


            for (byte x = 0; x < 64; x++)
            {
                Piece piece = examineBoard.Squares[x].Piece;

                //Make sure there is a piece on the square
                if (piece == null)
                    continue;

                //Make sure the color is the same color as the one we are moving.
                if (piece.PieceColor != examineBoard.WhoseMove)
                    continue;

                //For each valid move for this piece
                foreach (byte dst in piece.ValidMoves)
                {
                    Position move = new Position();

                    move.SrcPosition = x;
                    move.DstPosition = dst;
				
                    if (move.SrcPosition == KillerMove[0, depth].SrcPosition && move.DstPosition == KillerMove[0, depth].DstPosition)
                    {
                        //move.TopSort = true;
                        move.Score += 5000;
                        positions.Add(move);
                        continue;
                    }
                    if (move.SrcPosition == KillerMove[1, depth].SrcPosition && move.DstPosition == KillerMove[1, depth].DstPosition)
                    {
                        //move.TopSort = true;
                        move.Score += 5000;
                        positions.Add(move);
                        continue;
                    }

                    Piece pieceAttacked = examineBoard.Squares[move.DstPosition].Piece;

                    //If the move is a capture add it's value to the score
                    if (pieceAttacked != null)
                    {
                        move.Score += pieceAttacked.PieceValue;

                        if (piece.PieceValue < pieceAttacked.PieceValue)
                        {
                            move.Score += pieceAttacked.PieceValue - piece.PieceValue;
                        }
                    }

                    if (!piece.Moved)
                    {
                        move.Score += 10;
                    }

                    move.Score += piece.PieceActionValue;

                    //Add Score for Castling
                    if (!examineBoard.WhiteCastled && examineBoard.WhoseMove == ChessPieceColor.White)
                    {

                        if (piece.PieceType == ChessPieceType.King)
                        {
                            if (move.DstPosition != 62 && move.DstPosition != 58)
                            {
                                move.Score -= 40;
                            }
                            else
                            {
                                move.Score += 40;
                            }
                        }
                        if (piece.PieceType == ChessPieceType.Rook)
                        {
                            move.Score -= 40;
                        }
                    }

                    if (!examineBoard.BlackCastled && examineBoard.WhoseMove == ChessPieceColor.Black)
                    {
                        if (piece.PieceType == ChessPieceType.King)
                        {
                            if (move.DstPosition != 6 && move.DstPosition != 2)
                            {
                                move.Score -= 40;
                            }
                            else
                            {
                                move.Score += 40;
                            }
                        }
                        if (piece.PieceType == ChessPieceType.Rook)
                        {
                            move.Score -= 40;
                        }
                    }

                    positions.Add(move);
                }
            }

            return positions;
        }

        // Captures + non-capture knight checks (squares from which a knight
        // would attack enemyKingPos). Used at the first quiescence ply when
        // not already in check, so the tree picks up tactical knight forks
        // (the Game 1 Nf3+ pattern) without paying the full EvaluateMoves
        // cost. Sliders/pawn checks omitted — too expensive to detect cheaply.
        private static List<Position> EvaluateMovesQPlusKnightChecks(Board examineBoard, byte enemyKingPos)
        {
            List<Position> positions = new List<Position>();
            List<byte> knightCheckSquares = MoveArrays.KnightMoves[enemyKingPos].Moves;
            ChessPieceColor color = examineBoard.WhoseMove;

            for (byte x = 0; x < 64; x++)
            {
                Piece piece = examineBoard.Squares[x].Piece;
                if (piece == null) continue;
                if (piece.PieceColor != color) continue;

                bool isKnight = piece.PieceType == ChessPieceType.Knight;

                foreach (byte dst in piece.ValidMoves)
                {
                    Piece pieceAttacked = examineBoard.Squares[dst].Piece;
                    bool isCapture = pieceAttacked != null;

                    if (!isCapture)
                    {
                        // Non-capture: only emit if it's a knight giving check.
                        if (!isKnight) continue;
                        if (!knightCheckSquares.Contains(dst)) continue;
                    }

                    Position move = new Position();
                    move.SrcPosition = x;
                    move.DstPosition = dst;

                    if (move.SrcPosition == KillerMove[2, 0].SrcPosition && move.DstPosition == KillerMove[2, 0].DstPosition)
                    {
                        move.Score += 5000;
                        positions.Add(move);
                        continue;
                    }

                    if (isCapture)
                    {
                        move.Score += pieceAttacked.PieceValue;
                        if (piece.PieceValue < pieceAttacked.PieceValue)
                        {
                            move.Score += pieceAttacked.PieceValue - piece.PieceValue;
                        }
                    }

                    move.Score += piece.PieceActionValue;
                    positions.Add(move);
                }
            }

            return positions;
        }

        private static List<Position> EvaluateMovesQ(Board examineBoard)
        {
            //We are going to store our result boards here           
            List<Position> positions = new List<Position>();

            for (byte x = 0; x < 64; x++)
            {
                Piece piece = examineBoard.Squares[x].Piece;

                //Make sure there is a piece on the square
                if (piece == null)
                    continue;

                //Make sure the color is the same color as the one we are moving.
                if (piece.PieceColor != examineBoard.WhoseMove)
                    continue;

                //For each valid move for this piece
                foreach (byte dst in piece.ValidMoves)
                {
                    if (examineBoard.Squares[dst].Piece == null)
                    {
                        continue;
                    }

                    Position move = new Position();

                    move.SrcPosition = x;
                    move.DstPosition = dst;

                    if (move.SrcPosition == KillerMove[2, 0].SrcPosition && move.DstPosition == KillerMove[2, 0].DstPosition)
                    {
                        //move.TopSort = true;
                        move.Score += 5000;
                        positions.Add(move);
                        continue;
                    }

                    Piece pieceAttacked = examineBoard.Squares[move.DstPosition].Piece;

                    move.Score += pieceAttacked.PieceValue;

                    if (piece.PieceValue < pieceAttacked.PieceValue)
                    {
                        move.Score += pieceAttacked.PieceValue - piece.PieceValue;
                    }

                    move.Score += piece.PieceActionValue;


                    positions.Add(move);
                }
            }

            return positions;
        }

        internal static bool SearchForMate(ChessPieceColor movingSide, Board examineBoard, ref bool blackMate, ref bool whiteMate, ref bool staleMate)
        {
            bool foundNonCheckBlack = false;
            bool foundNonCheckWhite = false;

            for (byte x = 0; x < 64; x++)
            {
                Square sqr = examineBoard.Squares[x];

                //Make sure there is a piece on the square
                if (sqr.Piece == null)
                    continue;

                //Make sure the color is the same color as the one we are moving.
                if (sqr.Piece.PieceColor != movingSide)
                    continue;

                //For each valid move for this piece
                foreach (byte dst in sqr.Piece.ValidMoves)
                {

                    //We make copies of the board and move so that we can move it without effecting the parent board
                    Board board = examineBoard.FastCopy();

                    //Make move so we can examine it
                    Board.MovePiece(board, x, dst, ChessPieceType.Queen);

                    //We Generate Valid Moves for Board
                    PieceValidMoves.GenerateValidMoves(board);

                    if (board.BlackCheck == false)
                    {
                        foundNonCheckBlack = true;
                    }
                    else if (movingSide == ChessPieceColor.Black)
                    {
                        continue;
                    }

                    if (board.WhiteCheck == false )
                    {
                        foundNonCheckWhite = true;
                    }
                    else if (movingSide == ChessPieceColor.White)
                    {
                        continue;
                    }
                }
            }

            if (foundNonCheckBlack == false)
            {
                if (examineBoard.BlackCheck)
                {
                    blackMate = true;
                    return true;
                }
                if (!examineBoard.WhiteMate && movingSide != ChessPieceColor.White)
                {
                    staleMate = true;
                    return true;
                }
            }

            if (foundNonCheckWhite == false)
            {
                if (examineBoard.WhiteCheck)
                {
                    whiteMate = true;
                    return true;
                }
                if (!examineBoard.BlackMate && movingSide != ChessPieceColor.Black)
                {
                    staleMate = true;
                    return true;
                }
            }

            return false;
           
        }

        private static byte ModifyDepth(byte depth, int possibleMoves)
        {
            if (possibleMoves <= 20 || piecesRemaining < 14)
            {
                if (possibleMoves <= 10 || piecesRemaining < 6)
                {
                    depth += 1;
                }

                depth += 1;
            }

            return depth;
        }

        private static int StaticExchangeEvaluation(Square examineSquare)
        {
            if (examineSquare.Piece == null)
            {
                return 0;
            }
            if (examineSquare.Piece.AttackedValue == 0)
            {
                return 0;
            }

            return examineSquare.Piece.PieceActionValue - examineSquare.Piece.AttackedValue + examineSquare.Piece.DefendedValue;
        }

    }
}
