/**
 * One game between two people.
 *
 * The division of labour here is the whole design, and it is deliberate:
 *
 *  - **This object owns everything that does not require knowing chess**: which token holds
 *    which seat, whose turn it is, the clocks, resignation, draw offers, and abandonment.
 *  - **Moonforge, in each player's browser, owns everything that does**: whether a move is
 *    legal, whether it is mate, whether the position is drawn.
 *
 * That split is what keeps the real engine the only thing deciding legality — the same rule
 * `vote.ts` follows — without shipping a 26 MB .NET runtime into a Worker, which does not fit
 * inside the script size limit anyway.
 *
 * It holds up because **both** clients validate independently. A player who submits an illegal
 * move does get it recorded here, but their opponent's engine refuses it and posts a dispute,
 * which aborts the game. So nobody can force an illegal position onto an opponent's board, and
 * nobody can win a game they did not win — the worst a cheat achieves is an aborted game. They
 * cannot steal time either, because the clocks live here and not in the browser.
 *
 * The one thing this cannot do is adjudicate a disagreement: with no neutral engine it cannot
 * tell which of two clients is right. ChessBin has no accounts and no ratings, so there is
 * nothing to win by lying, and an abort is a proportionate answer. If that ever changes, the
 * interface below does not: a version that adjudicates replaces the body of `move` and nothing
 * on the client has to move.
 */

/** Bounds on a client-supplied token. Long enough to be unguessable, short enough to store. */
const MIN_TOKEN_LENGTH = 8;
const MAX_TOKEN_LENGTH = 64;

/** A game nobody has touched for this long is abandoned, even an untimed one. */
const IDLE_ABANDON_MS = 20 * 60_000;

/** Longest a claimed result can still be disputed after the game ends. */
const DISPUTE_WINDOW_MS = 60_000;

export type Seat = "white" | "black";
export type Status = "waiting" | "playing" | "finished";
export type Outcome = "none" | "white" | "black" | "draw" | "aborted";

/** Why the game ended. The chess reasons are claimed by a client, never decided here. */
export type Reason =
  | "none"
  | "checkmate"
  | "stalemate"
  | "repetition"
  | "fifty-move"
  | "insufficient"
  | "timeout"
  | "resignation"
  | "agreement"
  | "abandoned"
  | "disputed";

/** What a mover claims their move did to the game. Verified by the opponent, not here. */
export interface EndClaim {
  outcome: Outcome;
  reason: Reason;
}

export interface MatchClock {
  initialMs: number;
  incrementMs: number;
}

interface MatchState {
  clock: MatchClock;
  /** Set by the lobby when it pairs, never by a client — see the note on `seat`. */
  white: string | null;
  black: string | null;
  /** Which seats have actually opened the page. The game starts when both have. */
  whiteHere: boolean;
  blackHere: boolean;
  /**
   * True for a game created from a challenge link, where one seat is deliberately empty and
   * belongs to whoever opens the link first. False for a lobby pairing, where both seats were
   * assigned up front and a stranger must not be able to sit down.
   */
  openSeat: boolean;
  /** Move list in SAN, as reported by whoever played each one. */
  moves: string[];
  /** Same moves in coordinate form, which is what a client replays. */
  uci: string[];
  status: Status;
  outcome: Outcome;
  reason: Reason;
  whiteMs: number;
  blackMs: number;
  /** When the side to move started thinking. */
  turnStartedAt: number;
  drawOfferedBy: Seat | null;
  createdAt: number;
  lastEventAt: number;
  finishedAt: number | null;
}

export interface MoveOutcome {
  ok: boolean;
  reason?:
    | "not_your_turn"
    | "not_running"
    | "unknown_player"
    | "malformed"
    | "clock_expired"
    | "already_seated";
}

const UNSTARTED: MatchClock = { initialMs: 0, incrementMs: 0 };

function blank(now: number, clock: MatchClock): MatchState {
  return {
    clock,
    white: null,
    black: null,
    whiteHere: false,
    blackHere: false,
    openSeat: false,
    moves: [],
    uci: [],
    status: "waiting",
    outcome: "none",
    reason: "none",
    whiteMs: clock.initialMs,
    blackMs: clock.initialMs,
    turnStartedAt: now,
    drawOfferedBy: null,
    createdAt: now,
    lastEventAt: now,
    finishedAt: null,
  };
}

export class MatchDO implements DurableObject {
  private readonly storage: DurableObjectStorage;

  constructor(ctx: DurableObjectState) {
    this.storage = ctx.storage;
  }

  async fetch(request: Request): Promise<Response> {
    const { pathname, searchParams } = new URL(request.url);

    switch (`${request.method} ${pathname}`) {
      case "POST /seat":
        return this.seat(await body(request));
      case "POST /open":
        return this.open(await body(request));
      case "POST /join":
        return this.join(await body(request));
      case "GET /state":
        return this.state(searchParams.get("token"));
      case "POST /move":
        return this.move(await body(request));
      case "POST /resign":
        return this.resign(await body(request));
      case "POST /draw":
        return this.draw(await body(request));
      case "POST /decline":
        return this.decline(await body(request));
      case "POST /abort":
        return this.abort(await body(request));
      case "POST /dispute":
        return this.dispute(await body(request));
      default:
        return json({ error: "not_found" }, 404);
    }
  }

  /**
   * Assigns both seats. Called by the lobby the instant it pairs, never by a browser.
   *
   * Seats have to be decided in one place. They used to be handed out here on a first-come
   * basis while the lobby was separately telling each player a colour, and the two orders are
   * not the same one — so both players could be told they were White, and every move came back
   * as "not your turn". The lobby pairs, so the lobby decides.
   */
  private async seat(input: { white?: unknown; black?: unknown; clock?: unknown }): Promise<Response> {
    const white = readToken(input.white);
    const black = readToken(input.black);
    if (white === null || black === null || white === black) {
      return json<MoveOutcome>({ ok: false, reason: "malformed" }, 400);
    }

    const now = Date.now();
    const match = await this.load(now, readClock(input.clock));
    if (match.white !== null || match.black !== null) {
      return json<MoveOutcome>({ ok: false, reason: "already_seated" }, 409);
    }

    match.white = white;
    match.black = black;
    match.clock = readClock(input.clock);
    match.whiteMs = match.clock.initialMs;
    match.blackMs = match.clock.initialMs;
    match.lastEventAt = now;

    await this.save(match);
    return json({ ok: true });
  }

  /**
   * Opens a game from a challenge link: seats the creator and leaves the other seat for
   * whoever opens the link first. Unlike {@link seat}, which the lobby calls having already
   * decided both players, this one is a standing invitation.
   */
  private async open(input: { token?: unknown; clock?: unknown; seat?: unknown }): Promise<Response> {
    const token = readToken(input.token);
    if (token === null) return json<MoveOutcome>({ ok: false, reason: "malformed" }, 400);

    const now = Date.now();
    const match = await this.load(now, readClock(input.clock));
    if (match.white !== null || match.black !== null) {
      return json<MoveOutcome>({ ok: false, reason: "already_seated" }, 409);
    }

    // The creator picks a side; anything else means they did not care, so they get White.
    const creator: Seat = input.seat === "black" ? "black" : "white";
    if (creator === "white") match.white = token;
    else match.black = token;

    match.openSeat = true;
    match.clock = readClock(input.clock);
    match.whiteMs = match.clock.initialMs;
    match.blackMs = match.clock.initialMs;
    match.lastEventAt = now;

    await this.save(match);
    return json({ ok: true, seat: creator });
  }

  /**
   * Records that a seated player has arrived. The game starts once both have, so a player who
   * never opens the page does not burn their opponent's clock in the meantime. Calling it again
   * is how a reconnecting player gets back in, and is not an error.
   */
  private async join(input: { token?: unknown }): Promise<Response> {
    const token = readToken(input.token);
    if (token === null) return json<MoveOutcome>({ ok: false, reason: "malformed" }, 400);

    const now = Date.now();
    const match = await this.load(now, UNSTARTED);

    let seat = seatOf(match, token);

    // A challenge link's empty seat belongs to whoever opens it first. Only then — a lobby
    // pairing has both seats spoken for, and a stranger with the id must not sit down.
    if (seat === null && match.openSeat && match.status === "waiting") {
      if (match.white === null) {
        match.white = token;
        seat = "white";
      } else if (match.black === null) {
        match.black = token;
        seat = "black";
      }
    }

    if (seat === null) return json<MoveOutcome>({ ok: false, reason: "unknown_player" }, 403);

    if (seat === "white") match.whiteHere = true;
    else match.blackHere = true;

    if (match.status === "waiting" && match.whiteHere && match.blackHere) {
      match.status = "playing";
      match.turnStartedAt = now;
    }

    match.lastEventAt = now;
    await this.save(match);
    return json({ ok: true, seat, state: view(match, token, now) });
  }

  private async state(token: string | null): Promise<Response> {
    const now = Date.now();
    const match = await this.load(now, UNSTARTED);

    if (this.expire(match, now)) await this.save(match);
    return json({ ok: true, state: view(match, token, now) });
  }

  /**
   * Records a move on behalf of the player whose turn it is.
   *
   * The order of the checks matters. An expired clock ends the game even when the move itself
   * was fine, or a player could sit on a losing position, let the flag fall, and then move.
   */
  private async move(input: {
    token?: unknown;
    san?: unknown;
    uci?: unknown;
    ends?: unknown;
  }): Promise<Response> {
    const token = readToken(input.token);
    const san = readShortString(input.san);
    const uci = readShortString(input.uci);
    if (token === null || san === null || uci === null) {
      return json<MoveOutcome>({ ok: false, reason: "malformed" }, 400);
    }

    const now = Date.now();
    const match = await this.load(now, UNSTARTED);

    if (match.status !== "playing") return json<MoveOutcome>({ ok: false, reason: "not_running" }, 409);

    const seat = seatOf(match, token);
    if (seat === null) return json<MoveOutcome>({ ok: false, reason: "unknown_player" }, 403);

    if (this.expire(match, now)) {
      await this.save(match);
      return json<MoveOutcome>({ ok: false, reason: "clock_expired" }, 409);
    }
    if (seat !== toMove(match)) return json<MoveOutcome>({ ok: false, reason: "not_your_turn" }, 409);

    match.moves.push(san);
    match.uci.push(uci);
    charge(match, seat, now);
    match.turnStartedAt = now;
    match.lastEventAt = now;

    // A move answers a draw offer. Clearing it on either side's move is what stops an offer
    // made twenty moves ago from being accepted once the game has turned.
    match.drawOfferedBy = null;

    // The mover's claim about what their move did. Their opponent's engine checks it; if it
    // disagrees, /dispute aborts the game.
    const ends = readEndClaim(input.ends);
    if (ends !== null) finish(match, ends.outcome, ends.reason, now);

    await this.save(match);
    return json({ ok: true, state: view(match, token, now) });
  }

  private async resign(input: { token?: unknown }): Promise<Response> {
    const now = Date.now();
    const match = await this.load(now, UNSTARTED);
    const seat = seatOf(match, readToken(input.token));

    if (match.status !== "playing" || seat === null) {
      return json<MoveOutcome>({ ok: false, reason: seat === null ? "unknown_player" : "not_running" }, 409);
    }

    finish(match, seat === "white" ? "black" : "white", "resignation", now);
    await this.save(match);
    return json({ ok: true, state: view(match, seat === "white" ? match.white : match.black, now) });
  }

  /** Offers a draw, or accepts one already outstanding — two standing offers are an agreement. */
  private async draw(input: { token?: unknown }): Promise<Response> {
    const now = Date.now();
    const match = await this.load(now, UNSTARTED);
    const token = readToken(input.token);
    const seat = seatOf(match, token);

    if (match.status !== "playing" || seat === null) {
      return json<MoveOutcome>({ ok: false, reason: seat === null ? "unknown_player" : "not_running" }, 409);
    }

    if (match.drawOfferedBy !== null && match.drawOfferedBy !== seat) {
      finish(match, "draw", "agreement", now);
    } else {
      match.drawOfferedBy = seat;
      match.lastEventAt = now;
    }

    await this.save(match);
    return json({ ok: true, state: view(match, token, now) });
  }

  /** Turns down the opponent's offer. Declining your own is not a thing. */
  private async decline(input: { token?: unknown }): Promise<Response> {
    const now = Date.now();
    const match = await this.load(now, UNSTARTED);
    const token = readToken(input.token);
    const seat = seatOf(match, token);

    if (match.status !== "playing" || seat === null) {
      return json<MoveOutcome>({ ok: false, reason: seat === null ? "unknown_player" : "not_running" }, 409);
    }
    if (match.drawOfferedBy === null || match.drawOfferedBy === seat) {
      return json<MoveOutcome>({ ok: false, reason: "malformed" }, 409);
    }

    match.drawOfferedBy = null;
    match.lastEventAt = now;
    await this.save(match);
    return json({ ok: true, state: view(match, token, now) });
  }

  /** An opponent who never arrived, or a game abandoned before a move. A real game is resigned. */
  private async abort(input: { token?: unknown }): Promise<Response> {
    const now = Date.now();
    const match = await this.load(now, UNSTARTED);
    const token = readToken(input.token);
    const seat = seatOf(match, token);

    if (seat === null) return json<MoveOutcome>({ ok: false, reason: "unknown_player" }, 403);
    if (match.status === "finished") return json<MoveOutcome>({ ok: false, reason: "not_running" }, 409);
    if (match.moves.length > 0) return json<MoveOutcome>({ ok: false, reason: "not_running" }, 409);

    finish(match, "aborted", "abandoned", now);
    await this.save(match);
    return json({ ok: true, state: view(match, token, now) });
  }

  /**
   * "My engine says the move at this ply is not legal, or the result claimed for it is wrong."
   *
   * This is the safety valve that makes a chess-ignorant server safe: it costs a cheat the
   * game rather than winning it for them. It stays open briefly after the game ends, because
   * the move being disputed is often the one that claimed to end it.
   */
  private async dispute(input: { token?: unknown; ply?: unknown }): Promise<Response> {
    const now = Date.now();
    const match = await this.load(now, UNSTARTED);
    const token = readToken(input.token);
    const seat = seatOf(match, token);

    if (seat === null) return json<MoveOutcome>({ ok: false, reason: "unknown_player" }, 403);

    const settled = match.finishedAt !== null && now - match.finishedAt > DISPUTE_WINDOW_MS;
    if (match.status === "waiting" || settled) {
      return json<MoveOutcome>({ ok: false, reason: "not_running" }, 409);
    }

    // Only the move you did not make can be disputed; disputing your own is meaningless.
    const ply = typeof input.ply === "number" ? input.ply : match.moves.length;
    if (ply < 1 || ply > match.moves.length) return json<MoveOutcome>({ ok: false, reason: "malformed" }, 400);
    const mover: Seat = ply % 2 === 1 ? "white" : "black";
    if (mover === seat) return json<MoveOutcome>({ ok: false, reason: "malformed" }, 400);

    finish(match, "aborted", "disputed", now);
    await this.save(match);
    return json({ ok: true, state: view(match, token, now) });
  }

  /**
   * Ends the game if the side to move has run out of time, or if nobody has touched it for
   * long enough. Called on every read, because a player who simply closes the tab still has
   * to lose on the clock and the object is only awake when someone asks it something.
   */
  private expire(match: MatchState, now: number): boolean {
    if (match.status !== "playing") return false;

    if (match.clock.initialMs > 0 && remaining(match, toMove(match), now) <= 0) {
      // Whether a flag fall is a loss or a draw depends on whether the opponent could ever
      // mate, which is a chess question this object cannot answer. It records the timeout;
      // a client holding only a bare king claims the draw through /move with an "ends" claim.
      finish(match, toMove(match) === "white" ? "black" : "white", "timeout", now);
      return true;
    }

    if (now - match.lastEventAt >= IDLE_ABANDON_MS) {
      finish(match, "aborted", "abandoned", now);
      return true;
    }

    return false;
  }

  private async load(now: number, clock: MatchClock): Promise<MatchState> {
    const held = await this.storage.get<MatchState>("match");
    return held ?? blank(now, clock);
  }

  private async save(match: MatchState): Promise<void> {
    await this.storage.put("match", match);
  }
}

// ── helpers ───────────────────────────────────────────────────────────────────

function seatOf(match: MatchState, token: string | null): Seat | null {
  if (token === null) return null;
  if (token === match.white) return "white";
  if (token === match.black) return "black";
  return null;
}

function toMove(match: MatchState): Seat {
  return match.moves.length % 2 === 0 ? "white" : "black";
}

/**
 * Time left on a clock as of `now`. A function rather than a stored number because the answer
 * depends on the moment being asked about, and there is deliberately no ambient clock.
 */
function remaining(match: MatchState, seat: Seat, now: number): number {
  const held = seat === "white" ? match.whiteMs : match.blackMs;
  if (match.clock.initialMs <= 0 || match.status !== "playing" || seat !== toMove(match)) return held;
  return Math.max(0, held - elapsed(match, now));
}

/**
 * How long the current turn has lasted. Clamped at zero because clients, hosts and clocks
 * disagree: a timestamp from before the turn began must not hand out free time.
 */
function elapsed(match: MatchState, now: number): number {
  return Math.max(0, now - match.turnStartedAt);
}

function charge(match: MatchState, seat: Seat, now: number): void {
  if (match.clock.initialMs <= 0) return;

  const spent = elapsed(match, now);
  if (seat === "white") match.whiteMs = Math.max(0, match.whiteMs - spent) + match.clock.incrementMs;
  else match.blackMs = Math.max(0, match.blackMs - spent) + match.clock.incrementMs;
}

function finish(match: MatchState, outcome: Outcome, reason: Reason, now: number): void {
  match.status = "finished";
  match.outcome = outcome;
  match.reason = reason;
  match.finishedAt = now;
  match.lastEventAt = now;
  match.drawOfferedBy = null;
}

/**
 * What a player is allowed to see. Seat tokens never leave this object — they are the only
 * thing authorising a move, so publishing one would let anyone play the opponent's pieces.
 */
function view(match: MatchState, token: string | null, now: number) {
  const seat = seatOf(match, token);
  return {
    status: match.status,
    outcome: match.outcome,
    reason: match.reason,
    seat,
    moves: match.moves,
    uci: match.uci,
    toMove: toMove(match),
    clock: match.clock,
    whiteMs: remaining(match, "white", now),
    blackMs: remaining(match, "black", now),
    drawOfferedBy: match.drawOfferedBy,
    /** True only for the player being offered one, so the UI has nothing to work out. */
    drawOfferedToYou: seat !== null && match.drawOfferedBy !== null && match.drawOfferedBy !== seat,
    opponentJoined: match.whiteHere && match.blackHere,
    serverNow: now,
  };
}

function readToken(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const token = value.trim();
  return token.length >= MIN_TOKEN_LENGTH && token.length <= MAX_TOKEN_LENGTH ? token : null;
}

function readShortString(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const text = value.trim();
  return text.length > 0 && text.length <= 12 ? text : null;
}

export function readClock(value: unknown): MatchClock {
  const raw = value as { initialMs?: unknown; incrementMs?: unknown } | undefined;
  const initialMs = typeof raw?.initialMs === "number" && Number.isFinite(raw.initialMs) ? raw.initialMs : 0;
  const incrementMs = typeof raw?.incrementMs === "number" && Number.isFinite(raw.incrementMs) ? raw.incrementMs : 0;

  // Clamped rather than rejected: a client asking for a 40-hour clock is a bug, not an attack,
  // and a game that silently never times out is worse than one snapped to a sane bound.
  return {
    initialMs: Math.min(Math.max(0, Math.floor(initialMs)), 3 * 3_600_000),
    incrementMs: Math.min(Math.max(0, Math.floor(incrementMs)), 60_000),
  };
}

function readEndClaim(value: unknown): EndClaim | null {
  const raw = value as { outcome?: unknown; reason?: unknown } | undefined;
  if (raw === undefined || raw === null) return null;

  const outcomes: Outcome[] = ["white", "black", "draw"];
  const reasons: Reason[] = ["checkmate", "stalemate", "repetition", "fifty-move", "insufficient"];

  const outcome = outcomes.find((o) => o === raw.outcome);
  const reason = reasons.find((r) => r === raw.reason);
  return outcome !== undefined && reason !== undefined ? { outcome, reason } : null;
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
