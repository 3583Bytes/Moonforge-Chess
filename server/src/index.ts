/**
 * chessbin-api — the Cloudflare Worker behind chessbin.com.
 *
 * The site itself is static, on GitHub Pages, and stays there. This Worker exists only for
 * the things a static host cannot do: hold state that changes, and be the single authority
 * when two visitors disagree.
 *
 * Phase 0 is deliberately just /health plus origin handling. Getting the deploy path and the
 * origin rules settled once, with nothing else in the way, is the point.
 */

export { VoteDO } from "./vote";

/**
 * Just the origin settings. Split out so {@link isAllowedOrigin} declares that it reads
 * nothing else — the whole Env would let it quietly grow a dependency on storage or secrets.
 */
export interface OriginConfig {
  /** Comma-separated browser origins allowed to call this API. Set in wrangler.jsonc. */
  ALLOWED_ORIGINS?: string;
  /** "1" enables loopback origins for local development. Never set in production. */
  ALLOW_LOCALHOST?: string;
}

export interface Env extends OriginConfig {
  VOTE: DurableObjectNamespace;
  /** Shared secret for the vote-chess referee. Set with `wrangler secret put REFEREE_SECRET`. */
  REFEREE_SECRET?: string;
}

const LOOPBACK = /^https?:\/\/(?:localhost|127\.0\.0\.1)(?::\d+)?$/;

/**
 * Whether a browser origin may call this API.
 *
 * Worth being precise about what this buys, because it is easy to mistake for security:
 * CORS is enforced by the *browser*, so this stops a page on another site from using the API
 * with a visitor's identity — it does not stop anyone with curl. Endpoints that change state
 * need their own protection (a token, a rate limit, a shared secret), not this.
 *
 * A request with no Origin header at all is allowed through: that is how a server-to-server
 * caller reaches us, such as the vote-chess referee running in GitHub Actions. Those routes
 * carry a secret of their own.
 */
export function isAllowedOrigin(origin: string | null, env: OriginConfig): boolean {
  if (origin === null) return true;

  const configured = (env.ALLOWED_ORIGINS ?? "")
    .split(",")
    .map((value) => value.trim())
    .filter((value) => value.length > 0);

  if (configured.includes(origin)) return true;

  return env.ALLOW_LOCALHOST === "1" && LOOPBACK.test(origin);
}

/**
 * Whether this request carries the referee's secret.
 *
 * Compared at constant time so a wrong guess reveals nothing about how much of it was right.
 * An unset secret refuses everyone rather than admitting everyone — a misconfigured deploy
 * should lock the referee out, not open the ballot box.
 */
function isReferee(request: Request, env: Env): boolean {
  const expected = env.REFEREE_SECRET ?? "";
  if (expected === "") return false;

  const offered = (request.headers.get("Authorization") ?? "").replace(/^Bearer /, "");
  if (offered.length !== expected.length) return false;

  let difference = 0;
  for (let i = 0; i < offered.length; i++) {
    difference |= offered.charCodeAt(i) ^ expected.charCodeAt(i);
  }
  return difference === 0;
}

/** Passes a Durable Object's response back out, adding the CORS headers the browser needs. */
async function relay(response: Response, origin: string | null): Promise<Response> {
  return new Response(await response.text(), {
    status: response.status,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
      ...corsHeaders(origin),
    },
  });
}

/** CORS headers for an allowed origin. Omitted entirely when there was no Origin to answer. */
function corsHeaders(origin: string | null): Record<string, string> {
  if (origin === null) return {};

  return {
    "Access-Control-Allow-Origin": origin,
    "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
    "Access-Control-Allow-Headers": "Content-Type",
    "Access-Control-Max-Age": "86400",
    // The allowed origin varies by requester, so caches must not serve one origin's
    // response to another.
    Vary: "Origin",
  };
}

function json(body: unknown, origin: string | null, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
      ...corsHeaders(origin),
    },
  });
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const origin = request.headers.get("Origin");

    if (!isAllowedOrigin(origin, env)) {
      // No CORS headers on the way out: the browser would reject the response regardless,
      // and echoing back a rejected origin only muddies what happened.
      return new Response(JSON.stringify({ error: "origin_not_allowed" }), {
        status: 403,
        headers: { "Content-Type": "application/json; charset=utf-8" },
      });
    }

    if (request.method === "OPTIONS") {
      return new Response(null, { status: 204, headers: corsHeaders(origin) });
    }

    const { pathname } = new URL(request.url);

    if (pathname === "/health" && request.method === "GET") {
      return json({ ok: true, service: "chessbin-api" }, origin);
    }

    // One ballot box for the whole community game, so every vote lands in the same object.
    const votes = () => env.VOTE.get(env.VOTE.idFromName("vote"));

    if (pathname === "/vote/cast" && request.method === "POST") {
      let body: unknown;
      try {
        body = await request.json();
      } catch {
        return json({ ok: false, reason: "bad_request" }, origin, 400);
      }

      // The address comes from Cloudflare's own header, never from the body — a client that
      // could name its own IP could sidestep the rate limit by inventing a new one each time.
      const payload = {
        ...(body as Record<string, unknown>),
        ip: request.headers.get("CF-Connecting-IP") ?? "",
      };

      return relay(await votes().fetch("https://vote/cast", {
        method: "POST",
        body: JSON.stringify(payload),
      }), origin);
    }

    if (pathname === "/vote/tally" && request.method === "GET") {
      return relay(await votes().fetch("https://vote/tally"), origin);
    }

    // Referee routes. A shared secret rather than an origin check, because the caller is
    // GitHub Actions and sends no Origin at all.
    if (pathname === "/vote/round" || pathname === "/vote/next") {
      if (!isReferee(request, env)) return json({ error: "forbidden" }, origin, 403);

      if (pathname === "/vote/round" && request.method === "GET") {
        return relay(await votes().fetch("https://vote/ballots"), origin);
      }

      if (pathname === "/vote/next" && request.method === "POST") {
        return relay(await votes().fetch("https://vote/open", {
          method: "POST",
          body: await request.text(),
        }), origin);
      }
    }

    return json({ error: "not_found" }, origin, 404);
  },
} satisfies ExportedHandler<Env>;
