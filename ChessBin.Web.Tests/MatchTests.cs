using ChessBin.Online;
using Match = ChessBin.Online.Match;

namespace ChessBin.Web.Tests;

/// <summary>
/// The rules a server has to get right for a game between two people who cannot be trusted.
/// Every clock assertion passes its own <c>now</c>, which is the whole reason the match core
/// takes one — none of this needs a host, a socket or a real second to elapse.
/// </summary>
[TestFixture]
public class MatchTests
{
    private const long T0 = 1_000_000;

    private static Match Started(MatchClock? clock = null, string? fen = null, long now = T0)
    {
        var match = new Match(clock ?? MatchClock.Untimed, now, fen);
        match.Join("white", now);
        match.Join("black", now);
        return match;
    }

    // ── seating ─────────────────────────────────────────────────────────────

    [Test]
    public void A_new_match_waits_for_a_second_player()
    {
        var match = new Match(MatchClock.Blitz, T0);

        Assert.That(match.Status, Is.EqualTo(MatchStatus.Waiting));
        Assert.That(match.Join("first", T0), Is.EqualTo(Seat.White));
        Assert.That(match.Status, Is.EqualTo(MatchStatus.Waiting));
        Assert.That(match.Join("second", T0), Is.EqualTo(Seat.Black));
        Assert.That(match.Status, Is.EqualTo(MatchStatus.Playing));
    }

    [Test]
    public void A_third_player_cannot_join()
    {
        var match = Started();

        Assert.That(match.Join("gatecrasher", T0), Is.Null);
        Assert.That(match.SeatOf("gatecrasher"), Is.Null);
    }

    [Test]
    public void The_same_token_cannot_take_both_seats()
    {
        var match = new Match(MatchClock.Blitz, T0);
        match.Join("me", T0);

        Assert.That(match.Join("me", T0), Is.Null);
        Assert.That(match.Status, Is.EqualTo(MatchStatus.Waiting));
    }

    [Test]
    public void Moves_are_refused_before_both_seats_are_filled()
    {
        var match = new Match(MatchClock.Blitz, T0);
        match.Join("white", T0);

        Assert.That(match.Submit("white", "e2e4", T0).Rejection, Is.EqualTo(MoveRejection.GameNotRunning));
    }

    // ── whose move it is ────────────────────────────────────────────────────

    [Test]
    public void Only_the_player_on_the_move_can_move()
    {
        var match = Started();

        Assert.That(match.Submit("black", "e7e5", T0).Rejection, Is.EqualTo(MoveRejection.NotYourTurn));
        Assert.That(match.Submit("white", "e2e4", T0).Accepted, Is.True);
        Assert.That(match.Submit("white", "d2d4", T0).Rejection, Is.EqualTo(MoveRejection.NotYourTurn));
    }

    [Test]
    public void A_stranger_cannot_move_for_either_player()
    {
        var match = Started();

        Assert.That(match.Submit("someone-else", "e2e4", T0).Rejection, Is.EqualTo(MoveRejection.UnknownPlayer));
        Assert.That(match.Submit(null, "e2e4", T0).Rejection, Is.EqualTo(MoveRejection.UnknownPlayer));
        Assert.That(match.Moves, Is.Empty);
    }

    // ── legality is decided here, not by the client ──────────────────────────

    [TestCase("e2e5", TestName = "pawn three squares")]
    [TestCase("e1g1", TestName = "castling through occupied squares")]
    [TestCase("a1a5", TestName = "rook through its own pawn")]
    [TestCase("d1h5", TestName = "queen through its own pawn")]
    [TestCase("e4e5", TestName = "moving from an empty square")]
    public void Illegal_moves_are_refused(string move)
    {
        var match = Started();

        Assert.That(match.Submit("white", move, T0).Rejection, Is.EqualTo(MoveRejection.IllegalMove));
        Assert.That(match.Moves, Is.Empty);
        Assert.That(match.ToMove, Is.EqualTo(Seat.White));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void Empty_moves_are_refused(string? move)
    {
        var match = Started();

        Assert.That(match.Submit("white", move, T0).Rejection, Is.EqualTo(MoveRejection.Malformed));
    }

    [TestCase("resign")]
    [TestCase("e2e4e5")]
    [TestCase("../../etc/passwd")]
    [TestCase("Qxz9")]
    public void Nonsense_is_refused_rather_than_guessed_at(string move)
    {
        var match = Started();

        Assert.That(match.Submit("white", move, T0).Accepted, Is.False);
        Assert.That(match.Moves, Is.Empty);
    }

    [Test]
    public void Both_coordinates_and_notation_are_accepted()
    {
        var match = Started();

        Assert.That(match.Submit("white", "e2e4", T0).Accepted, Is.True);
        Assert.That(match.Submit("black", "Nf6", T0).Accepted, Is.True);
        Assert.That(match.Moves, Is.EqualTo(new[] { "e4", "Nf6" }));
    }

    [Test]
    public void An_accepted_move_is_recorded_in_notation()
    {
        var match = Started();
        MoveResult result = match.Submit("white", "g1f3", T0);

        Assert.That(result.San, Is.EqualTo("Nf3"));
        Assert.That(match.Moves, Is.EqualTo(new[] { "Nf3" }));
    }

    [Test]
    public void A_promotion_takes_the_piece_the_client_asked_for()
    {
        var match = Started(fen: "8/4P3/8/8/8/8/8/K6k w - - 0 1");

        Assert.That(match.Submit("white", "e7e8r", T0).Accepted, Is.True);
        Assert.That(match.Fen, Does.StartWith("4R3/"));
    }

    // ── the clock ───────────────────────────────────────────────────────────

    [Test]
    public void Time_spent_thinking_comes_off_the_movers_clock()
    {
        var match = Started(new MatchClock(60_000, 0));

        match.Submit("white", "e2e4", T0 + 5_000);

        Assert.That(match.MsRemaining(Seat.White, T0), Is.EqualTo(55_000));
        Assert.That(match.MsRemaining(Seat.Black, T0), Is.EqualTo(60_000));
    }

    [Test]
    public void The_increment_is_added_after_the_move()
    {
        var match = Started(new MatchClock(60_000, 2_000));

        match.Submit("white", "e2e4", T0 + 1_000);

        Assert.That(match.MsRemaining(Seat.White, T0), Is.EqualTo(61_000));
    }

    [Test]
    public void The_clock_of_the_player_on_the_move_counts_down_between_moves()
    {
        var match = Started(new MatchClock(60_000, 0));

        // Nothing submitted; simply asking later has to show less time.
        Assert.That(match.MsRemaining(Seat.White, T0), Is.EqualTo(60_000));
        Assert.That(match.MsRemaining(Seat.White, T0 + 4_000), Is.EqualTo(56_000));
        Assert.That(match.MsRemaining(Seat.Black, T0 + 4_000), Is.EqualTo(60_000));
    }

    [Test]
    public void Running_out_of_time_loses_the_game()
    {
        var match = Started(new MatchClock(60_000, 0));

        Assert.That(match.ExpireIfFlagged(T0 + 60_001), Is.True);
        Assert.That(match.Status, Is.EqualTo(MatchStatus.Finished));
        Assert.That(match.Outcome, Is.EqualTo(MatchOutcome.BlackWins));
        Assert.That(match.Reason, Is.EqualTo(MatchReason.Timeout));
    }

    [Test]
    public void A_move_arriving_after_the_flag_falls_does_not_save_the_game()
    {
        var match = Started(new MatchClock(60_000, 0));

        MoveResult result = match.Submit("white", "e2e4", T0 + 90_000);

        Assert.That(result.Rejection, Is.EqualTo(MoveRejection.ClockExpired));
        Assert.That(match.Outcome, Is.EqualTo(MatchOutcome.BlackWins));
        Assert.That(match.Moves, Is.Empty);
    }

    [Test]
    public void Timing_out_against_a_lone_king_is_a_draw()
    {
        // Black cannot mate with a bare king, so White's flag falling is not a loss.
        var match = Started(new MatchClock(60_000, 0), "7k/8/8/8/8/8/8/K7 w - - 0 1");

        Assert.That(match.ExpireIfFlagged(T0 + 60_001), Is.True);
        Assert.That(match.Outcome, Is.EqualTo(MatchOutcome.Draw));
        Assert.That(match.Reason, Is.EqualTo(MatchReason.InsufficientMaterial));
    }

    [Test]
    public void Timing_out_while_holding_a_queen_is_still_a_draw_against_a_bare_king()
    {
        // The question a flag asks is whether the OPPONENT could ever have mated —
        // not whether the position was drawish. White has a queen and loses nothing by it.
        var match = Started(new MatchClock(60_000, 0), "7k/8/8/8/8/8/8/K5Q1 w - - 0 1");

        Assert.That(match.ExpireIfFlagged(T0 + 60_001), Is.True);
        Assert.That(match.Outcome, Is.EqualTo(MatchOutcome.Draw));
    }

    [TestCase("7k/8/8/8/8/8/8/K7 w - - 0 1", false, TestName = "bare king cannot mate")]
    [TestCase("6nk/8/8/8/8/8/8/K7 w - - 0 1", false, TestName = "king and knight cannot mate")]
    [TestCase("6bk/8/8/8/8/8/8/K7 w - - 0 1", false, TestName = "king and bishop cannot mate")]
    [TestCase("5bnk/8/8/8/8/8/8/K7 w - - 0 1", true, TestName = "two minors can mate")]
    [TestCase("6rk/8/8/8/8/8/8/K7 w - - 0 1", true, TestName = "a rook can mate")]
    [TestCase("7k/6p1/8/8/8/8/8/K7 w - - 0 1", true, TestName = "a pawn can promote and mate")]
    public void Mating_material_is_counted_for_one_side_only(string fen, bool expected)
    {
        Assert.That(Match.HasMatingMaterial(fen, Seat.Black), Is.EqualTo(expected));
        Assert.That(Match.HasMatingMaterial(fen, Seat.White), Is.False, "White is a bare king in every fixture");
    }

    [Test]
    public void Timing_out_against_a_rook_loses()
    {
        var match = Started(new MatchClock(60_000, 0), "6rk/8/8/8/8/8/8/K7 w - - 0 1");

        Assert.That(match.ExpireIfFlagged(T0 + 60_001), Is.True);
        Assert.That(match.Outcome, Is.EqualTo(MatchOutcome.BlackWins));
        Assert.That(match.Reason, Is.EqualTo(MatchReason.Timeout));
    }

    [Test]
    public void An_untimed_match_never_flags()
    {
        var match = Started(MatchClock.Untimed);

        Assert.That(match.ExpireIfFlagged(T0 + 100_000_000), Is.False);
        Assert.That(match.Submit("white", "e2e4", T0 + 100_000_000).Accepted, Is.True);
    }

    [Test]
    public void A_clock_never_reads_below_zero()
    {
        var match = Started(new MatchClock(60_000, 0));

        Assert.That(match.MsRemaining(Seat.White, T0 + 500_000), Is.Zero);
    }

    [Test]
    public void A_clock_reading_ignores_a_now_from_the_past()
    {
        // Clients and hosts disagree about the time; a stale reading must not hand out time.
        var match = Started(new MatchClock(60_000, 0));

        Assert.That(match.MsRemaining(Seat.White, T0 - 30_000), Is.EqualTo(60_000));
    }

    [Test]
    public void A_move_stamped_in_the_past_does_not_refund_time()
    {
        var match = Started(new MatchClock(60_000, 0));
        match.Submit("white", "e2e4", T0 + 10_000);

        match.Submit("black", "e7e5", T0 + 5_000);

        Assert.That(match.MsRemaining(Seat.White, T0), Is.EqualTo(50_000));
        Assert.That(match.MsRemaining(Seat.Black, T0), Is.EqualTo(60_000));
    }

    [Test]
    public void The_clock_starts_when_the_second_player_arrives_not_when_the_match_is_made()
    {
        var match = new Match(new MatchClock(60_000, 0), T0);
        match.Join("white", T0);
        match.Join("black", T0 + 300_000);        // five minutes later

        Assert.That(match.MsRemaining(Seat.White, T0 + 300_000), Is.EqualTo(60_000));
        Assert.That(match.Status, Is.EqualTo(MatchStatus.Playing));
    }

    // ── how games end ───────────────────────────────────────────────────────

    [Test]
    public void Checkmate_ends_the_game()
    {
        var match = Started();
        foreach ((string token, string move) in new[]
                 { ("white", "f2f3"), ("black", "e7e5"), ("white", "g2g4"), ("black", "d8h4") })
        {
            Assert.That(match.Submit(token, move, T0).Accepted, Is.True, move);
        }

        Assert.That(match.Status, Is.EqualTo(MatchStatus.Finished));
        Assert.That(match.Outcome, Is.EqualTo(MatchOutcome.BlackWins));
        Assert.That(match.Reason, Is.EqualTo(MatchReason.Checkmate));
    }

    [Test]
    public void Stalemate_is_a_draw()
    {
        var match = Started(fen: "7k/8/6K1/8/8/8/8/5Q2 w - - 0 1");

        Assert.That(match.Submit("white", "f1f7", T0).Accepted, Is.True);
        Assert.That(match.Outcome, Is.EqualTo(MatchOutcome.Draw));
        Assert.That(match.Reason, Is.EqualTo(MatchReason.Stalemate));
    }

    [Test]
    public void Trading_into_bare_kings_is_a_draw()
    {
        var match = Started(fen: "7k/8/7b/8/8/8/8/K1B5 w - - 0 1");

        Assert.That(match.Submit("white", "c1h6", T0).Accepted, Is.True);
        Assert.That(match.Status, Is.EqualTo(MatchStatus.Finished));
        Assert.That(match.Outcome, Is.EqualTo(MatchOutcome.Draw));
    }

    [Test]
    public void The_fifty_move_rule_draws()
    {
        // Ninety-nine half-moves already made without a capture or a pawn move.
        var match = Started(fen: "7k/8/8/8/8/8/R7/K7 w - - 99 60");

        Assert.That(match.Submit("white", "a2b2", T0).Accepted, Is.True);
        Assert.That(match.Status, Is.EqualTo(MatchStatus.Finished));
        Assert.That(match.Outcome, Is.EqualTo(MatchOutcome.Draw));
        Assert.That(match.Reason, Is.EqualTo(MatchReason.FiftyMove));
    }

    [Test]
    public void Shuffling_the_same_position_three_times_draws()
    {
        // The engine counts positions it has recorded, and the starting position is not
        // one of them, so this trips on the ninth half-move rather than the eighth.
        var match = Started();
        string[] shuffle =
        [
            "g1f3", "g8f6", "f3g1", "f6g8",
            "g1f3", "g8f6", "f3g1", "f6g8",
            "g1f3", "g8f6", "f3g1", "f6g8",
        ];

        foreach ((string move, int i) in shuffle.Select((m, i) => (m, i)))
        {
            if (match.Status != MatchStatus.Playing) break;
            string token = i % 2 == 0 ? "white" : "black";
            Assert.That(match.Submit(token, move, T0).Accepted, Is.True, move);
        }

        Assert.That(match.Outcome, Is.EqualTo(MatchOutcome.Draw));
        Assert.That(match.Reason, Is.EqualTo(MatchReason.Repetition));
    }

    [Test]
    public void Resigning_hands_the_win_to_the_opponent()
    {
        var match = Started();

        Assert.That(match.Resign("black", T0), Is.True);
        Assert.That(match.Outcome, Is.EqualTo(MatchOutcome.WhiteWins));
        Assert.That(match.Reason, Is.EqualTo(MatchReason.Resignation));
    }

    [Test]
    public void A_player_may_resign_when_it_is_not_their_turn()
    {
        var match = Started();
        match.Submit("white", "e2e4", T0);

        Assert.That(match.Resign("white", T0), Is.True);
        Assert.That(match.Outcome, Is.EqualTo(MatchOutcome.BlackWins));
    }

    [Test]
    public void A_stranger_cannot_resign_someone_elses_game()
    {
        var match = Started();

        Assert.That(match.Resign("someone-else", T0), Is.False);
        Assert.That(match.Status, Is.EqualTo(MatchStatus.Playing));
    }

    // ── draw offers ─────────────────────────────────────────────────────────

    [Test]
    public void Two_offers_are_an_agreed_draw()
    {
        var match = Started();

        Assert.That(match.OfferDraw("white", T0), Is.True);
        Assert.That(match.DrawOfferedBy, Is.EqualTo(Seat.White));
        Assert.That(match.Status, Is.EqualTo(MatchStatus.Playing));

        Assert.That(match.OfferDraw("black", T0 + 1_000), Is.True);
        Assert.That(match.Outcome, Is.EqualTo(MatchOutcome.Draw));
        Assert.That(match.Reason, Is.EqualTo(MatchReason.Agreement));
    }

    [Test]
    public void Offering_twice_does_not_draw_the_game_by_yourself()
    {
        var match = Started();

        match.OfferDraw("white", T0);
        match.OfferDraw("white", T0 + 1_000);

        Assert.That(match.Status, Is.EqualTo(MatchStatus.Playing));
    }

    [Test]
    public void A_declined_offer_cannot_be_accepted_later()
    {
        var match = Started();
        match.OfferDraw("white", T0);

        Assert.That(match.DeclineDraw("black", T0), Is.True);
        Assert.That(match.DrawOfferedBy, Is.Null);

        // Black changing their mind now is a fresh offer, not an acceptance.
        Assert.That(match.OfferDraw("black", T0), Is.True);
        Assert.That(match.Status, Is.EqualTo(MatchStatus.Playing));
    }

    [Test]
    public void Making_a_move_answers_an_outstanding_offer()
    {
        var match = Started();
        match.OfferDraw("white", T0);

        match.Submit("white", "e2e4", T0);
        Assert.That(match.DrawOfferedBy, Is.Null);

        // Otherwise a player could bank an offer and cash it in once the game turned.
        Assert.That(match.OfferDraw("black", T0), Is.True);
        Assert.That(match.Status, Is.EqualTo(MatchStatus.Playing));
    }

    [Test]
    public void A_stranger_cannot_offer_or_decline_a_draw()
    {
        var match = Started();
        match.OfferDraw("white", T0);

        Assert.That(match.OfferDraw("someone-else", T0), Is.False);
        Assert.That(match.DeclineDraw("someone-else", T0), Is.False);
        Assert.That(match.DrawOfferedBy, Is.EqualTo(Seat.White));
    }

    [Test]
    public void You_cannot_decline_your_own_offer()
    {
        var match = Started();
        match.OfferDraw("white", T0);

        Assert.That(match.DeclineDraw("white", T0), Is.False);
        Assert.That(match.DrawOfferedBy, Is.EqualTo(Seat.White));
    }

    // ── what a host needs to reap matches ───────────────────────────────────

    [Test]
    public void The_end_of_the_game_is_timestamped()
    {
        var match = Started();
        Assert.That(match.FinishedAtMs, Is.Null);

        match.Resign("white", T0 + 90_000);

        Assert.That(match.FinishedAtMs, Is.EqualTo(T0 + 90_000));
    }

    [Test]
    public void Activity_is_timestamped_so_a_silent_game_can_be_cleared_away()
    {
        var match = new Match(MatchClock.Untimed, T0);
        Assert.That(match.LastEventAtMs, Is.EqualTo(T0));

        match.Join("white", T0 + 1_000);
        Assert.That(match.LastEventAtMs, Is.EqualTo(T0 + 1_000));

        match.Join("black", T0 + 2_000);
        match.Submit("white", "e2e4", T0 + 3_000);
        Assert.That(match.LastEventAtMs, Is.EqualTo(T0 + 3_000));

        // A refused move is not activity — otherwise spamming illegal moves would keep
        // an abandoned game alive for ever.
        match.Submit("black", "e2e4", T0 + 9_000);
        Assert.That(match.LastEventAtMs, Is.EqualTo(T0 + 3_000));
    }

    [Test]
    public void A_match_nobody_joined_can_be_abandoned()
    {
        var match = new Match(MatchClock.Blitz, T0);
        match.Join("white", T0);

        Assert.That(match.Abort(T0 + 60_000), Is.True);
        Assert.That(match.Outcome, Is.EqualTo(MatchOutcome.Aborted));
    }

    [Test]
    public void A_game_already_underway_cannot_be_aborted()
    {
        var match = Started();
        match.Submit("white", "e2e4", T0);

        Assert.That(match.Abort(T0), Is.False);
        Assert.That(match.Status, Is.EqualTo(MatchStatus.Playing));
    }

    [Test]
    public void Nothing_is_accepted_after_the_game_ends()
    {
        var match = Started();
        match.Resign("white", T0);

        Assert.That(match.Submit("black", "e7e5", T0).Rejection, Is.EqualTo(MoveRejection.GameNotRunning));
        Assert.That(match.Resign("black", T0), Is.False);
        Assert.That(match.ExpireIfFlagged(T0 + 10_000_000), Is.False);
        Assert.That(match.Outcome, Is.EqualTo(MatchOutcome.BlackWins));
    }

    [Test]
    public void The_result_of_the_first_end_condition_stands()
    {
        var match = Started(new MatchClock(60_000, 0));
        match.Resign("white", T0);

        // A flag that falls afterwards must not overwrite a decided game.
        Assert.That(match.ExpireIfFlagged(T0 + 60_001), Is.False);
        Assert.That(match.Reason, Is.EqualTo(MatchReason.Resignation));
    }
}
