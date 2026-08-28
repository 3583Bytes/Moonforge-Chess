# VoteReferee

Runs the community game: counts the votes cast on the site, plays the winning move, lets
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
   - a boot page at `wwwroot/vote/index.html`,
     so the route answers 200 to crawlers and link previews instead of falling through to
     `404.html`
   - a `<url>` entry in `wwwroot/sitemap.xml`

   Until then `/vote` still works for anyone who has the link — it just isn't advertised.

The daily schedule takes over from there.

## How votes are counted

Votes are cast **on chessbin.com**, not in the issue thread. A Cloudflare Worker collects
them; this referee reads them, decides, and writes the state file the site renders.

- **The ballot is every legal move** in the position, published by the referee when it opens
  a round. The community can play anything the rules allow, and the vote server — which knows
  nothing about chess — only has to check that a ballot names one of them.
- **One vote per browser**, and voting again replaces the earlier ballot, so anyone can change
  their mind until the deadline.
- **Ties go to whichever move comes first on the ballot.** The ballot is sorted, so this is
  checkable by anyone who wants to argue about it.
- **A round with no votes extends the deadline** rather than forfeiting. A stalled board looks
  worse than a slow one, and the referee says so in the thread.
- **Ballots that name something not on the list are discarded.** The server refuses these, but
  the referee is the authority and checks again — a bug upstream must not put an unplayable
  move on the board.

If the ballots cannot be read at all, the referee changes nothing and exits non-zero. "Nobody
voted" and "we could not ask" must lead to different actions, so they are never conflated.

The GitHub issue is still used, but only for discussion and for the referee's round summary.


## Locally

```bash
dotnet run --project tools/VoteReferee -- tally \
  --state ChessBin.Web/wwwroot/vote/state.json \
  --api https://chessbin-api.3583bytes.workers.dev \
  --api-secret "$REFEREE_SECRET" \
  --repo 3583Bytes/Moonforge-Chess --issue 7 --token "$GH_TOKEN"
```

`--api-secret` must match the Worker's `REFEREE_SECRET` (`wrangler secret put REFEREE_SECRET`),
and in CI comes from the repository secret of the same name. Against a local `wrangler dev`,
point `--api` at `http://127.0.0.1:8787` and use the value in `server/.dev.vars`.

`--now <iso8601>` overrides the clock, which is how the deadline behaviour is exercised
without waiting a day. Without `--token` the referee still tallies but skips commenting.

## What is tested where

The parts that decide the game — vote counting, tie-breaks, applying a round, detecting the
end — live in `ChessBin.Web/VoteChess.cs` and are covered by `ChessBin.Web.Tests`. This tool
is the I/O around them: GitHub, files, and arguments.
