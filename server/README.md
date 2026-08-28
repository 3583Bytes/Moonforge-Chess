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
