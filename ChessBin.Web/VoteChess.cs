using System.Text.Json;
using System.Text.Json.Serialization;
using ChessEngine.Engine;

namespace ChessBin.Web;

/// <summary>One move in the community game, and how it was arrived at.</summary>
public sealed record VoteMove(int Ply, string San, string Uci, string By, int Votes)
{
    public const string Community = "community";
    public const string Engine = "moonforge";

    public bool IsCommunity => By == Community;
    public int MoveNumber => (Ply + 1) / 2;
    public string Reference => Ply % 2 == 1 ? $"{MoveNumber}. {San}" : $"{MoveNumber}… {San}";
}

/// <summary>
/// The whole game, as committed to the repo. The site reads this file and nothing else;
/// there is no server, so the state <em>is</em> the deployment.
/// </summary>
public sealed record VoteState(
    int Version,
    string Status,
    int Game,
    int Issue,
    string CommunityColor,
    string StartFen,
    string Fen,
    string DeadlineUtc,
    string Result,
    VoteMove[] History)
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Finished = "finished";

    public bool IsRunning => Status == Running;
    public bool IsFinished => Status == Finished;
    public bool HasGame => Status != Idle;

    public ChessPieceColor Community =>
        CommunityColor.Equals("black", StringComparison.OrdinalIgnoreCase)
            ? ChessPieceColor.Black
            : ChessPieceColor.White;

    public DateTimeOffset? Deadline =>
        DateTimeOffset.TryParse(DeadlineUtc, out DateTimeOffset d) ? d : null;

    public static VoteState Empty => new(
        Version: 1, Status: Idle, Game: 0, Issue: 0, CommunityColor: "White",
        StartFen: ChessGameSession.StartingFen, Fen: ChessGameSession.StartingFen,
        DeadlineUtc: "", Result: "", History: []);
}

/// <summary>A comment on the voting issue, as read from the GitHub API.</summary>
public sealed record VoteComment(string Author, string Body, DateTimeOffset CreatedAt);

/// <summary>How many people asked for a given move, and when it was first proposed.</summary>
public sealed record VoteCount(string San, int Votes, DateTimeOffset FirstProposed);

public sealed record TallyResult(string? Winner, IReadOnlyList<VoteCount> Counts, int Voters)
{
    public bool HasWinner => Winner is not null;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(VoteState))]
internal sealed partial class VoteJsonContext : JsonSerializerContext;

/// <summary>The outcome of playing one round: the new state and what the engine answered.</summary>
public sealed record PlayResult(VoteState State, string EngineReply, bool Applied);

public static class VoteChess
{
    public static string StatePath => "vote/state.json";

    /// <summary>Characters people wrap a move in — markdown, punctuation, quoting.</summary>
    private static readonly char[] Noise = ['*', '_', '`', '>', '"', '\'', '.', ',', '!', '?', ':', ';', '(', ')', '[', ']'];

    public static VoteState Parse(string json) =>
        JsonSerializer.Deserialize(json, VoteJsonContext.Default.VoteState) ?? VoteState.Empty;

    public static string Serialise(VoteState state) =>
        JsonSerializer.Serialize(state, VoteJsonContext.Default.VoteState) + "\n";

    /// <summary>
    /// Counts the votes in a window of comments.
    /// <para>
    /// One vote per person — their most recent comment wins, so anyone can change their mind.
    /// A comment votes for the first legal move it mentions, which lets people write "I say
    /// Nf6" without learning a command syntax. Ties go to whichever move was proposed first,
    /// because a workflow that has to be reproducible cannot roll dice.
    /// </para>
    /// </summary>
    public static TallyResult Tally(
        string fen,
        IEnumerable<VoteComment> comments,
        string botLogin,
        DateTimeOffset windowStart)
    {
        ArgumentNullException.ThrowIfNull(comments);

        var latestPerVoter = new Dictionary<string, VoteComment>(StringComparer.OrdinalIgnoreCase);
        foreach (VoteComment comment in comments)
        {
            if (comment.CreatedAt < windowStart) continue;
            if (string.Equals(comment.Author, botLogin, StringComparison.OrdinalIgnoreCase)) continue;

            if (!latestPerVoter.TryGetValue(comment.Author, out VoteComment? held) || comment.CreatedAt > held.CreatedAt)
                latestPerVoter[comment.Author] = comment;
        }

        var votes = new Dictionary<string, (int Count, DateTimeOffset First)>(StringComparer.Ordinal);
        int voters = 0;

        foreach (VoteComment comment in latestPerVoter.Values.OrderBy(c => c.CreatedAt))
        {
            string? move = FindMove(fen, comment.Body);
            if (move is null) continue;

            voters++;
            if (votes.TryGetValue(move, out var held))
                votes[move] = (held.Count + 1, held.First);
            else
                votes[move] = (1, comment.CreatedAt);
        }

        var counts = votes
            .Select(v => new VoteCount(v.Key, v.Value.Count, v.Value.First))
            .OrderByDescending(v => v.Votes)
            .ThenBy(v => v.FirstProposed)
            .ToArray();

        return new TallyResult(counts.Length > 0 ? counts[0].San : null, counts, voters);
    }

    /// <summary>
    /// Plays the winning vote and Moonforge's answer, returning the state to commit.
    /// <para>
    /// Kept out of the referee tool so it can be tested without a GitHub token: this is the
    /// only part that changes the game, and it is the part worth being sure about.
    /// </para>
    /// </summary>
    public static PlayResult Play(VoteState state, string winningSan, int votes, DateTimeOffset now, int hours)
    {
        ArgumentNullException.ThrowIfNull(state);

        var engine = new Engine(state.Fen);
        engine.GenerateValidMoves();

        if (!SanMove.TryApply(engine, winningSan))
            return new PlayResult(state, "", Applied: false);

        var history = state.History.ToList();
        history.Add(Record(engine, history.Count + 1, winningSan, VoteMove.Community, votes));

        string reply = "";
        if (!engine.IsGameOver())
        {
            EngineSearchResult best = engine.SearchBestMove();
            if (best.HasMove && PuzzleData.TryApplyUci(engine, best.BestMove))
            {
                reply = string.IsNullOrWhiteSpace(engine.LastMove.PgnMove) ? best.BestMove : engine.LastMove.PgnMove;
                history.Add(Record(engine, history.Count + 1, reply, VoteMove.Engine, 0));
            }
        }

        bool over = engine.IsGameOver();

        return new PlayResult(state with
        {
            Fen = engine.FEN,
            History = [.. history],
            Status = over ? VoteState.Finished : VoteState.Running,
            Result = over ? Describe(engine, state.Community) : "",
            DeadlineUtc = over ? "" : now.AddHours(hours).ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        }, reply, Applied: true);
    }

    private static VoteMove Record(Engine engine, int ply, string san, string by, int votes)
    {
        MoveContent last = engine.LastMove;
        byte from = last.MovingPiecePrimary.SrcPosition, to = last.MovingPiecePrimary.DstPosition;
        string uci = $"{(char)('a' + from % 8)}{8 - from / 8}{(char)('a' + to % 8)}{8 - to / 8}";
        return new VoteMove(ply, san, uci, by, votes);
    }

    private static string Describe(Engine engine, ChessPieceColor community)
    {
        if (engine.GetWhiteMate()) return community == ChessPieceColor.White ? "Moonforge wins" : "The community wins";
        if (engine.GetBlackMate()) return community == ChessPieceColor.Black ? "Moonforge wins" : "The community wins";
        return "Drawn";
    }

    /// <summary>The first legal move mentioned in a comment, or null if it names none.</summary>
    public static string? FindMove(string fen, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        foreach (string raw in body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim().Trim(Noise);
            if (token.Length is < 2 or > 7) continue;
            if (SanMove.IsLegal(fen, token)) return token.TrimEnd('+', '#', '!', '?');
        }

        return null;
    }
}
