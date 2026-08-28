using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ChessBin.Web;
using ChessEngine.Engine;

namespace ChessBin.Tools.VoteReferee;

/// <summary>
/// Applies the community's vote and Moonforge's reply, then writes the state file the site
/// reads. There is no server: the committed state <em>is</em> the deployment, and this runs
/// on a schedule in CI.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null) return 2;

        VoteState state = File.Exists(options.StatePath)
            ? VoteChess.Parse(await File.ReadAllTextAsync(options.StatePath))
            : VoteState.Empty;

        return options.Command switch
        {
            "start" => await StartAsync(options, state),
            "tally" => await TallyAsync(options, state),
            _ => Fail($"unknown command '{options.Command}'"),
        };
    }

    // ── start ───────────────────────────────────────────────────────────────────

    private static async Task<int> StartAsync(Options options, VoteState state)
    {
        if (state.IsRunning)
        {
            Console.WriteLine("A game is already running; refusing to start another.");
            return 1;
        }

        var next = VoteState.Empty with
        {
            Status = VoteState.Running,
            Game = state.Game + 1,
            Issue = options.Issue,
            CommunityColor = options.CommunityColor,
            DeadlineUtc = options.Now.AddHours(options.Hours).ToString("o", CultureInfo.InvariantCulture),
        };

        await WriteAsync(options.StatePath, next);

        if (!await OpenRoundAsync(options, next))
            return Fail("the game state was written but the vote server would not open a round");

        Console.WriteLine($"Started game {next.Game}: community plays {next.CommunityColor}, " +
                          $"first deadline {next.DeadlineUtc}.");
        return 0;
    }

    // ── tally ───────────────────────────────────────────────────────────────────

    private static async Task<int> TallyAsync(Options options, VoteState state)
    {
        // Holding the launch is the default state: with no game running this does nothing,
        // so the schedule can be live long before the first game is.
        if (!state.IsRunning)
        {
            Console.WriteLine("No game running — nothing to do.");
            return 0;
        }

        // The issue belongs to the game, so a scheduled run needn't be told which it is.
        if (options.Issue <= 0) options.Issue = state.Issue;

        if (state.Deadline is DateTimeOffset deadline && options.Now < deadline)
        {
            Console.WriteLine($"Voting is open until {deadline:u}; {(deadline - options.Now).TotalHours:0.0}h left.");
            return 0;
        }

        IReadOnlyList<string> candidates = VoteChess.LegalMoves(state.Fen);
        IReadOnlyDictionary<string, string>? ballots = await FetchBallotsAsync(options);

        if (ballots is null)
            return Fail("could not read the ballots; leaving the game untouched so no vote is lost");

        TallyResult tally = VoteChess.Tally(ballots, candidates);

        if (!tally.HasWinner)
        {
            // A stalled board looks worse than a slow one, so an empty round extends rather
            // than forfeits — and says so out loud.
            var extended = state with
            {
                DeadlineUtc = options.Now.AddHours(options.Hours).ToString("o", CultureInfo.InvariantCulture),
            };
            await WriteAsync(options.StatePath, extended);
            if (!await OpenRoundAsync(options, extended))
                return Fail("could not extend the round on the vote server");

            await CommentAsync(options, $"No votes this round — voting stays open until {extended.DeadlineUtc}.");
            Console.WriteLine("No votes; deadline extended.");
            return 0;
        }

        PlayResult played = VoteChess.Play(state, tally.Winner!, tally.Counts[0].Votes, options.Now, options.Hours);
        if (!played.Applied)
            return Fail($"the winning vote '{tally.Winner}' is no longer legal in {state.Fen}");

        await WriteAsync(options.StatePath, played.State);

        // Open the next round before announcing this one, so nobody reads the summary and
        // finds nothing to vote on. A finished game has no next round.
        if (!played.State.IsFinished && !await OpenRoundAsync(options, played.State))
            return Fail("the move was played but the next round could not be opened");

        await CommentAsync(options, Summary(tally, played.EngineReply, played.State));
        Console.WriteLine($"Played {tally.Winner} ({tally.Counts[0].Votes} of {tally.Voters} votes)" +
                          (played.EngineReply.Length > 0 ? $"; Moonforge replied {played.EngineReply}." : "."));
        return 0;
    }

    private static string Summary(TallyResult tally, string reply, VoteState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**The community plays {tally.Winner}** — {tally.Counts[0].Votes} of {tally.Voters} votes.");
        sb.AppendLine();

        if (tally.Counts.Count > 1)
        {
            sb.AppendLine("| Move | Votes |");
            sb.AppendLine("| --- | --- |");
            foreach (VoteCount c in tally.Counts.Take(6)) sb.AppendLine($"| {c.San} | {c.Votes} |");
            sb.AppendLine();
        }

        if (reply.Length > 0) sb.AppendLine($"Moonforge replied **{reply}**.");
        sb.AppendLine();
        sb.AppendLine(state.IsFinished
            ? $"That ends the game — {state.Result}."
            : $"Voting is open again until {state.DeadlineUtc}. Vote on the board — this thread is for discussion.");
        sb.AppendLine();
        sb.AppendLine("Board: https://chessbin.com/vote/");
        return sb.ToString();
    }

    // ── the vote server ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the ballots Cloudflare collected. Returns null on any failure — never an empty
    /// tally — because "nobody voted" and "we could not ask" must lead to different actions:
    /// the first extends the round, the second must change nothing at all.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>?> FetchBallotsAsync(Options options)
    {
        if (options.Api.Length == 0)
        {
            Console.Error.WriteLine("no --api given; there is nowhere to read votes from");
            return null;
        }

        try
        {
            using HttpClient client = ApiClient(options);
            using HttpResponseMessage response = await client.GetAsync("vote/round");

            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"the vote server returned {(int)response.StatusCode} for the ballots" +
                    (response.StatusCode == System.Net.HttpStatusCode.Forbidden
                        ? " — check --api-secret matches the Worker's REFEREE_SECRET"
                        : ""));
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!document.RootElement.TryGetProperty("ballots", out JsonElement ballots))
            {
                Console.Error.WriteLine("the vote server's reply had no ballots in it");
                return null;
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty ballot in ballots.EnumerateObject())
            {
                if (ballot.Value.GetString() is string san && san.Length > 0) result[ballot.Name] = san;
            }

            Console.WriteLine($"read {result.Count} ballot(s) from the vote server.");
            return result;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            Console.Error.WriteLine($"could not reach the vote server: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// Publishes the ballot for the position now on the board and clears the previous round.
    /// The candidates are every legal move, so the community can play anything the rules
    /// allow — the server checks ballots against this list, which is the only reason it can
    /// accept votes without knowing anything about chess.
    /// </summary>
    private static async Task<bool> OpenRoundAsync(Options options, VoteState state)
    {
        if (options.Api.Length == 0) return false;

        IReadOnlyList<string> candidates = VoteChess.LegalMoves(state.Fen);
        if (candidates.Count == 0)
        {
            Console.Error.WriteLine($"no legal moves in {state.Fen}; not opening a round");
            return false;
        }

        if (state.Deadline is not DateTimeOffset deadline)
        {
            Console.Error.WriteLine("the state has no deadline; not opening a round");
            return false;
        }

        try
        {
            using HttpClient client = ApiClient(options);
            var payload = new StringContent(
                JsonSerializer.Serialize(new
                {
                    // Rounds count the community's turns, not plies — the engine's reply is
                    // part of the same round, so counting history entries would skip every
                    // other number.
                    round = state.History.Count(move => move.IsCommunity) + 1,
                    candidates,
                    closesAt = deadline.ToUnixTimeMilliseconds(),
                }),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await client.PostAsync("vote/next", payload);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"the vote server returned {(int)response.StatusCode} opening the round");
                return false;
            }

            Console.WriteLine($"opened a round with {candidates.Count} candidate move(s), closing {deadline:u}.");
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            Console.Error.WriteLine($"could not reach the vote server: {exception.Message}");
            return false;
        }
    }

    private static HttpClient ApiClient(Options options)
    {
        string root = options.Api.EndsWith('/') ? options.Api : options.Api + "/";
        var client = new HttpClient { BaseAddress = new Uri(root), Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiSecret);
        return client;
    }

    // ── GitHub ──────────────────────────────────────────────────────────────────
    // Votes no longer come from here — the thread is for discussion, and the referee only
    // posts the round summary to it.

    private static HttpClient GitHubClient(Options options)
    {
        var client = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("chessbin-vote-referee", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (options.Token.Length > 0)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        return client;
    }

    private static async Task CommentAsync(Options options, string body)
    {
        if (options.Issue <= 0 || options.Token.Length == 0)
        {
            Console.WriteLine("(no issue or token — skipping the comment)");
            return;
        }

        using HttpClient client = GitHubClient(options);
        var payload = new StringContent(
            JsonSerializer.Serialize(new { body }), Encoding.UTF8, "application/json");

        using HttpResponseMessage response =
            await client.PostAsync($"repos/{options.Repo}/issues/{options.Issue}/comments", payload);

        if (!response.IsSuccessStatusCode)
            Console.Error.WriteLine($"could not comment: {(int)response.StatusCode}");
    }

    // ── plumbing ────────────────────────────────────────────────────────────────

    private static async Task WriteAsync(string path, VoteState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, VoteChess.Serialise(state));
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    private sealed class Options
    {
        public string Command = "tally";
        public string StatePath = "ChessBin.Web/wwwroot/vote/state.json";
        public string Repo = "";
        public string Token = "";
        public int Issue;
        public int Hours = 24;
        public string Api = "";
        public string ApiSecret = "";
        public string CommunityColor = "White";
        public DateTimeOffset Now = DateTimeOffset.UtcNow;

        public static Options? Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{a} needs a value");
                switch (a)
                {
                    case "start" or "tally": o.Command = a; break;
                    case "--state": o.StatePath = Next(); break;
                    case "--repo": o.Repo = Next(); break;
                    case "--token": o.Token = Next(); break;
                    case "--issue": o.Issue = int.Parse(Next()); break;
                    case "--hours": o.Hours = int.Parse(Next()); break;
                    case "--api": o.Api = Next(); break;
                    case "--api-secret": o.ApiSecret = Next(); break;
                    case "--color": o.CommunityColor = Next(); break;
                    case "--now": o.Now = DateTimeOffset.Parse(Next(), CultureInfo.InvariantCulture); break;
                    default:
                        Console.Error.WriteLine($"unknown argument: {a}");
                        return null;
                }
            }

            if (o.Command == "start" && o.Issue <= 0)
            {
                Console.Error.WriteLine("start needs --issue, the voting thread's number");
                return null;
            }

            if (o.Api.Length > 0 && o.ApiSecret.Length == 0)
            {
                Console.Error.WriteLine("--api needs --api-secret, the Worker's REFEREE_SECRET");
                return null;
            }

            return o;
        }
    }
}
