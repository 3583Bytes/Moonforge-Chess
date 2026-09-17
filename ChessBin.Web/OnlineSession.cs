using ChessBin.Online;
using ChessEngine.Engine;

namespace ChessBin.Web;

public enum OnlinePhase
{
    /// <summary>Nothing asked for yet.</summary>
    Idle,
    /// <summary>In the queue, waiting to be paired.</summary>
    Seeking,
    /// <summary>Paired and seated, waiting for the opponent to take their seat.</summary>
    Seated,
    Playing,
    Finished,
}

/// <summary>
/// One online game, from this browser's point of view.
/// <para>
/// This is where the architecture actually lives. The server owns seats, turn order and the
/// clocks, and knows nothing about chess. <b>This</b> owns the rules, by running a real
/// <see cref="Match"/> — the same class, with the same tests, that a fully authoritative server
/// would have run. Every move the server reports, including the opponent's, is replayed through
/// it before the board moves.
/// </para>
/// <para>
/// So an opponent cannot play an illegal move: their move is only ever <em>relayed</em>, and
/// this refuses it and disputes it, which ends the game. They cannot win a game they did not
/// win either, because a claimed checkmate is replayed here too. What they can do is force an
/// abort — and with no accounts and no ratings on ChessBin, that is the whole of the prize.
/// </para>
/// </summary>
public sealed class OnlineSession(IPlayApi api, string playerToken)
{
    /// <summary>
    /// The mirror needs two seat tokens to accept moves from both sides. They are local names
    /// for "whoever is White" and "whoever is Black" and never leave this object — the real
    /// seat token is <see cref="_token"/>, and the server is the only other thing that sees it.
    /// </summary>
    private const string MirrorWhite = "mirror-white";
    private const string MirrorBlack = "mirror-black";

    private readonly IPlayApi _api = api ?? throw new ArgumentNullException(nameof(api));
    private readonly string _token = playerToken ?? throw new ArgumentNullException(nameof(playerToken));

    private Match _mirror = NewMirror();
    private Engine _board = NewBoard();
    private MatchClock _clock = MatchClock.Blitz;

    /// <summary>How many of the server's plies have been replayed into the mirror.</summary>
    private int _applied;

    private int? _selected;
    private readonly HashSet<int> _targets = [];
    private PendingPromotion? _promotion;
    private DateTimeOffset _syncedAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// One request at a time. Pressing "find a game" starts a poll of its own while the page's
    /// timer is also polling, so without this two are in flight at once — and the older reply
    /// can land second and roll the game backwards.
    /// </summary>
    private bool _busy;

    /// <summary>
    /// Latches once both players have arrived. A reply that was already in flight when they did
    /// still says they had not, and believing it puts a live game back to "waiting for your
    /// opponent" — which is exactly where it then sits, because the board is disabled.
    /// </summary>
    private bool _opponentSeen;

    /// <summary>When the current search began, so the page can stop pretending after a while.</summary>
    private DateTimeOffset _seekingSince = DateTimeOffset.UtcNow;

    public event Action? StateChanged;

    public OnlinePhase Phase { get; private set; } = OnlinePhase.Idle;
    public string? MatchId { get; private set; }
    public Seat? Seat { get; private set; }
    public MatchView? View { get; private set; }
    public string Status { get; private set; } = "Pick a time control and look for an opponent.";
    public int Waiting { get; private set; }

    /// <summary>Set when this browser's engine rejected a move the server reported.</summary>
    public bool Disputed { get; private set; }

    /// <summary>
    /// The game this browser created to send to someone. Null unless it made one — a game it
    /// was paired into has no link worth sharing, because both seats are already spoken for.
    /// </summary>
    public string? ChallengeId { get; private set; }

    /// <summary>
    /// True once a search has gone on long enough to be worth admitting nobody is about. The
    /// page offers Moonforge at that point; it never substitutes one silently, because passing
    /// an engine off as a person is the one thing that would cost this real trust.
    /// </summary>
    public bool NobodyAbout { get; private set; }

    public bool HasPendingPromotion => _promotion is not null;
    public bool IsMyTurn => Phase == OnlinePhase.Playing && View is not null && Seat == View.ToMove;
    public bool WhiteAtBottom => Seat != Online.Seat.Black;

    /// <summary>The position as both players' engines agree it is.</summary>
    public string Fen => _mirror.Fen;

    public IReadOnlyList<string> Moves => _mirror.Moves;

    /// <summary>
    /// Time left, interpolated between polls so the clock does not jump a second at a time.
    /// The server's figure is the truth; this only fills the gap since it last spoke.
    /// </summary>
    public long MsRemaining(Seat seat)
    {
        if (View is null) return _clock.InitialMs;

        long held = seat == Online.Seat.White ? View.WhiteMs : View.BlackMs;
        if (_clock.IsUntimed || View.Status != MatchStatus.Playing || seat != View.ToMove) return held;

        return Math.Max(0, held - (long)(DateTimeOffset.UtcNow - _syncedAt).TotalMilliseconds);
    }

    // ── finding a game ──────────────────────────────────────────────────────────

    public async Task SeekAsync(MatchClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;
        Reset();
        Phase = OnlinePhase.Seeking;
        _seekingSince = DateTimeOffset.UtcNow;
        Status = "Looking for an opponent…";
        Changed();

        await PollAsync();
    }

    /// <summary>
    /// Makes a game with one seat left open and holds on to its id, so the page can hand the
    /// player a link. Nobody is queued and nothing is matched: the invitation travels by
    /// whatever the player already uses to talk to the person they want to play.
    /// </summary>
    public async Task CreateChallengeAsync(MatchClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;
        Reset();
        Status = "Setting up a game…";
        Changed();

        ChallengeOutcome outcome = await _api.OpenChallengeAsync(_token, clock);
        if (!outcome.Created)
        {
            Status = "Could not set up a game just now. Try again in a moment.";
            Changed();
            return;
        }

        MatchId = outcome.MatchId;
        ChallengeId = outcome.MatchId;
        Seat = outcome.Seat;
        await JoinAsync();
    }

    /// <summary>Opens a game someone sent a link to, taking the seat they left.</summary>
    public async Task JoinByLinkAsync(string matchId)
    {
        if (string.IsNullOrWhiteSpace(matchId)) return;

        Reset();
        MatchId = matchId;
        Status = "Joining the game…";
        Changed();

        await JoinAsync();
    }

    /// <summary>True while a request is in flight; the page uses it to skip a timer tick.</summary>
    public bool Busy => _busy;

    public async Task CancelSeekAsync()
    {
        if (Phase != OnlinePhase.Seeking) return;

        await _api.CancelSeekAsync(_token);
        Reset();
        Status = "Search cancelled.";
        Changed();
    }

    /// <summary>
    /// Called on a timer. What it does depends on where we are: while seeking it asks the
    /// lobby again — which is also how a queued player finds out they have been paired — and
    /// once in a game it fetches the state and replays anything new.
    /// </summary>
    public async Task PollAsync()
    {
        if (_busy) return;

        _busy = true;
        try
        {
            switch (Phase)
            {
                case OnlinePhase.Seeking:
                    await PollLobbyAsync();
                    break;
                case OnlinePhase.Seated:
                case OnlinePhase.Playing:
                    await PollMatchAsync();
                    break;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task PollLobbyAsync()
    {
        SeekOutcome outcome = await _api.SeekAsync(_token, _clock);

        if (outcome.Paired && outcome.MatchId is not null)
        {
            MatchId = outcome.MatchId;
            Seat = outcome.Seat;
            await JoinAsync();
            return;
        }

        if (outcome.Queued)
        {
            Waiting = outcome.Waiting;
            NobodyAbout = DateTimeOffset.UtcNow - _seekingSince > QuietAfter;

            Status = outcome.Waiting > 1
                ? $"Waiting for an opponent — {outcome.Waiting} in the queue."
                : NobodyAbout ? "Still looking — it is quiet right now."
                : "Waiting for an opponent…";
        }
        else
        {
            Status = "Could not reach the game server. Still trying…";
        }

        Changed();
    }

    private async Task JoinAsync()
    {
        JoinOutcome outcome = await _api.JoinAsync(MatchId!, _token);

        if (outcome.Refused)
        {
            // A dead end, not a blip: the seat has gone or the game is over. Retrying would
            // just spin, so say so and put the player back where they can do something.
            Reset();
            Status = "That game is not open — someone else took the seat, or it has finished.";
            Changed();
            return;
        }

        if (outcome.View is not MatchView view)
        {
            Status = "Found a game, but could not join it. Retrying…";
            Changed();
            return;
        }

        // The game is the authority on the seat, not the lobby's reply. They agree — the lobby
        // seats the game when it pairs — but believing two sources is how they drift apart.
        Seat = view.Seat ?? Seat;
        await AbsorbAsync(view);
    }

    private async Task PollMatchAsync()
    {
        MatchView? view = await _api.StateAsync(MatchId!, _token);
        if (view is null)
        {
            // A dropped poll is not news. The board stays as it was and the next one recovers.
            return;
        }

        await AbsorbAsync(view);
    }

    // ── applying what the server says ───────────────────────────────────────────

    /// <summary>
    /// Takes the server's view and reconciles it with the local mirror. Every ply the server
    /// reports that this browser has not seen is replayed through <see cref="Match"/>; the
    /// first one the engine refuses ends the game rather than being drawn on the board.
    /// </summary>
    private async Task AbsorbAsync(MatchView view)
    {
        // A reply that knows about fewer moves than have already been played is one that was
        // issued before them and simply arrived late. Applying it would rewind the board.
        if (view.Uci.Count < _applied) return;

        _opponentSeen |= view.OpponentJoined;
        View = view;
        _syncedAt = DateTimeOffset.UtcNow;

        for (int ply = _applied; ply < view.Uci.Count; ply++)
        {
            Seat mover = ply % 2 == 0 ? Online.Seat.White : Online.Seat.Black;
            string mirrorToken = mover == Online.Seat.White ? MirrorWhite : MirrorBlack;

            // The mirror is untimed, so the clock never enters into this — time is the
            // server's job and a local clock could only disagree with it.
            MoveResult result = _mirror.Submit(mirrorToken, view.Uci[ply], 0);
            if (!result.Accepted)
            {
                await RejectAsync(ply, mover);
                return;
            }

            _applied = ply + 1;
        }

        // Replaying the moves is only half the check. A player could send a perfectly legal
        // move and claim it was mate — the server cannot tell, so this has to. The claim is
        // only believed if this engine also thinks the game is over, and agrees who won.
        if (view.Status == MatchStatus.Finished && IsBoardReason(view.Reason)
            && (_mirror.Status != MatchStatus.Finished || _mirror.Outcome != view.Outcome))
        {
            await RejectClaimAsync(view);
            return;
        }

        RebuildBoard();
        UpdatePhase(view);
        Changed();
    }

    /// <summary>
    /// The ways a game can end that are facts about the position, and so are checkable here.
    /// Resignation, agreement, timeout and abandonment are not: they are the server's to decide
    /// and there is nothing on the board that would confirm or deny them.
    /// </summary>
    private static bool IsBoardReason(MatchReason reason) => reason
        is MatchReason.Checkmate or MatchReason.Stalemate or MatchReason.Repetition
        or MatchReason.FiftyMove or MatchReason.InsufficientMaterial;

    private async Task RejectClaimAsync(MatchView view)
    {
        Disputed = true;
        Phase = OnlinePhase.Finished;
        Status = "Your opponent claimed a result this position does not support. The game has been stopped.";

        if (MatchId is not null && view.Uci.Count > 0)
        {
            await _api.DisputeAsync(MatchId, _token, view.Uci.Count);
        }

        Changed();
    }

    /// <summary>
    /// The engine refused a move the server accepted. If it was the opponent's, tell the server
    /// so the game ends for both of us rather than only here — otherwise they would sit looking
    /// at a board this browser has abandoned.
    /// </summary>
    private async Task RejectAsync(int ply, Seat mover)
    {
        Disputed = true;
        Phase = OnlinePhase.Finished;
        Status = mover == Seat
            ? "Your own move was refused — the game is out of step and has been stopped."
            : "Your opponent's move was not legal. The game has been stopped.";

        if (mover != Seat && MatchId is not null)
        {
            await _api.DisputeAsync(MatchId, _token, ply + 1);
        }

        Changed();
    }

    private void UpdatePhase(MatchView view)
    {
        if (view.Status == MatchStatus.Finished)
        {
            Phase = OnlinePhase.Finished;
            Status = Describe(view);
            return;
        }

        if (!_opponentSeen)
        {
            Phase = OnlinePhase.Seated;
            Status = ChallengeId is null
                ? "Waiting for your opponent to arrive…"
                : "Send the link to whoever you want to play — the game starts when they open it.";
            return;
        }

        Phase = OnlinePhase.Playing;
        Status = view.DrawOfferedToYou
            ? "Your opponent has offered a draw."
            : IsMyTurn ? "Your move." : "Waiting for your opponent…";
    }

    /// <summary>The result in words, from this player's side of the board.</summary>
    private string Describe(MatchView view)
    {
        if (view.Outcome == MatchOutcome.Aborted)
        {
            return view.Reason == MatchReason.Disputed
                ? "The game was stopped: the two sides disagreed about a move."
                : "The game was abandoned.";
        }

        if (view.Outcome == MatchOutcome.Draw)
        {
            string how = view.Reason switch
            {
                MatchReason.Agreement => "by agreement",
                MatchReason.Stalemate => "by stalemate",
                MatchReason.Repetition => "by repetition",
                MatchReason.FiftyMove => "by the fifty-move rule",
                MatchReason.InsufficientMaterial => "— neither side can mate",
                _ => "",
            };
            return $"Drawn {how}".TrimEnd();
        }

        bool won = (view.Outcome == MatchOutcome.WhiteWins && Seat == Online.Seat.White)
                || (view.Outcome == MatchOutcome.BlackWins && Seat == Online.Seat.Black);

        string cause = view.Reason switch
        {
            MatchReason.Checkmate => "by checkmate",
            MatchReason.Timeout => "on time",
            MatchReason.Resignation => won ? "— your opponent resigned" : "— you resigned",
            _ => "",
        };

        return $"{(won ? "You won" : "You lost")} {cause}".TrimEnd();
    }

    // ── making a move ───────────────────────────────────────────────────────────

    public IReadOnlyList<BoardSquare> GetDisplaySquares()
    {
        (int from, int to) = LastMoveSquares();
        return BoardView.Squares(_board, WhiteAtBottom,
            isSelected: i => i == _selected,
            isLegalTarget: _targets.Contains,
            isLastMove: i => i == from || i == to);
    }

    public async Task ClickSquareAsync(int column, int row)
    {
        if (!IsMyTurn || _promotion is not null) return;

        int clicked = column + row * 8;

        if (_selected is int selected && _targets.Contains(clicked))
        {
            int fromColumn = selected % 8;
            int fromRow = selected / 8;

            if (_board.GetPieceTypeAt((byte)fromColumn, (byte)fromRow) == ChessPieceType.Pawn && row is 0 or 7)
            {
                _promotion = new PendingPromotion(fromColumn, fromRow, column, row);
                Status = "Choose a piece for promotion.";
                Changed();
                return;
            }

            await SubmitAsync(Uci(fromColumn, fromRow, column, row, null));
            return;
        }

        ChessPieceType type = _board.GetPieceTypeAt((byte)column, (byte)row);
        ChessPieceColor? colour = type == ChessPieceType.None ? null : _board.GetPieceColorAt((byte)column, (byte)row);

        if (colour is ChessPieceColor mine && (mine == ChessPieceColor.White) == (Seat == Online.Seat.White))
        {
            Select(column, row);
        }
        else
        {
            ClearSelection();
        }

        Changed();
    }

    public async Task CompletePromotionAsync(ChessPieceType piece)
    {
        if (_promotion is not PendingPromotion pending) return;
        if (piece is not (ChessPieceType.Queen or ChessPieceType.Rook or ChessPieceType.Bishop or ChessPieceType.Knight))
            throw new ArgumentOutOfRangeException(nameof(piece), "A pawn can only promote to a queen, rook, bishop, or knight.");

        _promotion = null;
        await SubmitAsync(Uci(pending.FromColumn, pending.FromRow, pending.ToColumn, pending.ToRow, piece));
    }

    public void CancelPromotion()
    {
        _promotion = null;
        ClearSelection();
        Changed();
    }

    /// <summary>
    /// Plays a move: through the local engine first, so an illegal one never reaches the wire,
    /// and so the result claim sent with it is the engine's verdict rather than a guess.
    /// </summary>
    private async Task SubmitAsync(string uci)
    {
        if (MatchId is null || Seat is not Seat seat) return;

        string mirrorToken = seat == Online.Seat.White ? MirrorWhite : MirrorBlack;
        MoveResult result = _mirror.Submit(mirrorToken, uci, 0);
        if (!result.Accepted)
        {
            Status = "That move is not legal.";
            ClearSelection();
            Changed();
            return;
        }

        _applied++;
        RebuildBoard();
        ClearSelection();
        Status = "Waiting for your opponent…";
        Changed();

        // If that move ended the game, the claim travels with it. The opponent's engine checks
        // it and disputes it if it disagrees, so a bogus claim costs the game rather than wins it.
        bool ended = _mirror.Status == MatchStatus.Finished;
        MatchView? view = await _api.MoveAsync(
            MatchId, _token, result.San ?? uci, uci,
            ended ? _mirror.Outcome : null,
            ended ? _mirror.Reason : null);

        if (view is not null) await AbsorbAsync(view);
    }

    // ── the other things a player can do ────────────────────────────────────────

    public Task ResignAsync() => ActAsync("resign");

    public Task OfferDrawAsync() => ActAsync("draw");

    public Task DeclineDrawAsync() => ActAsync("decline");

    public Task AbortAsync() => ActAsync("abort");

    private async Task ActAsync(string action)
    {
        if (MatchId is null || Phase is not (OnlinePhase.Playing or OnlinePhase.Seated)) return;

        MatchView? view = await _api.ActAsync(MatchId, _token, action);
        if (view is not null) await AbsorbAsync(view);
    }

    // ── internals ───────────────────────────────────────────────────────────────

    private void Select(int column, int row)
    {
        _selected = column + row * 8;
        _targets.Clear();

        byte[][]? moves = _board.GetValidMoves((byte)column, (byte)row);
        if (moves is null) return;

        foreach (byte[] move in moves)
        {
            if (move.Length >= 2) _targets.Add(move[0] + move[1] * 8);
        }
    }

    private void ClearSelection()
    {
        _selected = null;
        _targets.Clear();
    }

    private void RebuildBoard()
    {
        // Rebuilt from the mirror's FEN rather than tracked in parallel, so there is exactly one
        // source of truth for the position and the board cannot drift away from the rules.
        _board = new Engine(_mirror.Fen);
        _board.GenerateValidMoves();
        ClearSelection();
    }

    private (int From, int To) LastMoveSquares()
    {
        if (View is null || _applied == 0 || _applied > View.Uci.Count) return (-1, -1);

        string uci = View.Uci[_applied - 1];
        return uci.Length < 4
            ? (-1, -1)
            : ((uci[0] - 'a') + (8 - (uci[1] - '0')) * 8, (uci[2] - 'a') + (8 - (uci[3] - '0')) * 8);
    }

    private void Reset()
    {
        _mirror = NewMirror();
        _board = NewBoard();
        _applied = 0;
        _promotion = null;
        Disputed = false;
        _opponentSeen = false;
        NobodyAbout = false;
        ChallengeId = null;
        MatchId = null;
        Seat = null;
        View = null;
        Waiting = 0;
        Phase = OnlinePhase.Idle;
        ClearSelection();
    }

    /// <summary>
    /// A local copy of the game with both seats filled and no clock. Untimed on purpose: the
    /// server owns time, and a second clock here could only ever disagree with it.
    /// </summary>
    private static Match NewMirror()
    {
        var mirror = new Match(MatchClock.Untimed, 0);
        mirror.Join(MirrorWhite, 0);
        mirror.Join(MirrorBlack, 0);
        return mirror;
    }

    private static Engine NewBoard()
    {
        var engine = new Engine(Match.StandardStart);
        engine.GenerateValidMoves();
        return engine;
    }

    /// <summary>Board index is 0 = a8, 63 = h1 — the convention the engine uses throughout.</summary>
    private static string Uci(int fromColumn, int fromRow, int toColumn, int toRow, ChessPieceType? promotion)
    {
        string square = $"{(char)('a' + fromColumn)}{8 - fromRow}{(char)('a' + toColumn)}{8 - toRow}";
        return promotion switch
        {
            ChessPieceType.Queen => square + "q",
            ChessPieceType.Rook => square + "r",
            ChessPieceType.Bishop => square + "b",
            ChessPieceType.Knight => square + "n",
            _ => square,
        };
    }

    private void Changed() => StateChanged?.Invoke();

    /// <summary>
    /// How long to search before admitting the lobby is empty. Long enough not to give up on a
    /// site that has people on it, short enough that a player on a quiet one is not left
    /// staring at a spinner wondering whether the feature works.
    /// </summary>
    private static readonly TimeSpan QuietAfter = TimeSpan.FromSeconds(25);

    private sealed record PendingPromotion(int FromColumn, int FromRow, int ToColumn, int ToRow);
}
