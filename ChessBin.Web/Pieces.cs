using ChessEngine.Engine;

namespace ChessBin.Web;

/// <summary>
/// Piece glyphs, in one place. The board renders them for squares and the promotion
/// dialog renders them for a bare type/colour pair, so the mapping can't live in either.
/// </summary>
public static class Pieces
{
    public static string Glyph(ChessPieceType type, ChessPieceColor color) => (type, color) switch
    {
        (ChessPieceType.King, ChessPieceColor.White) => "♔",
        (ChessPieceType.Queen, ChessPieceColor.White) => "♕",
        (ChessPieceType.Rook, ChessPieceColor.White) => "♖",
        (ChessPieceType.Bishop, ChessPieceColor.White) => "♗",
        (ChessPieceType.Knight, ChessPieceColor.White) => "♘",
        (ChessPieceType.Pawn, ChessPieceColor.White) => "♙",
        (ChessPieceType.King, ChessPieceColor.Black) => "♚",
        (ChessPieceType.Queen, ChessPieceColor.Black) => "♛",
        (ChessPieceType.Rook, ChessPieceColor.Black) => "♜",
        (ChessPieceType.Bishop, ChessPieceColor.Black) => "♝",
        (ChessPieceType.Knight, ChessPieceColor.Black) => "♞",
        (ChessPieceType.Pawn, ChessPieceColor.Black) => "♟",
        _ => string.Empty
    };

    public static string Glyph(BoardSquare square) =>
        Glyph(square.PieceType, square.PieceColor ?? ChessPieceColor.White);
}
