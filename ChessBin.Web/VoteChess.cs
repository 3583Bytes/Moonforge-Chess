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

/// <summary>How many people asked for a given move, and when it was first proposed.</summary>
public sealed record VoteCount(string San, int Votes);

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
    /// <summary>
    /// Counts the ballots the vote server collected.
    /// <para>
    /// One person, one vote is already settled by the time ballots get here — the server keys
    /// them by browser and a second vote replaces the first — so this only has to count and
    /// break ties. Ties go to whichever move comes first in the published ballot, which is
    /// alphabetical and therefore checkable by anyone who wants to argue about it.
    /// </para>
    /// </summary>
    /// <param name="ballots">Voter token to the move they chose.</param>
    /// <param name="candidates">The published ballot, in order. Anything not on it is ignored.</param>
    public static TallyResult Tally(
        IReadOnlyDictionary<string, string> ballots,
        IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(ballots);
        ArgumentNullException.ThrowIfNull(candidates);

        // Position on the ballot, which is both the membership test and the tie-break.
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < candidates.Count; i++) position.TryAdd(candidates[i], i);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int voters = 0;

        foreach (string choice in ballots.Values)
        {
            // A ballot for something not on the list cannot be played, so it cannot count.
            // The server refuses these, but the referee is the authority and re-checks.
            if (!position.ContainsKey(choice)) continue;

            counts[choice] = counts.GetValueOrDefault(choice) + 1;
            voters++;
        }

        var ordered = counts
            .Select(entry => new VoteCount(entry.Key, entry.Value))
            .OrderByDescending(count => count.Votes)
            .ThenBy(count => position[count.San])
            .ToArray();

        return new TallyResult(ordered.Length > 0 ? ordered[0].San : null, ordered, voters);
    }

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

    /// <summary>
    /// Every legal move in a position, in notation, sorted so the order is the same every time.
    /// <para>
    /// This is what the referee publishes as the ballot: the community can play anything the
    /// rules allow, and the vote server — which knows nothing about chess — only has to check
    /// that a ballot names one of them.
    /// </para>
    /// <para>
    /// Each candidate is produced by actually playing the move and reading back the notation
    /// the engine generated, rather than by composing notation here. That way disambiguation
    /// ("Nbd2", "R1e2"), captures, castling and promotion all read exactly as the engine will
    /// write them when the move is really played, so a ballot can never fail to match.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> LegalMoves(string fen)
    {
        if (string.IsNullOrWhiteSpace(fen)) return [];

        var survey = new Engine(fen);
        survey.GenerateValidMoves();
        ChessPieceColor mover = survey.WhoseMove;

        // Sorted rather than in generation order: a stable, alphabetical ballot is easy to scan
        // in a list of thirty, and it gives ties a tie-break anyone can check for themselves.
        var moves = new SortedSet<string>(StringComparer.Ordinal);

        for (byte column = 0; column < 8; column++)
        {
            for (byte row = 0; row < 8; row++)
            {
                ChessPieceType piece = survey.GetPieceTypeAt(column, row);
                if (piece == ChessPieceType.None) continue;
                if (survey.GetPieceColorAt(column, row) != mover) continue;

                byte[][]? targets = survey.GetValidMoves(column, row);
                if (targets is null) continue;

                foreach (byte[] target in targets)
                {
                    bool promotes = piece == ChessPieceType.Pawn
                        && target[1] == (mover == ChessPieceColor.White ? 0 : 7);

                    foreach (ChessPieceType choice in promotes ? PromotionChoices : NoPromotion)
                    {
                        // A fresh engine per move: the mailbox engine has no cheap unmake, and
                        // this runs once a day in CI, so clarity beats the microseconds.
                        var play = new Engine(fen);
                        play.GenerateValidMoves();
                        play.PromoteToPieceType = choice;

                        if (!play.MovePiece(column, row, target[0], target[1])) continue;

                        string san = play.LastMove.PgnMove;
                        if (!string.IsNullOrWhiteSpace(san)) moves.Add(san);
                    }
                }
            }
        }

        return [.. moves];
    }

    private static readonly ChessPieceType[] PromotionChoices =
        [ChessPieceType.Queen, ChessPieceType.Rook, ChessPieceType.Bishop, ChessPieceType.Knight];

    private static readonly ChessPieceType[] NoPromotion = [ChessPieceType.Queen];
}
