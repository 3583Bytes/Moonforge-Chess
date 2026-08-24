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
    private const string BotLogin = "github-actions[bot]";

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

        DateTimeOffset windowStart = LastTallyAt(state, options);
        IReadOnlyList<VoteComment> comments = await FetchCommentsAsync(options);
        TallyResult tally = VoteChess.Tally(state.Fen, comments, BotLogin, windowStart);

        if (!tally.HasWinner)
        {
            // A stalled board looks worse than a slow one, so an empty round extends rather
            // than forfeits — and says so out loud.
            var extended = state with
            {
                DeadlineUtc = options.Now.AddHours(options.Hours).ToString("o", CultureInfo.InvariantCulture),
            };
            await WriteAsync(options.StatePath, extended);
            await CommentAsync(options, $"No votes this round — voting stays open until {extended.DeadlineUtc}.");
            Console.WriteLine("No votes; deadline extended.");
            return 0;
        }

        PlayResult played = VoteChess.Play(state, tally.Winner!, tally.Counts[0].Votes, options.Now, options.Hours);
        if (!played.Applied)
            return Fail($"the winning vote '{tally.Winner}' is no longer legal in {state.Fen}");

        await WriteAsync(options.StatePath, played.State);
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
            : $"Voting is open again until {state.DeadlineUtc}. Reply with a move in algebraic notation, for example `Nf6`.");
        sb.AppendLine();
        sb.AppendLine("Board: https://chessbin.com/vote/");
        return sb.ToString();
    }

    /// <summary>Votes only count from the bot's last post, so a round never reuses old comments.</summary>
    private static DateTimeOffset LastTallyAt(VoteState state, Options options) =>
        state.Deadline is DateTimeOffset deadline
            ? deadline.AddHours(-options.Hours)
            : options.Now.AddHours(-options.Hours);

    // ── GitHub ──────────────────────────────────────────────────────────────────

    private static HttpClient Client(Options options)
    {
        var client = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("chessbin-vote-referee", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (options.Token.Length > 0)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        return client;
    }

    private static async Task<IReadOnlyList<VoteComment>> FetchCommentsAsync(Options options)
    {
        if (options.Issue <= 0) return [];

        using HttpClient client = Client(options);
        var all = new List<VoteComment>();

        for (int page = 1; page <= 10; page++)
        {
            string url = $"repos/{options.Repo}/issues/{options.Issue}/comments?per_page=100&page={page}";
            using HttpResponseMessage response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"GitHub returned {(int)response.StatusCode} for {url}");
                break;
            }

            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement.ArrayEnumerator items = document.RootElement.EnumerateArray();
            int count = 0;

            foreach (JsonElement item in items)
            {
                count++;
                string author = item.GetProperty("user").GetProperty("login").GetString() ?? "";
                string body = item.GetProperty("body").GetString() ?? "";
                DateTimeOffset created = item.GetProperty("created_at").GetDateTimeOffset();
                all.Add(new VoteComment(author, body, created));
            }

            if (count < 100) break;
        }

        return all;
    }

    private static async Task CommentAsync(Options options, string body)
    {
        if (options.Issue <= 0 || options.Token.Length == 0)
        {
            Console.WriteLine("(no issue or token — skipping the comment)");
            return;
        }

        using HttpClient client = Client(options);
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

            return o;
        }
    }
}
