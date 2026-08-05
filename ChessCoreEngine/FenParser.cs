using System;
using System.Globalization;

namespace ChessEngine.Engine
{
    /// <summary>
    /// Parses the syntactic parts of a FEN shared by the mailbox Board and the
    /// bitboard Position. It deliberately does not reject composed or otherwise
    /// illegal chess positions; legality belongs to move generation, not FEN I/O.
    /// </summary>
    internal static class FenParser
    {
        internal const int WhiteKingSide = 1;
        internal const int WhiteQueenSide = 2;
        internal const int BlackKingSide = 4;
        internal const int BlackQueenSide = 8;
        internal const byte Empty = byte.MaxValue;

        internal sealed class ParsedFen
        {
            internal readonly byte[] PieceOn = new byte[64];
            internal ChessPieceColor SideToMove;
            internal int CastleRights;
            internal int EnPassantSquare = -1;
            internal int HalfmoveClock;
            internal int FullmoveNumber;
            internal bool HasMoveCounters;

            internal ParsedFen()
            {
                Array.Fill(PieceOn, Empty);
            }
        }

        internal static ParsedFen Parse(string fen)
        {
            if (fen == null) throw new ArgumentNullException(nameof(fen));

            string[] fields = fen.Trim().Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 4 && fields.Length != 6)
                throw Invalid("expected four or six fields");

            var parsed = new ParsedFen();
            ParsePlacement(fields[0], parsed.PieceOn);

            parsed.SideToMove = fields[1] switch
            {
                "w" => ChessPieceColor.White,
                "b" => ChessPieceColor.Black,
                _ => throw Invalid("active color must be 'w' or 'b'")
            };

            parsed.CastleRights = ParseCastling(fields[2]);
            parsed.EnPassantSquare = ParseEnPassant(fields[3], parsed.SideToMove);

            if (fields.Length == 6)
            {
                parsed.HalfmoveClock = ParseNumber(fields[4], "halfmove clock", 0, byte.MaxValue);
                // Standard games start at 1. Keep 0 for compatibility with the
                // engine's deliberately blank Board(), whose FEN serializes as 0.
                parsed.FullmoveNumber = ParseNumber(fields[5], "fullmove number", 0, int.MaxValue);
                parsed.HasMoveCounters = true;
            }

            return parsed;
        }

        private static void ParsePlacement(string placement, byte[] pieceOn)
        {
            string[] ranks = placement.Split('/');
            if (ranks.Length != 8)
                throw Invalid("piece placement must contain eight ranks");

            for (int rank = 0; rank < 8; rank++)
            {
                int file = 0;
                bool previousWasDigit = false;

                foreach (char symbol in ranks[rank])
                {
                    if (symbol >= '1' && symbol <= '8')
                    {
                        if (previousWasDigit)
                            throw Invalid("adjacent empty-square digits are not allowed");
                        file += symbol - '0';
                        if (file > 8)
                            throw Invalid("a rank contains more than eight squares");
                        previousWasDigit = true;
                        continue;
                    }

                    int code = PieceCode(symbol);
                    if (code < 0)
                        throw Invalid("piece placement contains an unknown piece");
                    if (file >= 8)
                        throw Invalid("a rank contains more than eight squares");

                    pieceOn[rank * 8 + file] = (byte)code;
                    file++;
                    previousWasDigit = false;
                }

                if (file != 8)
                    throw Invalid("each rank must contain exactly eight squares");
            }
        }

        private static int PieceCode(char symbol)
        {
            ChessPieceType type = char.ToLowerInvariant(symbol) switch
            {
                'k' => ChessPieceType.King,
                'q' => ChessPieceType.Queen,
                'r' => ChessPieceType.Rook,
                'b' => ChessPieceType.Bishop,
                'n' => ChessPieceType.Knight,
                'p' => ChessPieceType.Pawn,
                _ => ChessPieceType.None
            };
            if (type == ChessPieceType.None) return -1;

            ChessPieceColor color = char.IsUpper(symbol)
                ? ChessPieceColor.White
                : ChessPieceColor.Black;
            return (int)color * 6 + (int)type;
        }

        private static int ParseCastling(string field)
        {
            if (field == "-") return 0;
            if (field.Length == 0 || field.Length > 4 || field.Contains('-'))
                throw Invalid("invalid castling rights");

            int rights = 0;
            foreach (char symbol in field)
            {
                int right = symbol switch
                {
                    'K' => WhiteKingSide,
                    'Q' => WhiteQueenSide,
                    'k' => BlackKingSide,
                    'q' => BlackQueenSide,
                    _ => throw Invalid("invalid castling rights")
                };
                if ((rights & right) != 0)
                    throw Invalid("castling rights must not be repeated");
                rights |= right;
            }
            return rights;
        }

        private static int ParseEnPassant(string field, ChessPieceColor sideToMove)
        {
            if (field == "-") return -1;
            if (field.Length != 2 || field[0] < 'a' || field[0] > 'h'
                || (field[1] != '3' && field[1] != '6'))
                throw Invalid("en-passant target must be '-' or a square on rank 3 or 6");

            int rank = field[1] - '0';
            if ((sideToMove == ChessPieceColor.White && rank != 6)
                || (sideToMove == ChessPieceColor.Black && rank != 3))
                throw Invalid("en-passant target rank is inconsistent with the active color");

            int file = field[0] - 'a';
            return (8 - rank) * 8 + file;
        }

        private static int ParseNumber(string field, string name, int minimum, int maximum)
        {
            if (!int.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                || value < minimum || value > maximum)
                throw Invalid(name + " is outside the supported range");
            return value;
        }

        private static FormatException Invalid(string reason)
            => new FormatException("Invalid FEN: " + reason + ".");
    }
}
