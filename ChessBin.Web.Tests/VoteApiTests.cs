using System.Net;
using System.Text;
using ChessBin.Web;

namespace ChessBin.Web.Tests;

/// <summary>
/// The HTTP client for the vote Worker, against canned responses. The point of these is the
/// failure paths: the vote panel is one part of a page whose board comes from a static file,
/// so an API that is down must cost the visitor the buttons and nothing else.
/// </summary>
[TestFixture]
public class VoteApiTests
{
    /// <summary>Answers every request with a fixed response, and remembers what was asked.</summary>
    private sealed class Canned(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? RequestedUri { get; private set; }
        public string? RequestBody { get; private set; }
        public Exception? Throw { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUri = request.RequestUri;
            if (request.Content is not null)
                RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            if (Throw is not null) throw Throw;

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (HttpVoteApi Api, Canned Handler) Build(
        HttpStatusCode status, string body, Exception? throws = null)
    {
        var handler = new Canned(status, body) { Throw = throws };
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };
        return (new HttpVoteApi(client), handler);
    }

    // ── reading the tally ───────────────────────────────────────────────────

    [Test]
    public async Task A_tally_is_read_from_the_workers_shape()
    {
        const string body = """
            {"round":4,"closesAt":1700000000000,"open":true,"voters":3,
             "counts":[{"san":"e4","votes":2},{"san":"d4","votes":1}]}
            """;
        (HttpVoteApi api, Canned handler) = Build(HttpStatusCode.OK, body);

        VoteTally tally = await api.GetTallyAsync();

        Assert.That(handler.RequestedUri?.AbsoluteUri, Is.EqualTo("https://api.test/vote/tally"));
        Assert.That(tally.Round, Is.EqualTo(4));
        Assert.That(tally.Open, Is.True);
        Assert.That(tally.Voters, Is.EqualTo(3));
        Assert.That(tally.Counts.Select(o => o.San), Is.EqualTo(new[] { "e4", "d4" }));
        Assert.That(tally.Counts[0].Votes, Is.EqualTo(2));
        Assert.That(tally.Deadline, Is.EqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000)));
    }

    [Test]
    public async Task No_round_reads_as_no_round_rather_than_an_error()
    {
        (HttpVoteApi api, _) = Build(
            HttpStatusCode.OK, """{"round":0,"closesAt":null,"open":false,"voters":0,"counts":[]}""");

        VoteTally tally = await api.GetTallyAsync();

        Assert.That(tally.HasRound, Is.False);
        Assert.That(tally.Counts, Is.Empty);
        Assert.That(tally.Deadline, Is.Null);
    }

    [Test]
    public async Task An_unreachable_server_leaves_the_page_working()
    {
        (HttpVoteApi api, _) = Build(HttpStatusCode.OK, "", new HttpRequestException("offline"));

        VoteTally tally = await api.GetTallyAsync();

        Assert.That(tally, Is.EqualTo(VoteTally.None));
    }

    [Test]
    public async Task Nonsense_from_the_server_is_not_trusted()
    {
        (HttpVoteApi api, _) = Build(HttpStatusCode.OK, "<html>a proxy error page</html>");

        Assert.That(await api.GetTallyAsync(), Is.EqualTo(VoteTally.None));
    }

    [Test]
    public async Task A_nameless_option_is_dropped_rather_than_rendered_blank()
    {
        const string body = """
            {"round":1,"closesAt":null,"open":true,"voters":1,
             "counts":[{"san":"e4","votes":1},{"san":"","votes":5},{"san":null,"votes":2}]}
            """;
        (HttpVoteApi api, _) = Build(HttpStatusCode.OK, body);

        VoteTally tally = await api.GetTallyAsync();

        Assert.That(tally.Counts.Select(o => o.San), Is.EqualTo(new[] { "e4" }));
    }

    // ── casting ─────────────────────────────────────────────────────────────

    [Test]
    public async Task A_vote_posts_the_token_and_the_move()
    {
        (HttpVoteApi api, Canned handler) = Build(HttpStatusCode.OK, """{"ok":true,"choice":"e4"}""");

        CastResult result = await api.CastAsync("token-abcdefgh", "e4");

        Assert.That(handler.RequestedUri?.AbsoluteUri, Is.EqualTo("https://api.test/vote/cast"));
        Assert.That(handler.RequestBody, Does.Contain("token-abcdefgh").And.Contain("e4"));
        Assert.That(result.Recorded, Is.True);
        Assert.That(result.Choice, Is.EqualTo("e4"));
    }

    [TestCase("no_round", CastStatus.NoRound)]
    [TestCase("round_closed", CastStatus.RoundClosed)]
    [TestCase("unknown_move", CastStatus.UnknownMove)]
    [TestCase("bad_token", CastStatus.BadToken)]
    [TestCase("rate_limited", CastStatus.RateLimited)]
    public async Task Each_refusal_reason_survives_the_round_trip(string reason, CastStatus expected)
    {
        (HttpVoteApi api, _) = Build(HttpStatusCode.Conflict, $$"""{"ok":false,"reason":"{{reason}}"}""");

        CastResult result = await api.CastAsync("token-abcdefgh", "e4");

        Assert.That(result.Status, Is.EqualTo(expected));
        Assert.That(result.Recorded, Is.False);
    }

    [Test]
    public async Task A_reason_this_client_does_not_know_is_not_guessed_at()
    {
        // A newer server could refuse for a reason this build has never heard of. Reporting it
        // as a generic failure is honest; picking the nearest-looking enum value would not be.
        (HttpVoteApi api, _) = Build(HttpStatusCode.BadRequest, """{"ok":false,"reason":"tempest"}""");

        Assert.That((await api.CastAsync("token-abcdefgh", "e4")).Status,
            Is.EqualTo(CastStatus.Unreachable));
    }

    [Test]
    public async Task A_vote_that_never_arrives_reports_as_much()
    {
        (HttpVoteApi api, _) = Build(HttpStatusCode.OK, "", new HttpRequestException("offline"));

        Assert.That((await api.CastAsync("token-abcdefgh", "e4")).Status,
            Is.EqualTo(CastStatus.Unreachable));
    }

    [Test]
    public async Task A_timeout_is_not_mistaken_for_a_recorded_vote()
    {
        (HttpVoteApi api, _) = Build(HttpStatusCode.OK, "", new TaskCanceledException("timed out"));

        Assert.That((await api.CastAsync("token-abcdefgh", "e4")).Recorded, Is.False);
    }
}
