using ChessEngine.Engine;
using System.Diagnostics;

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
    private int _currentPly;
    private TimeControl _timeControl = TimeControl.Unlimited;
    private long _whiteMilliseconds;
    private long _blackMilliseconds;
    private long _turnStartedAt;
    private bool _timeExpired;

    public event Action? StateChanged;

    public ChessGameSession()
    {
        ResetEngine(StartingFen, ChessPieceColor.White, Engine.Difficulty.Easy);
        Status = "Your move. Select a piece to begin.";
    }

    public ChessPieceColor HumanColor { get; private set; }
    public Engine.Difficulty Difficulty { get; private set; }
    public bool IsThinking { get; private set; }
    public bool IsGameOver => _timeExpired || _engine.IsGameOver();
    public bool IsViewingHistory => _currentPly != _moves.Count;
    public bool IsHumanTurn => !IsThinking && !IsGameOver && !IsViewingHistory && _engine.WhoseMove == HumanColor;
    public bool CanUndo => !IsThinking && !IsViewingHistory && _currentPly >= 2;
    public bool CanStepBack => !IsThinking && _currentPly > 0;
    public bool CanStepForward => !IsThinking && _currentPly < _moves.Count;
    public string Status { get; private set; }
    public string Fen => _engine.FEN;
    public IReadOnlyList<PlayedMove> Moves => _moves;
    public EngineSearchInfo? LastSearch { get; private set; }
    public bool LastMoveWasBook { get; private set; }
    public EvaluationBreakdown Evaluation { get; private set; }
    public bool HasPendingPromotion => _pendingPromotion is not null;
    public bool WhiteAtBottom => _whiteAtBottom;
    public int CurrentPly => _currentPly;
    public TimeControl CurrentTimeControl => _timeControl;
    public long WhiteMilliseconds => RemainingMilliseconds(ChessPieceColor.White);
    public long BlackMilliseconds => RemainingMilliseconds(ChessPieceColor.Black);
    public TimeSpan LastSearchElapsed { get; private set; }
    public long SearchNodesPerSecond => LastSearchElapsed.TotalSeconds <= 0 || LastSearch is null
        ? 0 : (long)(LastSearch.TotalNodes / LastSearchElapsed.TotalSeconds);
    public string Pgn => BuildPgn();

    public async Task NewGameAsync(
        ChessPieceColor humanColor,
        Engine.Difficulty difficulty,
        TimeControl? timeControl = null)
    {
        timeControl ??= TimeControl.Unlimited;
        CancelSearch();
        _moves.Clear();
        _currentPly = 0;
        _initialFen = StartingFen;
        _timeControl = timeControl;
        _whiteMilliseconds = timeControl.InitialMilliseconds;
        _blackMilliseconds = timeControl.InitialMilliseconds;
        _timeExpired = false;
        ResetEngine(_initialFen, humanColor, difficulty);
        _whiteAtBottom = humanColor == ChessPieceColor.White;
        ClearSelection();
        LastSearch = null;
        LastMoveWasBook = false;
        LastSearchElapsed = TimeSpan.Zero;
        StartTurnClock();
        Status = humanColor == ChessPieceColor.White
            ? "Your move. Select a piece to begin."
            : "Moonforge has White and is preparing the first move…";

        if (humanColor == ChessPieceColor.Black)
            await MakeEngineMoveAsync();
        NotifyStateChanged();
    }

    public void LoadPosition(
        string fen,
        ChessPieceColor humanColor = ChessPieceColor.White,
        Engine.Difficulty difficulty = Engine.Difficulty.Easy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fen);
        CancelSearch();
        _moves.Clear();
        _currentPly = 0;
        _initialFen = fen;
        ResetEngine(fen, humanColor, difficulty);
        _whiteAtBottom = humanColor == ChessPieceColor.White;
        ClearSelection();
        LastSearch = null;
        LastMoveWasBook = false;
        LastSearchElapsed = TimeSpan.Zero;
        _timeExpired = false;
        _timeControl = TimeControl.Unlimited;
        _whiteMilliseconds = 0;
        _blackMilliseconds = 0;
        StartTurnClock();
        Status = IsGameOver ? DescribeGameOver() : "Position loaded. Select a piece to begin.";
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
                bool isLastMove = _currentPly > 0
                    && (index == _moves[_currentPly - 1].FromIndex || index == _moves[_currentPly - 1].ToIndex);

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
        _moves.RemoveRange(_currentPly - 2, 2);
        _currentPly -= 2;
        RebuildPosition();
        ClearSelection();
        LastSearch = null;
        LastMoveWasBook = false;
        Status = "Last turn undone. Your move.";
        NotifyStateChanged();
    }

    public void StepBack() => NavigateToPly(_currentPly - 1);

    public void StepForward() => NavigateToPly(_currentPly + 1);

    public void NavigateToPly(int ply)
    {
        if (IsThinking) return;
        int requestedPly = Math.Clamp(ply, 0, _moves.Count);
        if (requestedPly == _currentPly) return;

        CancelSearch();
        _currentPly = requestedPly;
        RebuildPosition();
        ClearSelection();
        Status = IsViewingHistory
            ? $"Reviewing move {_currentPly} of {_moves.Count}."
            : IsGameOver ? DescribeGameOver() : _engine.WhoseMove == HumanColor ? "Your move." : "Moonforge to move.";
        NotifyStateChanged();
    }

    public void FlipBoard()
    {
        _whiteAtBottom = !_whiteAtBottom;
        ClearSelection();
        NotifyStateChanged();
    }

    /// <summary>Updates the displayed chess clock. The component calls this on a short UI timer.</summary>
    public void TickClock()
    {
        if (_timeControl.IsUnlimited || IsGameOver || IsViewingHistory || IsThinking) return;
        ChessPieceColor side = _engine.WhoseMove;
        if (RemainingMilliseconds(side) > 0) return;
        _timeExpired = true;
        Status = side == HumanColor ? "Time. Moonforge wins." : "Moonforge ran out of time. You win!";
        NotifyStateChanged();
    }

    private async Task MakeHumanMoveAsync(
        int sourceColumn,
        int sourceRow,
        int destinationColumn,
        int destinationRow,
        ChessPieceType promotion)
    {
        ClearSelection();
        SettleTurnClock();
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
            NotifyStateChanged();
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
            Stopwatch stopwatch = Stopwatch.StartNew();
            EngineSearchResult result = await Task.Run(
                () => _engine.SearchBestMove(token, info =>
                {
                    LastSearch = info;
                    LastMoveWasBook = false;
                    LastSearchElapsed = stopwatch.Elapsed;
                    NotifyStateChanged();
                }), token);
            stopwatch.Stop();
            if (!result.HasMove || token.IsCancellationRequested)
                return;

            LastSearch = result.Info;
            LastMoveWasBook = result.FromBook;
            LastSearchElapsed = stopwatch.Elapsed;
            SettleTurnClock();
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
            StartTurnClock();
            NotifyStateChanged();
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
        if (_currentPly < _moves.Count)
            _moves.RemoveRange(_currentPly, _moves.Count - _currentPly);
        MoveContent lastMove = _engine.LastMove;
        string label = string.IsNullOrWhiteSpace(lastMove.PgnMove) ? uci : lastMove.PgnMove;
        _moves.Add(new PlayedMove(
            uci,
            label,
            lastMove.MovingPiecePrimary.SrcPosition,
            lastMove.MovingPiecePrimary.DstPosition));
        _currentPly = _moves.Count;
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
        var retainedMoves = _moves.Take(_currentPly).ToArray();
        ResetEngine(_initialFen, HumanColor, Difficulty);
        foreach (PlayedMove move in retainedMoves)
        {
            ApplyCoordinateMove(move.Uci);
        }
        Evaluation = _engine.GetEvaluationBreakdown();
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
        if (_timeExpired)
            return _engine.WhoseMove == HumanColor ? "Time. Moonforge wins." : "Moonforge ran out of time. You win!";
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

    private void StartTurnClock() => _turnStartedAt = Stopwatch.GetTimestamp();

    private void SettleTurnClock()
    {
        if (_timeControl.IsUnlimited || _turnStartedAt == 0) return;
        long elapsed = (long)((Stopwatch.GetTimestamp() - _turnStartedAt) * 1000d / Stopwatch.Frequency);
        if (_engine.WhoseMove == ChessPieceColor.White)
            _whiteMilliseconds = Math.Max(0, _whiteMilliseconds - elapsed) + _timeControl.IncrementMilliseconds;
        else
            _blackMilliseconds = Math.Max(0, _blackMilliseconds - elapsed) + _timeControl.IncrementMilliseconds;
        _turnStartedAt = Stopwatch.GetTimestamp();
    }

    private long RemainingMilliseconds(ChessPieceColor color)
    {
        long stored = color == ChessPieceColor.White ? _whiteMilliseconds : _blackMilliseconds;
        if (_timeControl.IsUnlimited || IsThinking || IsViewingHistory || _turnStartedAt == 0 || _engine.WhoseMove != color)
            return stored;
        long elapsed = (long)((Stopwatch.GetTimestamp() - _turnStartedAt) * 1000d / Stopwatch.Frequency);
        return Math.Max(0, stored - elapsed);
    }

    private string BuildPgn()
    {
        string result = GameResult();
        var lines = new List<string>
        {
            "[Event \"ChessBin game\"]",
            "[Site \"https://chessbin.com\"]",
            $"[Date \"{DateTime.UtcNow:yyyy.MM.dd}\"]",
            $"[White \"{(HumanColor == ChessPieceColor.White ? "You" : "Moonforge")}\"]",
            $"[Black \"{(HumanColor == ChessPieceColor.Black ? "You" : "Moonforge")}\"]",
            $"[Result \"{result}\"]"
        };
        if (_initialFen != StartingFen)
        {
            lines.Add("[SetUp \"1\"]");
            lines.Add($"[FEN \"{_initialFen}\"]");
        }

        var notation = new List<string>();
        for (int index = 0; index < _currentPly; index++)
        {
            if (index % 2 == 0) notation.Add($"{index / 2 + 1}. {_moves[index].Label}");
            else notation.Add(_moves[index].Label);
        }
        notation.Add(result);
        return string.Join(Environment.NewLine, lines) + Environment.NewLine + Environment.NewLine + string.Join(' ', notation);
    }

    private string GameResult()
    {
        if (!IsGameOver) return "*";
        if (_engine.GetWhiteMate()) return "0-1";
        if (_engine.GetBlackMate()) return "1-0";
        if (_timeExpired) return _engine.WhoseMove == ChessPieceColor.White ? "0-1" : "1-0";
        return "1/2-1/2";
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();

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

public sealed record TimeControl(string Label, long InitialMilliseconds, long IncrementMilliseconds)
{
    public static readonly TimeControl Unlimited = new("Unlimited", 0, 0);
    public static readonly TimeControl Bullet = new("1 + 0", 60_000, 0);
    public static readonly TimeControl Blitz = new("3 + 2", 180_000, 2_000);
    public static readonly TimeControl Rapid = new("10 + 0", 600_000, 0);
    public bool IsUnlimited => InitialMilliseconds <= 0;
}

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
