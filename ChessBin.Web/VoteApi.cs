using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChessBin.Web;

/// <summary>One move a visitor can choose, with how many have chosen it so far.</summary>
public sealed record VoteOption(string San, int Votes);

/// <summary>
/// The public state of the open round, as the Worker reports it. Aggregates only — who voted
/// is never published.
/// </summary>
public sealed record VoteTally(
    int Round,
    long? ClosesAt,
    bool Open,
    int Voters,
    IReadOnlyList<VoteOption> Counts)
{
    public static readonly VoteTally None = new(0, null, false, 0, []);

    /// <summary>True when a round exists at all, open or not.</summary>
    public bool HasRound => Round > 0;

    public DateTimeOffset? Deadline =>
        ClosesAt is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
}

/// <summary>Why a vote was or wasn't recorded. Mirrors the Worker's reason codes.</summary>
public enum CastStatus
{
    Recorded,
    NoRound,
    RoundClosed,
    UnknownMove,
    BadToken,
    RateLimited,
    /// <summary>The request never got an answer — offline, or the API is down.</summary>
    Unreachable,
}

public sealed record CastResult(CastStatus Status, string? Choice)
{
    public bool Recorded => Status == CastStatus.Recorded;
    public static readonly CastResult Unreachable = new(CastStatus.Unreachable, null);
}

/// <summary>
/// The ballot box, as the page sees it. An interface so the page's logic can be tested
/// without a network — see <c>VoteBallotTests</c>.
/// </summary>
public interface IVoteApi
{
    Task<VoteTally> GetTallyAsync(CancellationToken cancellationToken = default);

    Task<CastResult> CastAsync(string token, string san, CancellationToken cancellationToken = default);
}

// Blazor WASM trims on publish, which can strip the reflection metadata reflection-based JSON
// needs. A source-generated context keeps this working in the published app — the same reason
// PuzzleJsonContext exists.
//
// Separate from VoteChess.cs's VoteJsonContext on purpose: that one writes state.json with a
// camelCase policy and indentation, which is a file format. This one reads the Worker's wire
// format. Same feature, two different contracts.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(VoteTallyDto))]
[JsonSerializable(typeof(CastResponseDto))]
[JsonSerializable(typeof(CastRequestDto))]
internal sealed partial class VoteApiJsonContext : JsonSerializerContext;

internal sealed record VoteTallyDto(
    int Round,
    long? ClosesAt,
    bool Open,
    int Voters,
    VoteOptionDto[]? Counts);

internal sealed record VoteOptionDto(string? San, int Votes);

internal sealed record CastResponseDto(bool Ok, string? Reason, string? Choice);

internal sealed record CastRequestDto(string Token, string San);

/// <summary>
/// Talks to the Cloudflare Worker over HTTP.
/// <para>
/// Every failure here is non-fatal by design: the page's board comes from a static file and
/// renders with or without this. A visitor who cannot reach the API sees the game, just not
/// the buttons — which beats an error screen over a feature that is only part of the page.
/// </para>
/// </summary>
public sealed class HttpVoteApi(HttpClient http) : IVoteApi
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<VoteTally> GetTallyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            VoteTallyDto? dto = await _http.GetFromJsonAsync(
                "vote/tally", VoteApiJsonContext.Default.VoteTallyDto, cancellationToken);

            if (dto is null) return VoteTally.None;

            var options = (dto.Counts ?? [])
                .Where(option => !string.IsNullOrWhiteSpace(option.San))
                .Select(option => new VoteOption(option.San!, option.Votes))
                .ToArray();

            return new VoteTally(dto.Round, dto.ClosesAt, dto.Open, dto.Voters, options);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return VoteTally.None;
        }
    }

    public async Task<CastResult> CastAsync(
        string token,
        string san,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpResponseMessage response = await _http.PostAsJsonAsync(
                "vote/cast",
                new CastRequestDto(token, san),
                VoteApiJsonContext.Default.CastRequestDto,
                cancellationToken);

            CastResponseDto? dto = await response.Content.ReadFromJsonAsync(
                VoteApiJsonContext.Default.CastResponseDto, cancellationToken);

            if (dto is null) return CastResult.Unreachable;

            return new CastResult(dto.Ok ? CastStatus.Recorded : StatusFor(dto.Reason), dto.Choice);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return CastResult.Unreachable;
        }
    }

    /// <summary>An unrecognised reason is treated as unreachable rather than guessed at.</summary>
    internal static CastStatus StatusFor(string? reason) => reason switch
    {
        "no_round" => CastStatus.NoRound,
        "round_closed" => CastStatus.RoundClosed,
        "unknown_move" => CastStatus.UnknownMove,
        "bad_token" => CastStatus.BadToken,
        "rate_limited" => CastStatus.RateLimited,
        _ => CastStatus.Unreachable,
    };
}
