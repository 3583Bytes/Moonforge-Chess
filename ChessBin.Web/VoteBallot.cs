namespace ChessBin.Web;

/// <summary>
/// The voting panel's state and wording, kept out of the Razor file so it can be tested
/// directly — the same split <see cref="PuzzleSession"/> and <see cref="ChessGameSession"/> use.
/// </summary>
public sealed class VoteBallot
{
    /// <summary>How long a "vote recorded" style confirmation stays on screen.</summary>
    public const int ConfirmationMs = 2_500;

    public VoteTally Tally { get; private set; } = VoteTally.None;

    /// <summary>The move this visitor has chosen, if they have voted this round.</summary>
    public string? MyChoice { get; private set; }

    /// <summary>What to tell the visitor right now. Never null; empty means say nothing.</summary>
    public string Message { get; private set; } = "";

    /// <summary>True when the message reports a problem rather than a confirmation.</summary>
    public bool MessageIsProblem { get; private set; }

    /// <summary>Set while a vote is in flight, so the buttons can be disabled.</summary>
    public bool IsSubmitting { get; private set; }

    /// <summary>Whether the panel should offer buttons at all.</summary>
    public bool CanVote => Tally.Open && Tally.Counts.Count > 0 && !IsSubmitting;

    /// <summary>Total votes cast across all options — the denominator for the bars.</summary>
    public int TotalVotes => Tally.Counts.Sum(option => option.Votes);

    /// <summary>
    /// The option with the most votes, or null when nothing has been cast or there is a tie.
    /// A tie deliberately has no leader: showing one of two equal moves as "winning" would be
    /// a lie about what happens at the deadline.
    /// </summary>
    public VoteOption? Leader
    {
        get
        {
            if (TotalVotes == 0) return null;

            var ordered = Tally.Counts.OrderByDescending(option => option.Votes).ToArray();
            return ordered.Length > 1 && ordered[0].Votes == ordered[1].Votes ? null : ordered[0];
        }
    }

    /// <summary>Share of the vote, 0–100, for a progress bar. Zero total means zero width.</summary>
    public int SharePercent(VoteOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        return TotalVotes == 0 ? 0 : (int)Math.Round(option.Votes * 100.0 / TotalVotes);
    }

    /// <summary>Adopts a freshly fetched tally. A new round clears the previous choice.</summary>
    public void Apply(VoteTally tally)
    {
        ArgumentNullException.ThrowIfNull(tally);

        if (tally.Round != Tally.Round)
        {
            MyChoice = null;
            Message = "";
            MessageIsProblem = false;
        }

        Tally = tally;
    }

    public void BeginSubmitting()
    {
        IsSubmitting = true;
        Message = "";
    }

    /// <summary>
    /// Records the outcome of a vote. On success the local count is nudged immediately so the
    /// bars move as soon as the button is pressed; the next fetch replaces it with the truth.
    /// </summary>
    public void Apply(CastResult result, string attempted)
    {
        ArgumentNullException.ThrowIfNull(result);
        IsSubmitting = false;

        if (result.Recorded)
        {
            string? previous = MyChoice;
            MyChoice = result.Choice ?? attempted;
            Message = previous is null
                ? $"Your vote for {MyChoice} is in."
                : $"Changed to {MyChoice}.";
            MessageIsProblem = false;
            ApplyOwnVoteLocally(previous, MyChoice);
            return;
        }

        MessageIsProblem = true;
        Message = result.Status switch
        {
            CastStatus.NoRound => "Voting isn't open yet. The next round starts when the referee posts it.",
            CastStatus.RoundClosed => "Voting just closed for this move. The count is being settled.",
            CastStatus.UnknownMove => "That move isn't one of the options any more — reloading the list.",
            CastStatus.BadToken => "This browser couldn't be identified, so the vote wasn't counted. Reloading may fix it.",
            CastStatus.RateLimited => "Too many votes from your network this round. This is to stop stuffing, not you.",
            _ => "Couldn't reach the vote server. Your vote wasn't counted — try again in a moment.",
        };
    }

    /// <summary>
    /// Moves this visitor's own vote between options in the local copy, so the panel responds
    /// at once. Deliberately only their own vote: inventing anyone else's would be a lie.
    /// </summary>
    private void ApplyOwnVoteLocally(string? from, string to)
    {
        if (from == to) return;

        var adjusted = Tally.Counts
            .Select(option => option.San switch
            {
                _ when option.San == to => option with { Votes = option.Votes + 1 },
                _ when option.San == from => option with { Votes = Math.Max(0, option.Votes - 1) },
                _ => option,
            })
            .ToArray();

        Tally = Tally with
        {
            Counts = adjusted,
            Voters = from is null ? Tally.Voters + 1 : Tally.Voters,
        };
    }

    /// <summary>How long is left to vote, phrased for a person rather than a clock.</summary>
    public string TimeRemaining(DateTimeOffset now)
    {
        if (Tally.Deadline is not DateTimeOffset deadline) return "";

        TimeSpan left = deadline - now;
        if (left <= TimeSpan.Zero) return "Voting has closed.";
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h {left.Minutes}m left to vote.";
        if (left.TotalMinutes >= 1) return $"{left.Minutes}m left to vote.";
        return "Less than a minute left to vote.";
    }
}
