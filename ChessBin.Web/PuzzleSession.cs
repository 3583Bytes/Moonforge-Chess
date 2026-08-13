using ChessEngine.Engine;

namespace ChessBin.Web;

public enum PuzzleOutcome
{
    /// <summary>Still being solved.</summary>
    Solving,
    /// <summary>The player found the whole line.</summary>
    Solved,
    /// <summary>The player gave up and the line was played out for them.</summary>
    Revealed,
}

/// <summary>
/// Drives one daily puzzle: tracks how far through the solution the player has got,
/// judges each attempted move, and plays the opponent's scripted replies. No search
/// is involved — every reply is already in <see cref="PuzzleRecord.Solution"/>.
/// </summary>
public sealed class PuzzleSession
{
    /// <summary>How long the opponent's scripted reply waits, so it reads as a move rather than a jump.</summary>
    public const int ReplyDelayMs = 420;

    private Engine _engine = new();
    private PuzzleRecord? _puzzle;
    private int _ply;
    private int? _selectedSquare;
    private int _lastFrom = -1;
    private int _lastTo = -1;
    private readonly HashSet<int> _legalTargets = [];
    private readonly List<string> _line = [];

    public event Action? StateChanged;

    public PuzzleRecord? Puzzle => _puzzle;
    public PuzzleOutcome Outcome { get; private set; } = PuzzleOutcome.Solving;
    public string Status { get; private set; } = "Loading today's puzzle…";
    public int Mistakes { get; private set; }
    public bool HintUsed { get; private set; }
    public int? HintSquare { get; private set; }
    public ChessPieceColor SolverColor { get; private set; } = ChessPieceColor.White;
    public bool WhiteAtBottom => SolverColor == ChessPieceColor.White;
    public EvaluationBreakdown StartEvaluation { get; private set; }
    public EvaluationBreakdown Evaluation { get; private set; }
    public bool IsLoaded => _puzzle is not null;
    public bool IsComplete => Outcome != PuzzleOutcome.Solving;

    /// <summary>The position as it currently stands.</summary>
    public string Fen => _engine.FEN;

    /// <summary>True when the line finished in checkmate, where an evaluation number says nothing useful.</summary>
    public bool EndedInMate { get; private set; }
    public bool IsSolverTurn => !IsComplete && IsLoaded && _ply % 2 == 0;

    /// <summary>Notation for the moves played so far, for the solved-state summary.</summary>
    public IReadOnlyList<string> Line => _line;

    public int SolverMovesMade => (_ply + 1) / 2;
    public int SolverMovesTotal => _puzzle?.SolverMoveCount ?? 0;

    /// <summary>Solved without a wrong move or a hint.</summary>
    public bool IsClean => Outcome == PuzzleOutcome.Solved && Mistakes == 0 && !HintUsed;

    /// <summary>
    /// How far the position moved in the solver's favour, in centipawns. The engine scores
    /// from White's point of view, so flip it when the player has Black.
    /// </summary>
    public int EvaluationSwing =>
        (Evaluation.Total - StartEvaluation.Total) * (SolverColor == ChessPieceColor.White ? 1 : -1);

    public void Load(PuzzleRecord puzzle)
    {
        ArgumentNullException.ThrowIfNull(puzzle);

        _puzzle = puzzle;
        _engine = new Engine(puzzle.Fen);
        _engine.GenerateValidMoves();
        SolverColor = _engine.WhoseMove;
        _ply = 0;
        Mistakes = 0;
        HintUsed = false;
        HintSquare = null;
        Outcome = PuzzleOutcome.Solving;
        EndedInMate = false;
        _line.Clear();
        ClearSelection();

        // Highlight the move that created the puzzle, so the player can see what just happened.
        (_lastFrom, _lastTo) = SquaresOf(puzzle.LastMove);

        StartEvaluation = _engine.GetEvaluationBreakdown();
        Evaluation = StartEvaluation;

        string side = SolverColor == ChessPieceColor.White ? "White" : "Black";
        Status = SolverMovesTotal == 1
            ? $"{side} to play — find the move."
            : $"{side} to play — find the first of {SolverMovesTotal} moves.";

        NotifyStateChanged();
    }

    public IReadOnlyList<BoardSquare> GetDisplaySquares()
    {
        var squares = new List<BoardSquare>(64);
        IEnumerable<int> rows = WhiteAtBottom ? Enumerable.Range(0, 8) : Enumerable.Range(0, 8).Reverse();
        IEnumerable<int> columns = WhiteAtBottom ? Enumerable.Range(0, 8) : Enumerable.Range(0, 8).Reverse();

        foreach (int row in rows)
        {
            foreach (int column in columns)
            {
                var type = _engine.GetPieceTypeAt((byte)column, (byte)row);
                ChessPieceColor? color = type == ChessPieceType.None
                    ? null
                    : _engine.GetPieceColorAt((byte)column, (byte)row);
                int index = column + row * 8;

                squares.Add(new BoardSquare(
                    column,
                    row,
                    type,
                    color,
                    index == _selectedSquare || index == HintSquare,
                    _legalTargets.Contains(index),
                    index == _lastFrom || index == _lastTo));
            }
        }

        return squares;
    }

    public async Task ClickSquareAsync(int column, int row)
    {
        if (!IsSolverTurn) return;

        int clicked = column + row * 8;
        var clickedType = _engine.GetPieceTypeAt((byte)column, (byte)row);
        ChessPieceColor? clickedColor = clickedType == ChessPieceType.None
            ? null
            : _engine.GetPieceColorAt((byte)column, (byte)row);

        if (_selectedSquare is int selected && _legalTargets.Contains(clicked))
        {
            await AttemptAsync(selected % 8, selected / 8, column, row);
            return;
        }

        if (clickedColor == SolverColor)
        {
            SelectSquare(column, row);
            NotifyStateChanged();
            return;
        }

        ClearSelection();
        Status = "Select one of your own pieces.";
        NotifyStateChanged();
    }

    /// <summary>Lights up the piece that moves next, at the cost of a clean solve.</summary>
    public void ShowHint()
    {
        if (!IsSolverTurn || _puzzle is null) return;

        HintUsed = true;
        (HintSquare, _) = SquaresOf(_puzzle.Solution[_ply]);
        ClearSelection();
        Status = "The piece to move is highlighted.";
        NotifyStateChanged();
    }

    /// <summary>Gives up and plays out the rest of the line.</summary>
    public async Task RevealAsync()
    {
        if (_puzzle is null || IsComplete) return;

        ClearSelection();
        Outcome = PuzzleOutcome.Revealed;

        while (_ply < _puzzle.Solution.Length)
        {
            Apply(_puzzle.Solution[_ply]);
            _ply++;
            Evaluation = _engine.GetEvaluationBreakdown();
            Status = "Here is how it went.";
            NotifyStateChanged();
            if (_ply < _puzzle.Solution.Length) await Task.Delay(ReplyDelayMs);
        }

        EndedInMate = _engine.GetWhiteMate() || _engine.GetBlackMate();
        Status = "Solution shown. Come back tomorrow for a new one.";
        NotifyStateChanged();
    }

    private async Task AttemptAsync(int fromColumn, int fromRow, int toColumn, int toRow)
    {
        if (_puzzle is null) return;

        string expected = _puzzle.Solution[_ply];
        string played = SquareName(fromColumn, fromRow) + SquareName(toColumn, toRow);
        bool isFinalSolverMove = _ply == _puzzle.Solution.Length - 1;

        ClearSelection();
        HintSquare = null;

        if (played == expected[..4])
        {
            // The solution's own promotion choice applies. Every promotion in the shipped
            // set is to a queen (PuzzleDataTests guards that); an underpromotion puzzle
            // would need a picker here, because the choice would be the puzzle.
            Apply(expected);
            _ply++;
            Evaluation = _engine.GetEvaluationBreakdown();

            if (_ply >= _puzzle.Solution.Length)
            {
                Finish();
                return;
            }

            Status = "Correct. Watch the reply…";
            NotifyStateChanged();

            await Task.Delay(ReplyDelayMs);

            Apply(_puzzle.Solution[_ply]);
            _ply++;
            Evaluation = _engine.GetEvaluationBreakdown();
            Status = $"Your move — {SolverMovesTotal - SolverMovesMade} to go.";
            NotifyStateChanged();
            return;
        }

        // A different move that still mates is a real solution, not a mistake. Only the
        // last move of the line can qualify, so this is the only place worth testing.
        if (isFinalSolverMove && TryAlternativeMate(fromColumn, fromRow, toColumn, toRow))
        {
            _ply++;
            Evaluation = _engine.GetEvaluationBreakdown();
            Finish(alternative: true);
            return;
        }

        Mistakes++;
        Status = Mistakes == 1
            ? "Not that one. Look for something more forcing."
            : $"Still not it — {Mistakes} tries so far.";
        NotifyStateChanged();
    }

    /// <summary>
    /// Plays a candidate move to see whether it delivers mate, and restores the position
    /// if it doesn't. Restoring means replaying the solution prefix, which is what the
    /// board held before the candidate.
    /// </summary>
    private bool TryAlternativeMate(int fromColumn, int fromRow, int toColumn, int toRow)
    {
        _engine.PromoteToPieceType = ChessPieceType.Queen;
        if (!_engine.MovePiece((byte)fromColumn, (byte)fromRow, (byte)toColumn, (byte)toRow))
            return false;

        if (_engine.GetWhiteMate() || _engine.GetBlackMate())
        {
            RecordLastMoveLabel(SquareName(fromColumn, fromRow) + SquareName(toColumn, toRow));
            _lastFrom = fromColumn + fromRow * 8;
            _lastTo = toColumn + toRow * 8;
            return true;
        }

        RebuildToPly();
        return false;
    }

    private void Finish(bool alternative = false)
    {
        Outcome = PuzzleOutcome.Solved;
        EndedInMate = _engine.GetWhiteMate() || _engine.GetBlackMate();
        Status = alternative
            ? "Solved — a different mate, but a mate all the same."
            : Mistakes == 0 && !HintUsed
                ? "Solved, first try. Nicely done."
                : "Solved.";
        NotifyStateChanged();
    }

    private void Apply(string uci)
    {
        if (!PuzzleData.TryApplyUci(_engine, uci))
            throw new InvalidOperationException($"Puzzle {_puzzle?.Id} contains an illegal move: {uci}");

        RecordLastMoveLabel(uci);
        (_lastFrom, _lastTo) = SquaresOf(uci);
    }

    private void RecordLastMoveLabel(string uci)
    {
        MoveContent last = _engine.LastMove;
        _line.Add(string.IsNullOrWhiteSpace(last.PgnMove) ? uci : last.PgnMove);
    }

    private void RebuildToPly()
    {
        if (_puzzle is null) return;

        _engine = new Engine(_puzzle.Fen);
        _engine.GenerateValidMoves();
        _line.Clear();
        for (int i = 0; i < _ply; i++)
        {
            PuzzleData.TryApplyUci(_engine, _puzzle.Solution[i]);
            RecordLastMoveLabel(_puzzle.Solution[i]);
        }

        (_lastFrom, _lastTo) = _ply == 0
            ? SquaresOf(_puzzle.LastMove)
            : SquaresOf(_puzzle.Solution[_ply - 1]);
    }

    private void SelectSquare(int column, int row)
    {
        ClearSelection();
        _selectedSquare = column + row * 8;

        byte[][]? moves = _engine.GetValidMoves((byte)column, (byte)row);
        if (moves is not null)
        {
            foreach (byte[] move in moves)
            {
                if (_engine.IsValidMove((byte)column, (byte)row, move[0], move[1]))
                    _legalTargets.Add(move[0] + move[1] * 8);
            }
        }

        if (_legalTargets.Count == 0)
            Status = $"{SquareName(column, row)} has no legal moves.";
    }

    private void ClearSelection()
    {
        _selectedSquare = null;
        _legalTargets.Clear();
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();

    private static (int From, int To) SquaresOf(string uci) =>
        uci.Length < 4 ? (-1, -1) : (IndexOf(uci[0], uci[1]), IndexOf(uci[2], uci[3]));

    private static int IndexOf(char file, char rank)
    {
        int column = file - 'a';
        int row = 8 - (rank - '0');
        return column < 0 || column > 7 || row < 0 || row > 7 ? -1 : column + row * 8;
    }

    private static string SquareName(int column, int row) => $"{(char)('a' + column)}{8 - row}";
}
