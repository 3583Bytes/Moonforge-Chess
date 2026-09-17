# chessbin-api

The Cloudflare Worker behind [chessbin.com](https://chessbin.com).

The site itself is a Blazor WebAssembly app on GitHub Pages and stays there — static, free,
and fast. This Worker exists only for what a static host cannot do: hold state that changes,
and be the single authority when two visitors disagree.

## Node version

**Node 20+ is required** (Wrangler will not run on older versions).

This repo's shell loads `nvm`, which pins Node 14 for interactive shells, so `node --version`
in a terminal will likely say `v14.21.3` while Homebrew's Node 26 sits unused. Either is fine
as long as the one on `PATH` is recent:

```bash
export PATH=/opt/homebrew/bin:$PATH   # use Homebrew's node
# or
nvm install --lts && nvm use --lts
```

`.npmrc` sets `engine-strict=true`, so a wrong version fails immediately with a clear message
rather than crashing somewhere inside a dependency.

## Commands

```bash
npm install
npm test            # runs inside workerd, the same runtime as production
npm run typecheck
npm run dev         # local server on :8787, no Cloudflare account needed
npm run deploy      # needs `npx wrangler login` first
npm run tail        # live logs from the deployed Worker
```

Pushing a change under `server/` deploys it automatically via `.github/workflows/chessbin-api.yml`,
which typechecks, tests and then verifies the live Worker. It needs two repository secrets:
`CLOUDFLARE_API_TOKEN` (Edit Cloudflare Workers template) and `CLOUDFLARE_ACCOUNT_ID`. The manual
command above stays for one-off deploys. `REFEREE_SECRET` is a *wrangler* secret, set once with
`wrangler secret put` — deploys do not touch it.

Copy `.dev.vars.example` to `.dev.vars` for local development. It enables loopback origins,
which production does not allow.

## Origins

`src/index.ts` refuses any browser origin not listed in `ALLOWED_ORIGINS` (set in
`wrangler.jsonc`). Two things worth being clear about:

- CORS is enforced by the *browser*. It stops a page on another site from using this API with
  a visitor's identity; it does not stop anyone with `curl`. Endpoints that change state need
  their own protection — a token, a rate limit, a shared secret.
- A request with **no** `Origin` header is allowed through, because that is how a
  server-to-server caller reaches us: the vote-chess referee running in GitHub Actions. Those
  routes carry a secret of their own.

## Layout

```
src/index.ts     Worker entry: origin rules, routing
test/            Specs, run in workerd via @cloudflare/vitest-pool-workers
wrangler.jsonc   Deployment config and production vars
```

## Online play

`/play/*` hosts games between two people. Two Durable Objects: `LobbyDO` (one, for the whole
site) pairs strangers on time control and assigns the colours; `MatchDO` (one per game) holds
the seats, the move list and the clocks.

**The server knows no chess, on purpose.** It owns seats, turn order and time; whether a move
is legal, whether it is mate, whether the position is drawn — none of that is decided here.
`ChessBin.Web/OnlineSession.cs` runs a real `ChessBin.Online.Match` in each player's browser
and replays every move the server reports, including the opponent's, before the board moves.

That split exists because the alternative is a 26 MB .NET runtime inside a Worker whose script
limit is 3 MB compressed, and because reimplementing the rules in TypeScript would mean two
engines that have to agree forever.

It is safe because **both** clients check:

- An illegal move is relayed but refused by the opponent's engine, which posts `/dispute`. The
  game aborts. It never reaches their board.
- A legal move carrying a false result claim ("that was mate") is caught the same way — the
  claim is replayed, not believed.
- Time cannot be stolen: the clocks are here, not in the browser.

What it cannot do is say **which** of two disagreeing clients is right. With no accounts and
no ratings there is nothing to win by lying, so an abort is proportionate. If that changes,
only `move` needs to grow an arbiter — the client's interface does not move.

### Routes

| Route | Who | What |
|---|---|---|
| `POST /play/seek` | player | Pair me, or queue me. Safe to repeat; that is how a queued player learns they have a game. |
| `POST /play/open` | player | Mint a game with one seat left open, for a challenge link. No lobby involved. |
| `POST /play/cancel` | player | Withdraw a seek. |
| `POST /play/<id>/join` | player | "I have arrived." The game starts once both have. |
| `GET /play/<id>/state` | player | The board, the clocks, whose turn. Token in the query string. |
| `POST /play/<id>/move` | player | Record a move, optionally with a claimed result. |
| `POST /play/<id>/{resign,draw,decline,abort}` | player | The rest of what a player can do. |
| `POST /play/<id>/dispute` | player | "My engine refuses the move at this ply." Aborts the game. |

`<id>` must match the UUID shape the lobby issues, so a caller cannot name an arbitrary
Durable Object and make us create it.

### An empty lobby

A new site has nobody in the queue, which is the normal case, not an edge case. Two answers,
and deliberately not a third:

- **A challenge link** (`POST /play/open`). One seat is left open and belongs to whoever opens
  the link first; `/join` claims it. This is the one that actually solves the cold start,
  because the first people to play online on a small site know each other.
- **Offering the engine.** After 25 seconds of searching the page says it is quiet and offers
  Moonforge at the same time control. Offered, never substituted.

The third answer — quietly pairing someone with a bot and letting them think it was a person —
is not implemented and should not be. It buys nothing (Moonforge is one nav item away) and
costs the site's credibility the first time someone works it out.

### The seek timeout is configurable

`SEEK_TIMEOUT_MS` (default five minutes) drops seeks from tabs that went away. It is a binding
so the tests can watch it fire; production leaves it unset. Note what it is guarding: a client
polls `/play/seek` about once a second, and an earlier version re-stamped the waiter as newly
arrived on every one of those polls, so nothing ever aged out. `since` is now carried over
unless the time control changes.

### Seats are the lobby's to decide

`MatchDO` never hands out a colour. The lobby calls `POST /seat` on the game the moment it
pairs, and `/join` only records arrival. They were split once — the lobby telling each player
a colour while the game seated whoever knocked first — and since those two orders differ, both
players could be told it was not their turn, forever. One authority, decided at pairing.
