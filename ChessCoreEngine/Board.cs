using System;


namespace ChessEngine.Engine
{
    internal sealed class Board
    {
      
        internal Square[] Squares;
              
        internal bool InsufficientMaterial;

        internal int Score;

        internal ulong ZobristHash;       
       
        //Game Over Flags
        internal bool BlackCheck;
        internal bool BlackMate;
        internal bool WhiteCheck;
        internal bool WhiteMate;
        internal bool StaleMate;

        internal byte HalfMoveClock;
        internal byte RepeatedMove;

        internal bool BlackCastled;
        internal bool WhiteCastled;

        internal bool BlackCanCastle;
        internal bool WhiteCanCastle;

        internal bool EndGamePhase;

        internal MoveContent LastMove;

        internal byte WhiteKingPosition;
        internal byte BlackKingPosition;

        internal bool[] BlackAttackBoard;
        internal bool[] WhiteAttackBoard;

        //Who initated En Passant
        internal ChessPieceColor EnPassantColor;
        //Positions liable to En Passant
        internal byte EnPassantPosition;

        internal ChessPieceColor WhoseMove;
        
        internal int MoveCount;

        #region Constructors

        // FEN constructor

        internal Board(string fen) : this()
        {
            FenParser.ParsedFen parsed = FenParser.Parse(fen);

            WhoseMove = parsed.SideToMove;
            HalfMoveClock = (byte)parsed.HalfmoveClock;
            MoveCount = parsed.FullmoveNumber;
            EnPassantPosition = parsed.EnPassantSquare < 0
                ? (byte)0
                : (byte)parsed.EnPassantSquare;
            if (parsed.EnPassantSquare >= 0)
            {
                // The target rank identifies the side that made the double push.
                EnPassantColor = parsed.EnPassantSquare >= 40
                    ? ChessPieceColor.White
                    : ChessPieceColor.Black;
            }

            WhiteCastled = (parsed.CastleRights
                & (FenParser.WhiteKingSide | FenParser.WhiteQueenSide)) == 0;
            BlackCastled = (parsed.CastleRights
                & (FenParser.BlackKingSide | FenParser.BlackQueenSide)) == 0;

            for (int square = 0; square < parsed.PieceOn.Length; square++)
            {
                byte code = parsed.PieceOn[square];
                if (code == FenParser.Empty) continue;

                var piece = new Piece(
                    (ChessPieceType)(code % 6),
                    (ChessPieceColor)(code / 6))
                {
                    Moved = true
                };
                Squares[square].Piece = piece;
            }

            ApplyCastlingRight(parsed.CastleRights, FenParser.WhiteKingSide,
                60, 63, ChessPieceColor.White);
            ApplyCastlingRight(parsed.CastleRights, FenParser.WhiteQueenSide,
                60, 56, ChessPieceColor.White);
            ApplyCastlingRight(parsed.CastleRights, FenParser.BlackKingSide,
                4, 7, ChessPieceColor.Black);
            ApplyCastlingRight(parsed.CastleRights, FenParser.BlackQueenSide,
                4, 0, ChessPieceColor.Black);

            ZobristHash = Zobrist.ComputeHash(this);
        }

        private void ApplyCastlingRight(int rights, int right, int kingSquare,
                                        int rookSquare, ChessPieceColor color)
        {
            if ((rights & right) == 0) return;

            Piece king = Squares[kingSquare].Piece;
            if (king != null && king.PieceColor == color
                && king.PieceType == ChessPieceType.King)
                king.Moved = false;

            Piece rook = Squares[rookSquare].Piece;
            if (rook != null && rook.PieceColor == color
                && rook.PieceType == ChessPieceType.Rook)
                rook.Moved = false;
        }

        internal Board()
        {
            Squares = new Square[64];

            for (byte i = 0; i < 64; i++)
            {
                Squares[i] = new Square();
            }

            LastMove = new MoveContent();

            BlackCanCastle = true;
            WhiteCanCastle = true;
            
            WhiteAttackBoard = new bool[64];
            BlackAttackBoard = new bool[64];

        }

        private Board(Square[] squares)
        {
            Squares = new Square[64];

            for (byte x = 0; x < 64; x++)
            {
                if (squares[x].Piece != null)
                {
                    Squares[x].Piece = new Piece(squares[x].Piece);
                }
            }

            

            WhiteAttackBoard = new bool[64];
            BlackAttackBoard = new bool[64];

        }

        //Constructor
        internal Board(int score) : this()
        {
            Score = score;

            WhiteAttackBoard = new bool[64];
            BlackAttackBoard = new bool[64];

        }

        //Copy Constructor
        internal Board(Board board)
        {
            Squares = new Square[64];

            for (byte x = 0; x < 64; x++)
            {
                if (board.Squares[x].Piece != null)
                {
                    Squares[x] = new Square(board.Squares[x].Piece);
                }
            }

            WhiteAttackBoard = new bool[64];
            BlackAttackBoard = new bool[64];

            EndGamePhase = board.EndGamePhase;

            HalfMoveClock = board.HalfMoveClock;
            RepeatedMove = board.RepeatedMove;
           
            WhiteCastled = board.WhiteCastled;
            BlackCastled = board.BlackCastled;

            WhiteCanCastle = board.WhiteCanCastle;
            BlackCanCastle = board.BlackCanCastle;

            WhiteKingPosition = board.WhiteKingPosition;
            BlackKingPosition = board.BlackKingPosition;

            BlackCheck = board.BlackCheck;
            WhiteCheck = board.WhiteCheck;
            StaleMate = board.StaleMate;
            WhiteMate = board.WhiteMate;
            BlackMate = board.BlackMate;
            WhoseMove = board.WhoseMove;
            EnPassantPosition = board.EnPassantPosition;
            EnPassantColor = board.EnPassantColor;

            ZobristHash = board.ZobristHash;

            Score = board.Score;

            LastMove = new MoveContent(board.LastMove);

            MoveCount = board.MoveCount;
        }

        #endregion

        #region PrivateMethods

        private static bool PromotePawns(Board board, Piece piece, byte dstPosition, ChessPieceType promoteToPiece)
        {
            if (piece.PieceType == ChessPieceType.Pawn)
            {
                if (dstPosition < 8)
                {
                    board.Squares[dstPosition].Piece.PieceType = promoteToPiece;
                    board.Squares[dstPosition].Piece.PieceValue = Piece.CalculatePieceValue(promoteToPiece);
                    board.Squares[dstPosition].Piece.PieceActionValue = Piece.CalculatePieceActionValue(promoteToPiece);
                    return true;
                }
                if (dstPosition > 55)
                {
                    board.Squares[dstPosition].Piece.PieceType = promoteToPiece;
                    board.Squares[dstPosition].Piece.PieceValue = Piece.CalculatePieceValue(promoteToPiece);
                    board.Squares[dstPosition].Piece.PieceActionValue = Piece.CalculatePieceActionValue(promoteToPiece);
                    return true;
                }
            }

            return false;
        }

        private static void RecordEnPassant(ChessPieceColor pcColor, ChessPieceType pcType, Board board, byte srcPosition, byte dstPosition)
        {
            //Record En Passant if Pawn Moving
            if (pcType == ChessPieceType.Pawn)
            {
                //Reset HalfMoveClockCount if pawn moved
                board.HalfMoveClock = 0;

                int difference = srcPosition - dstPosition; 

                if (difference == 16 || difference == -16)
                {
                    board.EnPassantPosition = (byte)(dstPosition + (difference / 2));
                    board.EnPassantColor = pcColor;
                }
            }
        }

        private static bool SetEnpassantMove(Board board, byte srcPosition, byte dstPosition, ChessPieceColor pcColor)
        {
            if (board.EnPassantPosition != dstPosition)
            {
                return false;
            }

            if (pcColor == board.EnPassantColor)
            {
                return false;
            }

            if (board.Squares[srcPosition].Piece.PieceType != ChessPieceType.Pawn)
            {
                return false;
            }

            int pieceLocationOffset = 8;

            if (board.EnPassantColor == ChessPieceColor.White)
            {
                pieceLocationOffset = -8;
            }

            dstPosition = (byte)(dstPosition + pieceLocationOffset);

            Square sqr = board.Squares[dstPosition];

            board.LastMove.TakenPiece = new PieceTaken(sqr.Piece.PieceColor, sqr.Piece.PieceType, sqr.Piece.Moved, dstPosition);

            board.Squares[dstPosition].Piece = null;
                    
            //Reset HalfMoveClockCount if capture
            board.HalfMoveClock = 0;

            return true;

        }

        private static void KingCastle(Board board, Piece piece, byte srcPosition, byte dstPosition)
        {
            if (piece.PieceType != ChessPieceType.King)
            {
                return;
            }

            //Lets see if this is a casteling move.
            if (piece.PieceColor == ChessPieceColor.White && srcPosition == 60)
            {
                //Castle Right
                if (dstPosition == 62)
                {
                    //Ok we are casteling we need to move the Rook
                    if (board.Squares[63].Piece != null)
                    {
                        board.Squares[61].Piece = board.Squares[63].Piece;
                        board.Squares[63].Piece = null;
                        board.WhiteCastled = true;
                        board.LastMove.MovingPieceSecondary = new PieceMoving(board.Squares[61].Piece.PieceColor, board.Squares[61].Piece.PieceType, board.Squares[61].Piece.Moved, 63, 61);
                        board.Squares[61].Piece.Moved = true;
                        return;
                    }
                }
                //Castle Left
                else if (dstPosition == 58)
                {   
                    //Ok we are casteling we need to move the Rook
                    if (board.Squares[56].Piece != null)
                    {
                        board.Squares[59].Piece = board.Squares[56].Piece;
                        board.Squares[56].Piece = null;
                        board.WhiteCastled = true;
                        board.LastMove.MovingPieceSecondary = new PieceMoving(board.Squares[59].Piece.PieceColor, board.Squares[59].Piece.PieceType, board.Squares[59].Piece.Moved, 56, 59);
                        board.Squares[59].Piece.Moved = true;
                        return;
                    }
                }
            }
            else if (piece.PieceColor == ChessPieceColor.Black && srcPosition == 4)
            {
                if (dstPosition == 6)
                {
                    //Ok we are casteling we need to move the Rook
                    if (board.Squares[7].Piece != null)
                    {
                        board.Squares[5].Piece = board.Squares[7].Piece;
                        board.Squares[7].Piece = null;
                        board.BlackCastled = true;
                        board.LastMove.MovingPieceSecondary = new PieceMoving(board.Squares[5].Piece.PieceColor, board.Squares[5].Piece.PieceType, board.Squares[5].Piece.Moved, 7, 5);
                        board.Squares[5].Piece.Moved = true;
                        return;
                    }
                }
                    //Castle Left
                else if (dstPosition == 2)
                {
                    //Ok we are casteling we need to move the Rook
                    if (board.Squares[0].Piece != null)
                    {
                        board.Squares[3].Piece = board.Squares[0].Piece;
                        board.Squares[0].Piece = null;
                        board.BlackCastled = true;
                        board.LastMove.MovingPieceSecondary = new PieceMoving(board.Squares[3].Piece.PieceColor, board.Squares[3].Piece.PieceType, board.Squares[3].Piece.Moved, 0, 3);
                        board.Squares[3].Piece.Moved = true;
                        return;
                    }
                }
            }

            return;
        }

        #endregion

        #region InternalMethods

        internal static MoveContent MovePiece(Board board, byte srcPosition, byte dstPosition, ChessPieceType promoteToPiece)
        {
            Piece piece = board.Squares[srcPosition].Piece;

            //Record my last move
            board.LastMove = new MoveContent();

            

            if (piece.PieceColor == ChessPieceColor.Black)
            {
                board.MoveCount++;
            }
            //Add One to HalfMoveClockCount to check for 50 move limit.
            board.HalfMoveClock++;

            //En Passant
            if (board.EnPassantPosition > 0)
            {
                board.LastMove.EnPassantOccured = SetEnpassantMove(board, srcPosition, dstPosition, piece.PieceColor);
            }

            if (!board.LastMove.EnPassantOccured)
            {
                Square sqr = board.Squares[dstPosition];

                if (sqr.Piece != null)
                {
                    board.LastMove.TakenPiece = new PieceTaken(sqr.Piece.PieceColor, sqr.Piece.PieceType,
                                                               sqr.Piece.Moved, dstPosition);
                    board.HalfMoveClock = 0;
                }
                else
                {
                    board.LastMove.TakenPiece = new PieceTaken(ChessPieceColor.White, ChessPieceType.None, false,
                                                               dstPosition);
                    
                }
            }

            board.LastMove.MovingPiecePrimary = new PieceMoving(piece.PieceColor, piece.PieceType, piece.Moved, srcPosition, dstPosition);

            //Delete the piece in its source position
            board.Squares[srcPosition].Piece = null;
      
            //Add the piece to its new position
            piece.Moved = true;
            piece.Selected = false;
            board.Squares[dstPosition].Piece = piece;

            //Reset EnPassantPosition
            board.EnPassantPosition = 0;
          
            //Record En Passant if Pawn Moving
            if (piece.PieceType == ChessPieceType.Pawn)
            {
               board.HalfMoveClock = 0;
               RecordEnPassant(piece.PieceColor, piece.PieceType, board, srcPosition, dstPosition);
            }

            board.WhoseMove = board.WhoseMove == ChessPieceColor.White ? ChessPieceColor.Black : ChessPieceColor.White;

            KingCastle(board, piece, srcPosition, dstPosition);

            //Promote Pawns 
            if (PromotePawns(board, piece, dstPosition, promoteToPiece))
            {
                board.LastMove.PawnPromotedTo = promoteToPiece;
            }
            else
            {
                board.LastMove.PawnPromotedTo = ChessPieceType.None;
            }

            if ( board.HalfMoveClock >= 100)
            {
                board.StaleMate = true;
            }

            // Recompute the Zobrist hash from the post-move board state. We do
            // this fresh rather than incrementally so we don't have to thread
            // hash-updates through every special-case path (castle, en-passant
            // capture, promotion, …). Cheaper than the GenerateValidMoves call
            // that the caller does next.
            board.ZobristHash = Zobrist.ComputeHash(board);

            return board.LastMove;
        }

        private static string GetColumnFromByte(byte column)
        {
            switch (column)
            {
                case 0:
                    return "a";
                case 1:
                    return "b";
                case 2:
                    return "c";
                case 3:
                    return "d";
                case 4:
                    return "e";
                case 5:
                    return "f";
                case 6:
                    return "g";
                case 7:
                    return "h";
                default:
                    return "a";
            }
        }

        public new string ToString()
        {
            return Fen(false, this);
        }

        internal static string Fen(bool boardOnly, Board board)
        {
            string output = String.Empty;
            byte blankSquares = 0;

            for (byte x = 0; x < 64; x++)
            {
                byte index = x;

                if (board.Squares[index].Piece != null)
                {
                    if (blankSquares > 0)
                    {
                        output += blankSquares.ToString();
                        blankSquares = 0;
                    }

                    if (board.Squares[index].Piece.PieceColor == ChessPieceColor.Black)
                    {
                        output += Piece.GetPieceTypeShort(board.Squares[index].Piece.PieceType).ToLower();
                    }
                    else
                    {
                        output += Piece.GetPieceTypeShort(board.Squares[index].Piece.PieceType);
                    }
                }
                else
                {
                    blankSquares++;
                }

                if (x % 8 == 7)
                {
                    if (blankSquares > 0)
                    {
                        output += blankSquares.ToString();
                        output += "/";
                        blankSquares = 0;
                    }
                    else
                    {
                        if (x > 0 && x != 63)
                        {
                            output += "/";
                        }
                    }
                }
            }

            if (output.EndsWith("/"))
            {
                output = output.TrimEnd('/');
            }

            if (board.WhoseMove == ChessPieceColor.White)
            {
                output += " w ";
            }
            else
            {
                output += " b ";
            }

			string castle = "-";

            if (board.WhiteCastled == false)
            {
                if (board.Squares[60].Piece != null)
                {
                    if (board.Squares[60].Piece.Moved == false)
                    {
                        if (board.Squares[63].Piece != null)
                        {
                            if (board.Squares[63].Piece.Moved == false)
                            {
                                castle += "K";
                            }
                        }
                        if (board.Squares[56].Piece != null)
                        {
                            if (board.Squares[56].Piece.Moved == false)
                            {
                                castle += "Q";
                            }
                        }
                    }
                }
            }

            if (board.BlackCastled == false)
            {
                if (board.Squares[4].Piece != null)
                {
                    if (board.Squares[4].Piece.Moved == false)
                    {
                        if (board.Squares[7].Piece != null)
                        {
                            if (board.Squares[7].Piece.Moved == false)
                            {
                                castle += "k";
                            }
                        }
                        if (board.Squares[0].Piece != null)
                        {
                            if (board.Squares[0].Piece.Moved == false)
                            {
                                castle += "q";
                            }
                        }
                    }
                }
            }
			
			if (castle != "-")
			{
				castle = castle.TrimStart('-');
			}
			output += castle;

            if (board.EnPassantPosition != 0)
            {
                output += " " + GetColumnFromByte((byte)(board.EnPassantPosition % 8)) + "" + (byte)(8 - (byte)(board.EnPassantPosition / 8)) + " ";
            }
            else
            {
                output += " - ";
            }

            if (!boardOnly)
            {
                output += board.HalfMoveClock + " ";
                output += board.MoveCount;
            }
            return output.Trim();
        }

        #endregion
    }
}
