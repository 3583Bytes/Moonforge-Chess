using ChessEngine.Engine;

namespace ChessBin.Online;

public enum Seat { White, Black }

public enum MatchStatus
{
    /// <summary>One seat filled, waiting for an opponent.</summary>
    Waiting,
    Playing,
    Finished,
}

public enum MatchOutcome
{
    None,
    WhiteWins,
    BlackWins,
    Draw,
    Aborted,
}

public enum MatchReason
{
    None,
    Checkmate,
    Timeout,
    Resignation,
    Stalemate,
    FiftyMove,
    Repetition,
    InsufficientMaterial,
    Agreement,
    Abandoned,
}

/// <summary>Why a submitted move was refused. Every one of these is a thing a client can attempt.</summary>
public enum MoveRejection
{
    Accepted,
    NotYourTurn,
    GameNotRunning,
    UnknownPlayer,
    IllegalMove,
    Malformed,
    ClockExpired,
}

public sealed record MoveResult(MoveRejection Rejection, string? San = null)
{
    public bool Accepted => Rejection == MoveRejection.Accepted;
}

/// <summary>A time control expressed in milliseconds. Zero initial time means untimed.</summary>
public sealed record MatchClock(long InitialMs, long IncrementMs)
{
    public static readonly MatchClock Untimed = new(0, 0);
    public static readonly MatchClock Bullet = new(60_000, 0);
    public static readonly MatchClock Blitz = new(180_000, 2_000);
    public static readonly MatchClock Rapid = new(600_000, 0);
    public bool IsUntimed => InitialMs <= 0;
}

/// <summary>
/// One game between two people, adjudicated authoritatively.
/// <para>
/// Two rules make this trustworthy and testable: legality is decided here by the engine and
/// never by a client, and the current time is always passed in rather than read from the
/// machine. Nothing here touches a network, a disk or a clock of its own.
/// </para>
/// </summary>
public sealed class Match
{
    private readonly Engine _engine;
    private readonly List<string> _moves = [];
    private long _whiteMs;
    private long _blackMs;
    private long _turnStartedAtMs;

    public Match(MatchClock clock, long nowMs, string? startFen = null)
    {
        ArgumentNullException.ThrowIfNull(clock);

        Clock = clock;
        _engine = new Engine(startFen ?? StandardStart);
        _engine.GenerateValidMoves();
        _whiteMs = clock.InitialMs;
        _blackMs = clock.InitialMs;
        _turnStartedAtMs = nowMs;
        CreatedAtMs = nowMs;
        LastEventAtMs = nowMs;
    }

    public const string StandardStart = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    public MatchClock Clock { get; }
    public long CreatedAtMs { get; }
    public MatchStatus Status { get; private set; } = MatchStatus.Waiting;
    public MatchOutcome Outcome { get; private set; } = MatchOutcome.None;
    public MatchReason Reason { get; private set; } = MatchReason.None;

    /// <summary>When the game ended, so a host can reap it. Null while it is still going.</summary>
    public long? FinishedAtMs { get; private set; }

    /// <summary>
    /// When anything last happened. A host needs this to clear away matches nobody joined
    /// and untimed games both players walked away from — neither of which any clock catches.
    /// </summary>
    public long LastEventAtMs { get; private set; }

    /// <summary>The seat with a draw offer outstanding, if any.</summary>
    public Seat? DrawOfferedBy { get; private set; }

    /// <summary>Opaque per-player tokens. A move is only accepted from the seat that owns one.</summary>
    public string? WhiteToken { get; private set; }
    public string? BlackToken { get; private set; }

    public string Fen => _engine.FEN;
    public IReadOnlyList<string> Moves => _moves;
    public Seat ToMove => _engine.WhoseMove == ChessPieceColor.White ? Seat.White : Seat.Black;

    /// <summary>
    /// Time left on a clock as of <paramref name="nowMs"/>. A method rather than a property
    /// because the answer depends on the moment being asked about, and there is deliberately
    /// no ambient clock to fall back on.
    /// </summary>
    public long MsRemaining(Seat seat, long nowMs)
    {
        long held = seat == Seat.White ? _whiteMs : _blackMs;
        if (Clock.IsUntimed || Status != MatchStatus.Playing || seat != ToMove) return held;
        return Math.Max(0, held - Elapsed(nowMs));
    }

    /// <summary>Seats a player and starts the game once both are taken.</summary>
    public Seat? Join(string token, long nowMs)
    {
        if (string.IsNullOrWhiteSpace(token) || Status != MatchStatus.Waiting) return null;
        if (token == WhiteToken || token == BlackToken) return null;   // no playing yourself

        LastEventAtMs = nowMs;

        if (WhiteToken is null)
        {
            WhiteToken = token;
            return Seat.White;
        }

        BlackToken = token;
        Status = MatchStatus.Playing;
        _turnStartedAtMs = nowMs;
        return Seat.Black;
    }

    public Seat? SeatOf(string? token) =>
        token is null ? null
        : token == WhiteToken ? Seat.White
        : token == BlackToken ? Seat.Black
        : null;

    /// <summary>
    /// Applies a move on behalf of a player. The order of checks matters: an expired clock
    /// ends the game even if the move itself was legal, or a player could sit on a losing
    /// position and then move.
    /// </summary>
    public MoveResult Submit(string? token, string? uciOrSan, long nowMs)
    {
        if (Status != MatchStatus.Playing) return new MoveResult(MoveRejection.GameNotRunning);

        Seat? seat = SeatOf(token);
        if (seat is null) return new MoveResult(MoveRejection.UnknownPlayer);

        if (ExpireIfFlagged(nowMs)) return new MoveResult(MoveRejection.ClockExpired);

        if (seat != ToMove) return new MoveResult(MoveRejection.NotYourTurn);
        if (string.IsNullOrWhiteSpace(uciOrSan)) return new MoveResult(MoveRejection.Malformed);

        if (!TryApply(uciOrSan)) return new MoveResult(MoveRejection.IllegalMove);

        string san = string.IsNullOrWhiteSpace(_engine.LastMove.PgnMove) ? uciOrSan : _engine.LastMove.PgnMove;
        _moves.Add(san);

        ChargeClock(seat.Value, nowMs);
        _turnStartedAtMs = nowMs;
        LastEventAtMs = nowMs;

        // A move answers a draw offer. Clearing it on either side's move is what stops an
        // offer made twenty moves ago from being accepted once the game has turned.
        DrawOfferedBy = null;

        AdjudicateBoard(nowMs);
        return new MoveResult(MoveRejection.Accepted, san);
    }

    public bool Resign(string? token, long nowMs)
    {
        if (Status != MatchStatus.Playing) return false;
        if (SeatOf(token) is not Seat seat) return false;

        Finish(seat == Seat.White ? MatchOutcome.BlackWins : MatchOutcome.WhiteWins, MatchReason.Resignation, nowMs);
        return true;
    }

    /// <summary>An opponent who never arrived, or a game abandoned before a move was made.</summary>
    public bool Abort(long nowMs)
    {
        if (Status == MatchStatus.Finished) return false;
        if (_moves.Count > 0 && Status == MatchStatus.Playing) return false;   // a real game must be resigned, not aborted

        Finish(MatchOutcome.Aborted, MatchReason.Abandoned, nowMs);
        return true;
    }

    /// <summary>
    /// Offers a draw, or accepts one the opponent has already offered. Two standing offers
    /// are an agreement, so accepting needs no separate call.
    /// </summary>
    public bool OfferDraw(string? token, long nowMs)
    {
        if (Status != MatchStatus.Playing) return false;
        if (SeatOf(token) is not Seat seat) return false;

        LastEventAtMs = nowMs;

        if (DrawOfferedBy is Seat offered && offered != seat)
        {
            Finish(MatchOutcome.Draw, MatchReason.Agreement, nowMs);
            return true;
        }

        DrawOfferedBy = seat;
        return true;
    }

    /// <summary>Turns down the opponent's offer. Declining your own is not a thing.</summary>
    public bool DeclineDraw(string? token, long nowMs)
    {
        if (Status != MatchStatus.Playing) return false;
        if (SeatOf(token) is not Seat seat) return false;
        if (DrawOfferedBy is not Seat offered || offered == seat) return false;

        DrawOfferedBy = null;
        LastEventAtMs = nowMs;
        return true;
    }

    /// <summary>
    /// Ends the game if whoever is on the move has run out of time. Callable at any moment,
    /// because a player who simply stops responding still has to lose on the clock.
    /// </summary>
    public bool ExpireIfFlagged(long nowMs)
    {
        if (Status != MatchStatus.Playing || Clock.IsUntimed) return false;
        if (MsRemaining(ToMove, nowMs) > 0) return false;

        // Losing on time to someone who could never have mated you is a draw, as it is
        // over the board. Note this asks about the opponent specifically — the engine's
        // own draw flag is about the position as a whole and answers a different question.
        Seat opponent = ToMove == Seat.White ? Seat.Black : Seat.White;
        bool canMate = HasMatingMaterial(_engine.FEN, opponent);
        Finish(
            !canMate ? MatchOutcome.Draw
                : opponent == Seat.White ? MatchOutcome.WhiteWins : MatchOutcome.BlackWins,
            !canMate ? MatchReason.InsufficientMaterial : MatchReason.Timeout,
            nowMs);
        return true;
    }

    // ── internals ───────────────────────────────────────────────────────────────

    private bool TryApply(string move)
    {
        // Coordinates first, because their shape is unambiguous; SAN afterwards, so a
        // client may send either. MoveContent.ParseAN ignores its own parse failure, so
        // the shape has to be checked here rather than trusted to it.
        if (IsCoordinateMove(move))
        {
            _engine.PromoteToPieceType = move.Length == 5
                ? move[4] switch
                {
                    'r' or 'R' => ChessPieceType.Rook,
                    'b' or 'B' => ChessPieceType.Bishop,
                    'n' or 'N' => ChessPieceType.Knight,
                    _ => ChessPieceType.Queen,
                }
                : ChessPieceType.Queen;

            return _engine.MovePieceAN(move[..4]);
        }

        return SanMove.TryApply(_engine, move);
    }

    /// <summary>
    /// Whether a side holds enough material to deliver mate at all: any pawn, rook or
    /// queen will do it, as will two minor pieces. A lone king, or a king with one
    /// knight or one bishop, cannot — which is what makes a flag fall a draw.
    /// </summary>
    public static bool HasMatingMaterial(string fen, Seat seat)
    {
        if (string.IsNullOrWhiteSpace(fen)) return true;      // unreadable: don't invent a draw

        string placement = fen.Split(' ')[0];
        int minors = 0;

        foreach (char square in placement)
        {
            if (!char.IsLetter(square)) continue;
            bool isWhite = char.IsUpper(square);
            if (isWhite != (seat == Seat.White)) continue;

            switch (char.ToLowerInvariant(square))
            {
                case 'p' or 'r' or 'q': return true;
                case 'b' or 'n':
                    if (++minors > 1) return true;
                    break;
            }
        }

        return false;
    }

    private static bool IsCoordinateMove(string move) =>
        move.Length is 4 or 5
        && move[0] is >= 'a' and <= 'h' && move[1] is >= '1' and <= '8'
        && move[2] is >= 'a' and <= 'h' && move[3] is >= '1' and <= '8'
        && (move.Length == 4 || "qrbnQRBN".Contains(move[4]));

    private void ChargeClock(Seat seat, long nowMs)
    {
        if (Clock.IsUntimed) return;

        long spent = Elapsed(nowMs);
        if (seat == Seat.White) _whiteMs = Math.Max(0, _whiteMs - spent) + Clock.IncrementMs;
        else _blackMs = Math.Max(0, _blackMs - spent) + Clock.IncrementMs;
    }

    /// <summary>
    /// How long the current turn has lasted. Clamped at zero because hosts, clients and
    /// clocks disagree: a timestamp from before the turn began must not hand out free time.
    /// </summary>
    private long Elapsed(long nowMs) => Math.Max(0, nowMs - _turnStartedAtMs);

    private void AdjudicateBoard(long nowMs)
    {
        if (_engine.GetWhiteMate()) { Finish(MatchOutcome.BlackWins, MatchReason.Checkmate, nowMs); return; }
        if (_engine.GetBlackMate()) { Finish(MatchOutcome.WhiteWins, MatchReason.Checkmate, nowMs); return; }
        // Order matters. Engine.StaleMate is an umbrella "this game is drawn" flag — the
        // fifty-move rule, repetition and insufficient material all raise it too (verified
        // against the engine, not assumed). So each specific reason is tested first and a
        // true stalemate is what is left over; otherwise every draw would be reported as one.
        if (_engine.InsufficientMaterial) { Finish(MatchOutcome.Draw, MatchReason.InsufficientMaterial, nowMs); return; }
        if (_engine.FiftyMove) { Finish(MatchOutcome.Draw, MatchReason.FiftyMove, nowMs); return; }
        if (_engine.RepeatedMove) { Finish(MatchOutcome.Draw, MatchReason.Repetition, nowMs); return; }
        if (_engine.StaleMate) { Finish(MatchOutcome.Draw, MatchReason.Stalemate, nowMs); }
    }

    private void Finish(MatchOutcome outcome, MatchReason reason, long nowMs)
    {
        Status = MatchStatus.Finished;
        Outcome = outcome;
        Reason = reason;
        FinishedAtMs = nowMs;
        LastEventAtMs = nowMs;
        DrawOfferedBy = null;
    }
}
