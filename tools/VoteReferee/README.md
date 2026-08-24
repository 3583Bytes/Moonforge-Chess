# VoteReferee

Runs the community game: counts the votes on a GitHub issue, plays the winning move, lets
Moonforge answer, and writes the state file the site reads. There is no server — the
committed `state.json` *is* the deployment.

Not in `ChessCore.sln`, so it can never affect the Pages or release workflows.

## Holding the launch

`state.json` ships as `"status": "idle"`, and a `tally` run against an idle state does
nothing and exits 0. The schedule in `.github/workflows/vote-chess.yml` is therefore safe to
enable long before a game exists, and `/vote` shows "no game running" until one does.

## Starting a game

1. Open an issue to hold the voting thread and note its number.
2. Run the **Vote chess** workflow with `command: start`, the issue number, and the colour
   the community plays.
3. Make it visible, which is deliberately three separate edits so none of it happens by accident:
   - a nav link in `ChessBin.Web/Layout/SiteHeader.razor`
   - a boot page at `wwwroot/vote/index.html` (copy `review/index.html` and change the head),
     so the route answers 200 to crawlers and link previews instead of falling through to
     `404.html`
   - a `<url>` entry in `wwwroot/sitemap.xml`

   Until then `/vote` still works for anyone who has the link — it just isn't advertised.

The daily schedule takes over from there.

## How votes are counted

- **One vote per person**, and the most recent comment wins, so anyone can change their mind.
- A comment votes for **the first legal move it mentions**, so "I say Nf6" works and nobody
  has to learn a command syntax. Illegal or ambiguous text is ignored rather than guessed at.
- **Ties go to whichever move was proposed first.** A workflow that has to be reproducible
  cannot roll dice.
- **A round with no votes extends the deadline** rather than forfeiting. A stalled board looks
  bad; a forfeited game looks worse.

Comments before the round opened, and the bot's own posts, are never counted.

## Locally

```bash
dotnet run --project tools/VoteReferee -- tally \
  --state ChessBin.Web/wwwroot/vote/state.json \
  --repo 3583Bytes/Moonforge-Chess --issue 7 --token "$GH_TOKEN"
```

`--now <iso8601>` overrides the clock, which is how the deadline behaviour is exercised
without waiting a day. Without `--token` the referee still tallies but skips commenting.

## What is tested where

The parts that decide the game — vote counting, tie-breaks, applying a round, detecting the
end — live in `ChessBin.Web/VoteChess.cs` and are covered by `ChessBin.Web.Tests`. This tool
is the I/O around them: GitHub, files, and arguments.
