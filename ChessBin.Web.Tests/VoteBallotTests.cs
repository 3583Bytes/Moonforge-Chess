using ChessBin.Web;

namespace ChessBin.Web.Tests;

/// <summary>
/// The voting panel's behaviour, with no page and no network. This is why the logic lives in
/// <see cref="VoteBallot"/> rather than in the Razor file.
/// </summary>
[TestFixture]
public class VoteBallotTests
{
    private static VoteTally Round(int round = 1, bool open = true, params (string San, int Votes)[] counts) =>
        new(round, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), open,
            counts.Sum(entry => entry.Votes),
            counts.Select(entry => new VoteOption(entry.San, entry.Votes)).ToArray());

    [Test]
    public void A_fresh_ballot_offers_nothing()
    {
        var ballot = new VoteBallot();

        Assert.That(ballot.CanVote, Is.False);
        Assert.That(ballot.Tally.HasRound, Is.False);
        Assert.That(ballot.Message, Is.Empty);
        Assert.That(ballot.MyChoice, Is.Null);
    }

    [Test]
    public void Voting_is_offered_once_a_round_has_options()
    {
        var ballot = new VoteBallot();
        ballot.Apply(Round(counts: [("e4", 0), ("d4", 0)]));

        Assert.That(ballot.CanVote, Is.True);
    }

    [Test]
    public void A_closed_round_offers_no_buttons()
    {
        var ballot = new VoteBallot();
        ballot.Apply(Round(open: false, counts: [("e4", 3)]));

        Assert.That(ballot.CanVote, Is.False);
        Assert.That(ballot.Tally.HasRound, Is.True);
    }

    [Test]
    public void Buttons_are_disabled_while_a_vote_is_in_flight()
    {
        var ballot = new VoteBallot();
        ballot.Apply(Round(counts: [("e4", 0)]));

        ballot.BeginSubmitting();

        Assert.That(ballot.IsSubmitting, Is.True);
        Assert.That(ballot.CanVote, Is.False);
    }

    // ── recording a vote ────────────────────────────────────────────────────

    [Test]
    public void A_recorded_vote_shows_immediately_without_waiting_for_the_server()
    {
        var ballot = new VoteBallot();
        ballot.Apply(Round(counts: [("e4", 4), ("d4", 2)]));

        ballot.Apply(new CastResult(CastStatus.Recorded, "e4"), "e4");

        Assert.That(ballot.MyChoice, Is.EqualTo("e4"));
        Assert.That(ballot.Message, Does.Contain("e4"));
        Assert.That(ballot.MessageIsProblem, Is.False);
        Assert.That(ballot.Tally.Counts.Single(o => o.San == "e4").Votes, Is.EqualTo(5));
        Assert.That(ballot.Tally.Voters, Is.EqualTo(7));
    }

    [Test]
    public void Changing_a_vote_moves_it_rather_than_adding_one()
    {
        var ballot = new VoteBallot();
        ballot.Apply(Round(counts: [("e4", 4), ("d4", 2)]));
        ballot.Apply(new CastResult(CastStatus.Recorded, "e4"), "e4");

        int votersAfterFirst = ballot.Tally.Voters;
        ballot.Apply(new CastResult(CastStatus.Recorded, "d4"), "d4");

        Assert.That(ballot.MyChoice, Is.EqualTo("d4"));
        Assert.That(ballot.Tally.Counts.Single(o => o.San == "e4").Votes, Is.EqualTo(4));
        Assert.That(ballot.Tally.Counts.Single(o => o.San == "d4").Votes, Is.EqualTo(3));
        Assert.That(ballot.Tally.Voters, Is.EqualTo(votersAfterFirst), "still one person");
        Assert.That(ballot.Message, Does.Contain("Changed"));
    }

    [Test]
    public void Voting_for_the_same_move_twice_changes_nothing()
    {
        var ballot = new VoteBallot();
        ballot.Apply(Round(counts: [("e4", 1)]));
        ballot.Apply(new CastResult(CastStatus.Recorded, "e4"), "e4");
        ballot.Apply(new CastResult(CastStatus.Recorded, "e4"), "e4");

        Assert.That(ballot.Tally.Counts.Single().Votes, Is.EqualTo(2), "one nudge, not two");
    }

    [TestCase(CastStatus.NoRound)]
    [TestCase(CastStatus.RoundClosed)]
    [TestCase(CastStatus.UnknownMove)]
    [TestCase(CastStatus.BadToken)]
    [TestCase(CastStatus.RateLimited)]
    [TestCase(CastStatus.Unreachable)]
    public void Every_failure_explains_itself_and_records_nothing(CastStatus status)
    {
        var ballot = new VoteBallot();
        ballot.Apply(Round(counts: [("e4", 3)]));
        ballot.BeginSubmitting();

        ballot.Apply(new CastResult(status, null), "e4");

        Assert.That(ballot.MyChoice, Is.Null);
        Assert.That(ballot.MessageIsProblem, Is.True);
        Assert.That(ballot.Message, Is.Not.Empty);
        Assert.That(ballot.IsSubmitting, Is.False, "the buttons must come back");
        Assert.That(ballot.Tally.Counts.Single().Votes, Is.EqualTo(3), "no optimistic nudge on failure");
    }

    [Test]
    public void The_rate_limit_message_does_not_blame_the_visitor()
    {
        // A shared office or household hits this without doing anything wrong.
        var ballot = new VoteBallot();
        ballot.Apply(Round(counts: [("e4", 0)]));

        ballot.Apply(new CastResult(CastStatus.RateLimited, null), "e4");

        Assert.That(ballot.Message, Does.Contain("not you"));
    }

    // ── a new round ─────────────────────────────────────────────────────────

    [Test]
    public void A_new_round_forgets_the_previous_choice()
    {
        var ballot = new VoteBallot();
        ballot.Apply(Round(1, counts: [("e4", 1)]));
        ballot.Apply(new CastResult(CastStatus.Recorded, "e4"), "e4");

        ballot.Apply(Round(2, counts: [("Nf3", 0), ("c4", 0)]));

        Assert.That(ballot.MyChoice, Is.Null);
        Assert.That(ballot.Message, Is.Empty);
    }

    [Test]
    public void A_refresh_of_the_same_round_keeps_the_choice()
    {
        var ballot = new VoteBallot();
        ballot.Apply(Round(1, counts: [("e4", 1)]));
        ballot.Apply(new CastResult(CastStatus.Recorded, "e4"), "e4");

        ballot.Apply(Round(1, counts: [("e4", 9)]));

        Assert.That(ballot.MyChoice, Is.EqualTo("e4"));
    }

    // ── the bars and the leader ─────────────────────────────────────────────

    [Test]
    public void Shares_are_percentages_of_the_votes_cast()
    {
        var ballot = new VoteBallot();
        ballot.Apply(Round(counts: [("e4", 3), ("d4", 1)]));

        Assert.That(ballot.SharePercent(ballot.Tally.Counts[0]), Is.EqualTo(75));
        Assert.That(ballot.SharePercent(ballot.Tally.Counts[1]), Is.EqualTo(25));
    }

    [Test]
    public void Nothing_cast_means_no_bars_rather_than_a_division_by_zero()
    {
        var ballot = new VoteBallot();
        ballot.Apply(Round(counts: [("e4", 0), ("d4", 0)]));

        Assert.That(ballot.TotalVotes, Is.Zero);
        Assert.That(ballot.SharePercent(ballot.Tally.Counts[0]), Is.Zero);
        Assert.That(ballot.Leader, Is.Null);
    }

    [Test]
    public void The_move_in_front_is_named()
    {
        var ballot = new VoteBallot();
        ballot.Apply(Round(counts: [("e4", 2), ("d4", 5)]));

        Assert.That(ballot.Leader?.San, Is.EqualTo("d4"));
    }

    [Test]
    public void A_tie_has_no_leader()
    {
        // Calling one of two equal moves "winning" would misrepresent what happens at the
        // deadline, where the referee's tie-break decides.
        var ballot = new VoteBallot();
        ballot.Apply(Round(counts: [("e4", 3), ("d4", 3)]));

        Assert.That(ballot.Leader, Is.Null);
    }

    // ── the deadline ────────────────────────────────────────────────────────

    [Test]
    public void Time_left_is_phrased_for_a_person()
    {
        // A fixed instant with no sub-millisecond part. Using UtcNow made this flaky:
        // ToUnixTimeMilliseconds truncates, so a deadline set 3h20m out came back 3h19m59.999s
        // and read as "3h 19m". Truncating a countdown downwards is right — never tell someone
        // they have more time than they do — so the test is what needed fixing.
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var ballot = new VoteBallot();

        static VoteTally Closing(DateTimeOffset at) =>
            new(1, at.ToUnixTimeMilliseconds(), true, 0, [new VoteOption("e4", 0)]);

        ballot.Apply(Closing(now.AddHours(3).AddMinutes(20)));
        Assert.That(ballot.TimeRemaining(now), Is.EqualTo("3h 20m left to vote."));

        ballot.Apply(Closing(now.AddMinutes(7)));
        Assert.That(ballot.TimeRemaining(now), Is.EqualTo("7m left to vote."));

        ballot.Apply(Closing(now.AddSeconds(30)));
        Assert.That(ballot.TimeRemaining(now), Is.EqualTo("Less than a minute left to vote."));

        ballot.Apply(Closing(now.AddMinutes(-5)));
        Assert.That(ballot.TimeRemaining(now), Is.EqualTo("Voting has closed."));
    }

    [Test]
    public void No_deadline_says_nothing_rather_than_guessing()
    {
        var ballot = new VoteBallot();
        ballot.Apply(VoteTally.None);

        Assert.That(ballot.TimeRemaining(DateTimeOffset.UtcNow), Is.Empty);
    }
}
