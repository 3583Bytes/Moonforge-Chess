using ChessEngine.Engine;

namespace ChessBin.Web;

public sealed class ChessGameSession : IDisposable
{
    public const string StartingFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    private Engine _engine = null!;
    private CancellationTokenSource _searchCancellation = new();
    private readonly List<PlayedMove> _moves = [];
    private readonly HashSet<int> _legalTargets = [];
    private string _initialFen = StartingFen;
    private int? _selectedSquare;
    private PendingMove? _pendingPromotion;
    private bool _whiteAtBottom = true;

    public ChessGameSession()
    {
        ResetEngine(StartingFen, ChessPieceColor.White, Engine.Difficulty.Easy);
        Status = "Your move. Select a piece to begin.";
    }

    public ChessPieceColor HumanColor { get; private set; }
    public Engine.Difficulty Difficulty { get; private set; }
    public bool IsThinking { get; private set; }
    public bool IsGameOver => _engine.IsGameOver();
    public bool IsHumanTurn => !IsThinking && !IsGameOver && _engine.WhoseMove == HumanColor;
    public bool CanUndo => !IsThinking && _moves.Count >= 2;
    public string Status { get; private set; }
    public string Fen => _engine.FEN;
    public IReadOnlyList<PlayedMove> Moves => _moves;
    public EngineSearchInfo? LastSearch { get; private set; }
    public bool LastMoveWasBook { get; private set; }
    public EvaluationBreakdown Evaluation { get; private set; }
    public bool HasPendingPromotion => _pendingPromotion is not null;
    public bool WhiteAtBottom => _whiteAtBottom;

    public async Task NewGameAsync(ChessPieceColor humanColor, Engine.Difficulty difficulty)
    {
        CancelSearch();
        _moves.Clear();
        _initialFen = StartingFen;
        ResetEngine(_initialFen, humanColor, difficulty);
        _whiteAtBottom = humanColor == ChessPieceColor.White;
        ClearSelection();
        LastSearch = null;
        LastMoveWasBook = false;
        Status = humanColor == ChessPieceColor.White
            ? "Your move. Select a piece to begin."
            : "Moonforge has White and is preparing the first move…";

        if (humanColor == ChessPieceColor.Black)
            await MakeEngineMoveAsync();
    }

    public void LoadPosition(
        string fen,
        ChessPieceColor humanColor = ChessPieceColor.White,
        Engine.Difficulty difficulty = Engine.Difficulty.Easy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fen);
        CancelSearch();
        _moves.Clear();
        _initialFen = fen;
        ResetEngine(fen, humanColor, difficulty);
        _whiteAtBottom = humanColor == ChessPieceColor.White;
        ClearSelection();
        LastSearch = null;
        LastMoveWasBook = false;
        Status = IsGameOver ? DescribeGameOver() : "Position loaded. Select a piece to begin.";
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
                bool isLastMove = _moves.Count > 0
                    && (index == _moves[^1].FromIndex || index == _moves[^1].ToIndex);

                squares.Add(new BoardSquare(
                    column,
                    row,
                    type,
                    color,
                    index == _selectedSquare,
                    _legalTargets.Contains(index),
                    isLastMove));
            }
        }

        return squares;
    }

    public async Task ClickSquareAsync(int column, int row)
    {
        if (!IsHumanTurn || _pendingPromotion is not null)
            return;

        int clicked = column + row * 8;
        var clickedType = _engine.GetPieceTypeAt((byte)column, (byte)row);
        ChessPieceColor? clickedColor = clickedType == ChessPieceType.None
            ? null
            : _engine.GetPieceColorAt((byte)column, (byte)row);

        if (_selectedSquare is int selected && _legalTargets.Contains(clicked))
        {
            int sourceColumn = selected % 8;
            int sourceRow = selected / 8;
            if (_engine.GetPieceTypeAt((byte)sourceColumn, (byte)sourceRow) == ChessPieceType.Pawn
                && (row == 0 || row == 7))
            {
                _pendingPromotion = new PendingMove(sourceColumn, sourceRow, column, row);
                Status = "Choose a piece for promotion.";
                return;
            }

            await MakeHumanMoveAsync(sourceColumn, sourceRow, column, row, ChessPieceType.Queen);
            return;
        }

        if (clickedColor == HumanColor)
        {
            SelectSquare(column, row);
            return;
        }

        ClearSelection();
        Status = "Select one of your pieces to begin.";
    }

    public async Task CompletePromotionAsync(ChessPieceType pieceType)
    {
        if (_pendingPromotion is not PendingMove move)
            return;
        if (pieceType is not (ChessPieceType.Queen or ChessPieceType.Rook or ChessPieceType.Bishop or ChessPieceType.Knight))
            throw new ArgumentOutOfRangeException(nameof(pieceType), "A pawn can only promote to a queen, rook, bishop, or knight.");

        _pendingPromotion = null;
        await MakeHumanMoveAsync(move.FromColumn, move.FromRow, move.ToColumn, move.ToRow, pieceType);
    }

    public void CancelPromotion()
    {
        _pendingPromotion = null;
        ClearSelection();
        Status = "Promotion cancelled. Select a piece to continue.";
    }

    public void UndoTurn()
    {
        if (!CanUndo)
            return;

        CancelSearch();
        _moves.RemoveRange(_moves.Count - 2, 2);
        RebuildPosition();
        ClearSelection();
        LastSearch = null;
        LastMoveWasBook = false;
        Status = "Last turn undone. Your move.";
    }

    public void FlipBoard()
    {
        _whiteAtBottom = !_whiteAtBottom;
        ClearSelection();
    }

    private async Task MakeHumanMoveAsync(
        int sourceColumn,
        int sourceRow,
        int destinationColumn,
        int destinationRow,
        ChessPieceType promotion)
    {
        ClearSelection();
        _engine.PromoteToPieceType = promotion;
        string coordinate = SquareName(sourceColumn, sourceRow) + SquareName(destinationColumn, destinationRow);
        string uci = coordinate + PromotionSuffix(promotion, destinationRow);

        if (!_engine.MovePiece((byte)sourceColumn, (byte)sourceRow, (byte)destinationColumn, (byte)destinationRow))
        {
            Status = $"{coordinate} is not legal in this position.";
            return;
        }

        RecordAppliedMove(uci);
        if (_engine.IsGameOver())
        {
            Status = DescribeGameOver();
            return;
        }

        Status = "Moonforge is thinking…";
        await MakeEngineMoveAsync();
    }

    private async Task MakeEngineMoveAsync()
    {
        if (_engine.IsGameOver())
        {
            Status = DescribeGameOver();
            return;
        }

        IsThinking = true;
        Status = "Moonforge is thinking…";
        var token = _searchCancellation.Token;

        try
        {
            // Yield once so Blazor can paint the thinking state before the CPU-bound
            // WebAssembly search begins. The search API remains cancellable for a
            // future worker-backed implementation.
            await Task.Yield();
            EngineSearchResult result = await Task.Run(() => _engine.SearchBestMove(token), token);
            if (!result.HasMove || token.IsCancellationRequested)
                return;

            LastSearch = result.Info;
            LastMoveWasBook = result.FromBook;
            ApplyCoordinateMove(result.BestMove);
            RecordAppliedMove(result.BestMove);
            Status = _engine.IsGameOver()
                ? DescribeGameOver()
                : $"Moonforge played {_moves[^1].Label}. Your move.";
        }
        catch (OperationCanceledException)
        {
            Status = "Search cancelled.";
        }
        finally
        {
            IsThinking = false;
        }
    }

    private void ApplyCoordinateMove(string uci)
    {
        if (uci.Length is not (4 or 5))
            throw new InvalidOperationException($"Moonforge returned an invalid coordinate move: {uci}");

        _engine.PromoteToPieceType = uci.Length == 5 ? PromotionPiece(uci[4]) : ChessPieceType.Queen;
        if (!_engine.MovePieceAN(uci[..4]))
            throw new InvalidOperationException($"Moonforge returned an illegal move: {uci}");
    }

    private void RecordAppliedMove(string uci)
    {
        MoveContent lastMove = _engine.LastMove;
        string label = string.IsNullOrWhiteSpace(lastMove.PgnMove) ? uci : lastMove.PgnMove;
        _moves.Add(new PlayedMove(
            uci,
            label,
            lastMove.MovingPiecePrimary.SrcPosition,
            lastMove.MovingPiecePrimary.DstPosition));
        Evaluation = _engine.GetEvaluationBreakdown();
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

        Status = _legalTargets.Count == 0
            ? $"{SquareName(column, row)} has no legal moves."
            : $"Selected {SquareName(column, row)}.";
    }

    private void RebuildPosition()
    {
        var retainedMoves = _moves.ToArray();
        ResetEngine(_initialFen, HumanColor, Difficulty);
        _moves.Clear();
        foreach (PlayedMove move in retainedMoves)
        {
            ApplyCoordinateMove(move.Uci);
            RecordAppliedMove(move.Uci);
        }
    }

    private void ResetEngine(string fen, ChessPieceColor humanColor, Engine.Difficulty difficulty)
    {
        _engine = new Engine(fen)
        {
            HumanPlayer = humanColor,
            GameDifficulty = difficulty,
            // The depth setting remains the primary difficulty control. This cap is
            // a safety net for slower phones and browsers once play leaves the book.
            SearchDeadlineMs = difficulty switch
            {
                Engine.Difficulty.Easy => 350,
                Engine.Difficulty.Medium => 900,
                Engine.Difficulty.Hard => 1_800,
                _ => 3_000
            }
        };
        _engine.GenerateValidMoves();
        HumanColor = humanColor;
        Difficulty = difficulty;
        Evaluation = _engine.GetEvaluationBreakdown();
    }

    private string DescribeGameOver()
    {
        if (_engine.GetWhiteMate()) return HumanColor == ChessPieceColor.White ? "Checkmate. Moonforge wins." : "Checkmate. You win!";
        if (_engine.GetBlackMate()) return HumanColor == ChessPieceColor.Black ? "Checkmate. Moonforge wins." : "Checkmate. You win!";
        if (_engine.StaleMate) return "Draw by stalemate.";
        if (_engine.FiftyMove) return "Draw by the fifty-move rule.";
        if (_engine.RepeatedMove) return "Draw by threefold repetition.";
        if (_engine.InsufficientMaterial) return "Draw by insufficient material.";
        return "Game over.";
    }

    private void ClearSelection()
    {
        _selectedSquare = null;
        _legalTargets.Clear();
    }

    private void CancelSearch()
    {
        _searchCancellation.Cancel();
        _searchCancellation.Dispose();
        _searchCancellation = new CancellationTokenSource();
        IsThinking = false;
    }

    private static string SquareName(int column, int row) => $"{(char)('a' + column)}{8 - row}";

    private static string PromotionSuffix(ChessPieceType promotion, int destinationRow)
    {
        if (destinationRow is not (0 or 7)) return string.Empty;
        return promotion switch
        {
            ChessPieceType.Queen => "q",
            ChessPieceType.Rook => "r",
            ChessPieceType.Bishop => "b",
            ChessPieceType.Knight => "n",
            _ => string.Empty
        };
    }

    private static ChessPieceType PromotionPiece(char suffix) => char.ToLowerInvariant(suffix) switch
    {
        'q' => ChessPieceType.Queen,
        'r' => ChessPieceType.Rook,
        'b' => ChessPieceType.Bishop,
        'n' => ChessPieceType.Knight,
        _ => throw new InvalidOperationException($"Unknown promotion suffix: {suffix}")
    };

    public void Dispose()
    {
        _searchCancellation.Cancel();
        _searchCancellation.Dispose();
    }

    private sealed record PendingMove(int FromColumn, int FromRow, int ToColumn, int ToRow);
}

public sealed record PlayedMove(string Uci, string Label, int FromIndex, int ToIndex);

public sealed record BoardSquare(
    int Column,
    int Row,
    ChessPieceType PieceType,
    ChessPieceColor? PieceColor,
    bool IsSelected,
    bool IsLegalTarget,
    bool IsLastMove)
{
    public string Coordinate => $"{(char)('a' + Column)}{8 - Row}";
    public bool IsDark => (Column + Row) % 2 != 0;
    public bool IsOccupied => PieceType != ChessPieceType.None;
}
