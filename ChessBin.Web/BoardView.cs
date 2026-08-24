using ChessEngine.Engine;

namespace ChessBin.Web;

/// <summary>
/// Turns a position into the squares <c>ChessBoardView</c> renders. The game, the puzzle and
/// the analysis page all need this and it differs between them only in which squares count as
/// selected or highlighted, so those are parameters rather than three copies of the loop.
/// </summary>
public static class BoardView
{
    public static IReadOnlyList<BoardSquare> Squares(
        Engine engine,
        bool whiteAtBottom,
        Func<int, bool> isSelected,
        Func<int, bool> isLegalTarget,
        Func<int, bool> isLastMove)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var squares = new List<BoardSquare>(64);
        IEnumerable<int> rows = whiteAtBottom ? Enumerable.Range(0, 8) : Enumerable.Range(0, 8).Reverse();
        IEnumerable<int> columns = whiteAtBottom ? Enumerable.Range(0, 8) : Enumerable.Range(0, 8).Reverse();

        foreach (int row in rows)
        {
            foreach (int column in columns)
            {
                ChessPieceType type = engine.GetPieceTypeAt((byte)column, (byte)row);
                ChessPieceColor? color = type == ChessPieceType.None
                    ? null
                    : engine.GetPieceColorAt((byte)column, (byte)row);
                int index = column + row * 8;

                squares.Add(new BoardSquare(
                    column,
                    row,
                    type,
                    color,
                    isSelected(index),
                    isLegalTarget(index),
                    isLastMove(index)));
            }
        }

        return squares;
    }

    /// <summary>A board nobody is playing on — just a position with its last move marked.</summary>
    public static IReadOnlyList<BoardSquare> Squares(
        Engine engine, bool whiteAtBottom, int lastFrom = -1, int lastTo = -1) =>
        Squares(engine, whiteAtBottom,
            isSelected: _ => false,
            isLegalTarget: _ => false,
            isLastMove: i => i == lastFrom || i == lastTo);
}
