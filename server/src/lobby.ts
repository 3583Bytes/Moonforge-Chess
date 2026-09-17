import type { MatchClock, Seat } from "./match";
import { readClock } from "./match";

/**
 * Pairs strangers who want the same time control.
 *
 * One Durable Object for the whole lobby, for the same reason the ballot box is one: pairing
 * needs strong consistency. Two people arriving at the same instant must not both be told they
 * are waiting, and must not both be paired with the same third person. A Durable Object is
 * single-threaded, so a seek either pairs or queues, never half of each.
 *
 * It creates matches but does not run them — each game is its own {@link MatchDO}, keyed by the
 * id handed out here. This object never learns a move.
 */

/**
 * A seek nobody matched in this long is dropped; the seeker's tab is probably closed.
 * Overridable so the tests can watch it actually fire — five minutes is not a thing a test
 * can wait for, and this is exactly the behaviour that was silently broken before.
 */
const DEFAULT_SEEK_TIMEOUT_MS = 5 * 60_000;

/** How long a pairing stays readable, so a seeker who was queued can come back and find it. */
const PAIRING_TTL_MS = 10 * 60_000;

/** Ceiling on the queue, so a loop cannot grow this object's storage without limit. */
const MAX_WAITING = 200;

const MIN_TOKEN_LENGTH = 8;
const MAX_TOKEN_LENGTH = 64;

interface Waiting {
  token: string;
  clock: MatchClock;
  since: number;
}

interface Pairing {
  matchId: string;
  seat: Seat;
  clock: MatchClock;
  at: number;
}

interface LobbyState {
  waiting: Waiting[];
  /** token → the game it was put into. How a player who was queued learns they have a game. */
  pairings: Record<string, Pairing>;
}

const EMPTY: LobbyState = { waiting: [], pairings: {} };

export class LobbyDO implements DurableObject {
  private readonly storage: DurableObjectStorage;
  private readonly matches: DurableObjectNamespace;
  private readonly seekTimeoutMs: number;

  constructor(ctx: DurableObjectState, env: { MATCH: DurableObjectNamespace; SEEK_TIMEOUT_MS?: string }) {
    this.storage = ctx.storage;
    this.matches = env.MATCH;

    const configured = Number(env.SEEK_TIMEOUT_MS);
    this.seekTimeoutMs = Number.isFinite(configured) && configured > 0 ? configured : DEFAULT_SEEK_TIMEOUT_MS;
  }

  async fetch(request: Request): Promise<Response> {
    const { pathname } = new URL(request.url);

    switch (`${request.method} ${pathname}`) {
      case "POST /seek":
        return this.seek(await body(request));
      case "POST /cancel":
        return this.cancel(await body(request));
      default:
        return json({ error: "not_found" }, 404);
    }
  }

  /**
   * Asks for a game, and is safe to call repeatedly — that is how a queued player finds out
   * they have been paired. Calling it again with a different time control replaces the earlier
   * request rather than leaving a stale one behind, and nobody is ever paired with themselves.
   */
  private async seek(input: { token?: unknown; clock?: unknown }): Promise<Response> {
    const token = readToken(input.token);
    if (token === null) return json({ ok: false, reason: "bad_token" }, 400);

    const now = Date.now();
    const state = await this.load();
    this.sweep(state, now);

    // Already in a game: say so rather than queueing them a second time.
    const existing = state.pairings[token];
    if (existing !== undefined) {
      await this.save(state);
      return json({ ok: true, paired: true, matchId: existing.matchId, seat: existing.seat, clock: existing.clock });
    }

    const clock = readClock(input.clock);

    // A client polls this every second or so to find out whether it has been paired yet, so
    // "asking again" is the normal case, not a new request. Carrying the original timestamp
    // over is what makes the queue mean anything: re-stamping it each poll reset every waiter
    // to "just arrived", which silently broke both the longest-waiter order and the timeout
    // below — an open tab could never age out, because it was newly arrived a second ago.
    // A different time control *is* a new request, and starts the clock again.
    const previous = state.waiting.find((w) => w.token === token);
    const since = previous !== undefined && sameClock(previous.clock, clock) ? previous.since : now;

    state.waiting = state.waiting.filter((w) => w.token !== token);

    // Longest wait first, which is the only fair order and the one players notice.
    const index = state.waiting.findIndex((w) => sameClock(w.clock, clock));
    if (index < 0) {
      if (state.waiting.length >= MAX_WAITING) {
        await this.save(state);
        return json({ ok: false, reason: "lobby_full" }, 503);
      }

      state.waiting.push({ token, clock, since });
      await this.save(state);
      return json({ ok: true, queued: true, waiting: state.waiting.filter((w) => sameClock(w.clock, clock)).length });
    }

    const opponent = state.waiting[index]!;
    state.waiting.splice(index, 1);

    // The player who waited gets White. Arbitrary, but fixed and explainable, which beats a
    // coin flip nobody can reproduce when a game is disputed.
    const matchId = crypto.randomUUID();

    // Tell the game who is who before either player can ask it. Doing this here rather than
    // letting the match seat whoever knocks first is what keeps the colour a consequence of
    // waiting, and keeps one authority on the answer.
    await this.matches.get(this.matches.idFromName(matchId)).fetch("https://match/seat", {
      method: "POST",
      body: JSON.stringify({ white: opponent.token, black: token, clock }),
    });

    state.pairings[opponent.token] = { matchId, seat: "white", clock, at: now };
    state.pairings[token] = { matchId, seat: "black", clock, at: now };

    await this.save(state);
    return json({ ok: true, paired: true, matchId, seat: "black", clock });
  }

  /** Withdraws a request that has not been paired yet. */
  private async cancel(input: { token?: unknown }): Promise<Response> {
    const token = readToken(input.token);
    if (token === null) return json({ ok: false, reason: "bad_token" }, 400);

    const state = await this.load();
    const before = state.waiting.length;
    state.waiting = state.waiting.filter((w) => w.token !== token);

    await this.save(state);
    return json({ ok: true, cancelled: state.waiting.length < before });
  }

  /**
   * Housekeeping every call has to do, because nothing here watches a clock: seeks from people
   * who closed the tab go stale, and pairings stop being worth keeping once both players have
   * had every chance to read them.
   */
  private sweep(state: LobbyState, now: number): void {
    state.waiting = state.waiting.filter((w) => now - w.since < this.seekTimeoutMs);

    for (const [token, pairing] of Object.entries(state.pairings)) {
      if (now - pairing.at >= PAIRING_TTL_MS) delete state.pairings[token];
    }
  }

  private async load(): Promise<LobbyState> {
    const held = await this.storage.get<LobbyState>("lobby");
    return held ?? { ...EMPTY, waiting: [], pairings: {} };
  }

  private async save(state: LobbyState): Promise<void> {
    await this.storage.put("lobby", state);
  }
}

function sameClock(a: MatchClock, b: MatchClock): boolean {
  return a.initialMs === b.initialMs && a.incrementMs === b.incrementMs;
}

function readToken(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const token = value.trim();
  return token.length >= MIN_TOKEN_LENGTH && token.length <= MAX_TOKEN_LENGTH ? token : null;
}

async function body(request: Request): Promise<Record<string, unknown>> {
  try {
    return (await request.json()) as Record<string, unknown>;
  } catch {
    return {};
  }
}

function json<T>(value: T, status = 200): Response {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json; charset=utf-8" },
  });
}
