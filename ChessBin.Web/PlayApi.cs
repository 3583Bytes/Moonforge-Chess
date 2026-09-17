using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ChessBin.Online;

namespace ChessBin.Web;

/// <summary>The game as the server reports it. Seats, turn order and clocks — never legality.</summary>
/// <param name="Seat">Which side this browser is playing, or null for a spectator.</param>
/// <param name="Uci">
/// The moves in coordinate form. This is what gets replayed, because SAN is ambiguous without
/// the position it was written in and the whole point is that this client re-derives everything.
/// </param>
/// <param name="DrawOfferedToYou">True only for the player being offered one, so the UI has nothing to work out.</param>
public sealed record MatchView(
    MatchStatus Status,
    MatchOutcome Outcome,
    MatchReason Reason,
    Seat? Seat,
    IReadOnlyList<string> Moves,
    IReadOnlyList<string> Uci,
    Seat ToMove,
    long WhiteMs,
    long BlackMs,
    Seat? DrawOfferedBy,
    bool DrawOfferedToYou,
    bool OpponentJoined)
{
    public bool IsOver => Status == MatchStatus.Finished;
}

/// <summary>
/// What came of trying to sit down at a game. The reason matters: a link whose game is already
/// full is a dead end the player needs telling about, while a dropped request is worth retrying,
/// and both look identical if all you get back is null.
/// </summary>
public sealed record JoinOutcome(MatchView? View, string? Reason)
{
    public static readonly JoinOutcome Unreachable = new(null, null);

    /// <summary>The server answered, and the answer was no. Retrying will not help.</summary>
    public bool Refused => View is null && Reason is not null;
}

/// <summary>A game created from a challenge link, waiting for whoever is sent it.</summary>
public sealed record ChallengeOutcome(string? MatchId, Seat? Seat)
{
    public static readonly ChallengeOutcome Failed = new(null, null);
    public bool Created => MatchId is not null;
}

/// <summary>What came of asking for a game.</summary>
public sealed record SeekOutcome(bool Paired, bool Queued, string? MatchId, Seat? Seat, int Waiting)
{
    /// <summary>The API could not be reached. Distinct from "nobody is waiting".</summary>
    public static readonly SeekOutcome Unreachable = new(false, false, null, null, 0);

    public bool Failed => !Paired && !Queued;
}

/// <summary>
/// The hosted game, as the page sees it. An interface so the session's logic can be tested
/// without a network — see <c>OnlineSessionTests</c>.
/// </summary>
public interface IPlayApi
{
    Task<SeekOutcome> SeekAsync(string token, MatchClock clock, CancellationToken cancellationToken = default);

    Task CancelSeekAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Creates a game with one seat open, for sending to a friend.</summary>
    Task<ChallengeOutcome> OpenChallengeAsync(string token, MatchClock clock, Seat? side = null,
                                              CancellationToken cancellationToken = default);

    Task<JoinOutcome> JoinAsync(string matchId, string token, CancellationToken cancellationToken = default);

    Task<MatchView?> StateAsync(string matchId, string token, CancellationToken cancellationToken = default);

    Task<MatchView?> MoveAsync(string matchId, string token, string san, string uci,
                               MatchOutcome? outcome, MatchReason? reason,
                               CancellationToken cancellationToken = default);

    /// <summary>resign, draw, decline or abort — the actions that carry no payload beyond the token.</summary>
    Task<MatchView?> ActAsync(string matchId, string token, string action, CancellationToken cancellationToken = default);

    /// <summary>"My engine says the move at this ply is not legal." Aborts the game.</summary>
    Task<MatchView?> DisputeAsync(string matchId, string token, int ply, CancellationToken cancellationToken = default);
}

// Blazor WASM trims on publish, which can strip the reflection metadata reflection-based JSON
// needs. A source-generated context keeps this working in the published app — the same reason
// VoteApiJsonContext and PuzzleJsonContext exist.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SeekResponseDto))]
[JsonSerializable(typeof(SeekRequestDto))]
[JsonSerializable(typeof(ChallengeRequestDto))]
[JsonSerializable(typeof(TokenRequestDto))]
[JsonSerializable(typeof(MoveRequestDto))]
[JsonSerializable(typeof(DisputeRequestDto))]
[JsonSerializable(typeof(MatchEnvelopeDto))]
internal sealed partial class PlayApiJsonContext : JsonSerializerContext;

internal sealed record ClockDto(long InitialMs, long IncrementMs);

internal sealed record SeekRequestDto(string Token, ClockDto Clock);

internal sealed record ChallengeRequestDto(string Token, ClockDto Clock, string? Seat);

internal sealed record TokenRequestDto(string Token);

internal sealed record EndClaimDto(string Outcome, string Reason);

internal sealed record MoveRequestDto(string Token, string San, string Uci, EndClaimDto? Ends);

internal sealed record DisputeRequestDto(string Token, int Ply);

internal sealed record SeekResponseDto(bool Ok, bool Paired, bool Queued, string? MatchId, string? Seat, int Waiting, string? Reason);

internal sealed record MatchEnvelopeDto(bool Ok, string? Reason, MatchStateDto? State);

internal sealed record MatchStateDto(
    string? Status,
    string? Outcome,
    string? Reason,
    string? Seat,
    string[]? Moves,
    string[]? Uci,
    string? ToMove,
    long WhiteMs,
    long BlackMs,
    string? DrawOfferedBy,
    bool DrawOfferedToYou,
    bool OpponentJoined);

/// <summary>
/// Talks to the Cloudflare Worker that hosts online games.
/// <para>
/// Every failure here is non-fatal and returns null rather than throwing: a dropped request in
/// the middle of a game must leave the board exactly as it was so the next poll can recover,
/// not tear the page down. The caller decides how long a silence is worth worrying about.
/// </para>
/// </summary>
public sealed class HttpPlayApi(HttpClient http) : IPlayApi
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<SeekOutcome> SeekAsync(string token, MatchClock clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);

        try
        {
            HttpResponseMessage response = await _http.PostAsJsonAsync(
                "play/seek",
                new SeekRequestDto(token, new ClockDto(clock.InitialMs, clock.IncrementMs)),
                PlayApiJsonContext.Default.SeekRequestDto,
                cancellationToken);

            SeekResponseDto? dto = await response.Content.ReadFromJsonAsync(
                PlayApiJsonContext.Default.SeekResponseDto, cancellationToken);

            if (dto is null || !dto.Ok) return SeekOutcome.Unreachable;

            return new SeekOutcome(dto.Paired, dto.Queued, dto.MatchId, ParseSeat(dto.Seat), dto.Waiting);
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            return SeekOutcome.Unreachable;
        }
    }

    public async Task CancelSeekAsync(string token, CancellationToken cancellationToken = default)
    {
        try
        {
            await _http.PostAsJsonAsync("play/cancel", new TokenRequestDto(token),
                PlayApiJsonContext.Default.TokenRequestDto, cancellationToken);
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            // Giving up on a seek that the server may still hold is harmless: it times out
            // there, and the player has already left the queue as far as this page is concerned.
        }
    }

    public async Task<ChallengeOutcome> OpenChallengeAsync(string token, MatchClock clock, Seat? side = null,
                                                           CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);

        try
        {
            HttpResponseMessage response = await _http.PostAsJsonAsync(
                "play/open",
                new ChallengeRequestDto(token, new ClockDto(clock.InitialMs, clock.IncrementMs),
                                        side is null ? null : SeatWire(side.Value)),
                PlayApiJsonContext.Default.ChallengeRequestDto,
                cancellationToken);

            SeekResponseDto? dto = await response.Content.ReadFromJsonAsync(
                PlayApiJsonContext.Default.SeekResponseDto, cancellationToken);

            return dto is null || !dto.Ok || dto.MatchId is null
                ? ChallengeOutcome.Failed
                : new ChallengeOutcome(dto.MatchId, ParseSeat(dto.Seat));
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            return ChallengeOutcome.Failed;
        }
    }

    /// <summary>
    /// Announces that this player has arrived — or, for a challenge link, claims the open seat.
    /// The seat is never asked for: a lobby game had both decided when it paired, and a
    /// challenge gives away whichever chair its creator left.
    /// </summary>
    public async Task<JoinOutcome> JoinAsync(string matchId, string token, CancellationToken cancellationToken = default)
    {
        try
        {
            HttpResponseMessage response = await _http.PostAsJsonAsync(
                $"play/{matchId}/join", new TokenRequestDto(token),
                PlayApiJsonContext.Default.TokenRequestDto, cancellationToken);

            MatchEnvelopeDto? dto = await response.Content.ReadFromJsonAsync(
                PlayApiJsonContext.Default.MatchEnvelopeDto, cancellationToken);

            if (dto is null) return JoinOutcome.Unreachable;
            return new JoinOutcome(ToView(dto), dto.Ok ? null : dto.Reason ?? "refused");
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            return JoinOutcome.Unreachable;
        }
    }

    public async Task<MatchView?> StateAsync(string matchId, string token, CancellationToken cancellationToken = default)
    {
        try
        {
            MatchEnvelopeDto? dto = await _http.GetFromJsonAsync(
                $"play/{matchId}/state?token={Uri.EscapeDataString(token)}",
                PlayApiJsonContext.Default.MatchEnvelopeDto, cancellationToken);

            return ToView(dto);
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            return null;
        }
    }

    public Task<MatchView?> MoveAsync(string matchId, string token, string san, string uci,
                                      MatchOutcome? outcome, MatchReason? reason,
                                      CancellationToken cancellationToken = default)
    {
        EndClaimDto? ends = outcome is null || reason is null
            ? null
            : new EndClaimDto(OutcomeWire(outcome.Value), ReasonWire(reason.Value));

        return PostAsync(matchId, "move", new MoveRequestDto(token, san, uci, ends),
            PlayApiJsonContext.Default.MoveRequestDto, cancellationToken);
    }

    public Task<MatchView?> ActAsync(string matchId, string token, string action, CancellationToken cancellationToken = default) =>
        PostAsync(matchId, action, new TokenRequestDto(token),
            PlayApiJsonContext.Default.TokenRequestDto, cancellationToken);

    public Task<MatchView?> DisputeAsync(string matchId, string token, int ply, CancellationToken cancellationToken = default) =>
        PostAsync(matchId, "dispute", new DisputeRequestDto(token, ply),
            PlayApiJsonContext.Default.DisputeRequestDto, cancellationToken);

    private async Task<MatchView?> PostAsync<T>(string matchId, string action, T payload,
                                                JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        try
        {
            HttpResponseMessage response = await _http.PostAsJsonAsync(
                $"play/{matchId}/{action}", payload, typeInfo, cancellationToken);

            MatchEnvelopeDto? dto = await response.Content.ReadFromJsonAsync(
                PlayApiJsonContext.Default.MatchEnvelopeDto, cancellationToken);

            return ToView(dto);
        }
        catch (Exception exception) when (IsTransport(exception))
        {
            return null;
        }
    }

    private static bool IsTransport(Exception exception) =>
        exception is HttpRequestException or JsonException or TaskCanceledException or NotSupportedException;

    private static MatchView? ToView(MatchEnvelopeDto? dto)
    {
        if (dto?.State is not MatchStateDto state) return null;

        return new MatchView(
            ParseStatus(state.Status),
            ParseOutcome(state.Outcome),
            ParseReason(state.Reason),
            ParseSeat(state.Seat),
            state.Moves ?? [],
            state.Uci ?? [],
            ParseSeat(state.ToMove) ?? Online.Seat.White,
            state.WhiteMs,
            state.BlackMs,
            ParseSeat(state.DrawOfferedBy),
            state.DrawOfferedToYou,
            state.OpponentJoined);
    }

    // ── wire mapping ────────────────────────────────────────────────────────────
    //
    // Written out rather than driven by a naming policy: the wire format is a contract with
    // server/src/match.ts, and an enum member renamed on one side should break a test here
    // rather than silently start round-tripping as something else.

    internal static Seat? ParseSeat(string? value) => value switch
    {
        "white" => Online.Seat.White,
        "black" => Online.Seat.Black,
        _ => null,
    };

    internal static MatchStatus ParseStatus(string? value) => value switch
    {
        "playing" => MatchStatus.Playing,
        "finished" => MatchStatus.Finished,
        _ => MatchStatus.Waiting,
    };

    internal static MatchOutcome ParseOutcome(string? value) => value switch
    {
        "white" => MatchOutcome.WhiteWins,
        "black" => MatchOutcome.BlackWins,
        "draw" => MatchOutcome.Draw,
        "aborted" => MatchOutcome.Aborted,
        _ => MatchOutcome.None,
    };

    internal static MatchReason ParseReason(string? value) => value switch
    {
        "checkmate" => MatchReason.Checkmate,
        "stalemate" => MatchReason.Stalemate,
        "repetition" => MatchReason.Repetition,
        "fifty-move" => MatchReason.FiftyMove,
        "insufficient" => MatchReason.InsufficientMaterial,
        "timeout" => MatchReason.Timeout,
        "resignation" => MatchReason.Resignation,
        "agreement" => MatchReason.Agreement,
        "abandoned" => MatchReason.Abandoned,
        "disputed" => MatchReason.Disputed,
        _ => MatchReason.None,
    };

    internal static string SeatWire(Seat seat) => seat == Online.Seat.White ? "white" : "black";

    internal static string OutcomeWire(MatchOutcome outcome) => outcome switch
    {
        MatchOutcome.WhiteWins => "white",
        MatchOutcome.BlackWins => "black",
        MatchOutcome.Draw => "draw",
        _ => "none",
    };

    internal static string ReasonWire(MatchReason reason) => reason switch
    {
        MatchReason.Checkmate => "checkmate",
        MatchReason.Stalemate => "stalemate",
        MatchReason.Repetition => "repetition",
        MatchReason.FiftyMove => "fifty-move",
        MatchReason.InsufficientMaterial => "insufficient",
        _ => "none",
    };
}
