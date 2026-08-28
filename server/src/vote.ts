/**
 * Where community votes live while a round is open.
 *
 * One Durable Object holds the whole ballot box. That is deliberate: counting votes needs
 * strong consistency, and Workers KV is eventually consistent — two visitors voting at once
 * could each read a stale count and one of them would be lost. A Durable Object is
 * single-threaded and transactional, so a vote either lands or it does not.
 *
 * What is NOT here: any judgement about chess. The Worker cannot tell a legal move from an
 * illegal one, and should not try. It accepts a ballot only if it names one of the candidate
 * moves the referee published, so every stored vote is a choice among moves the real engine
 * already vouched for. The engine stays the only thing that decides legality.
 */

/** Bounds on a client-supplied token. Long enough to be unguessable, short enough to store. */
const MIN_TOKEN_LENGTH = 8;
const MAX_TOKEN_LENGTH = 64;

/**
 * Casts allowed from one IP per round. A token lives in the visitor's browser and can be
 * reset, so this is a speed bump rather than a lock — enough to stop a bored person with a
 * loop, not enough to stop anyone determined. Households and offices share addresses, so it
 * cannot be 1.
 */
const MAX_CASTS_PER_IP = 8;

interface Round {
  /** Increments each time the referee opens a new round. 0 means no round has ever opened. */
  round: number;
  /** Epoch milliseconds after which votes are refused, or null when no round is open. */
  closesAt: number | null;
  /** The moves a visitor may choose between, in the order the referee published them. */
  candidates: string[];
  /** token → the move they chose. One entry per voter; voting again replaces it. */
  ballots: Record<string, string>;
  /** IP → how many casts it has made this round. */
  ipCasts: Record<string, number>;
}

const CLOSED: Round = {
  round: 0,
  closesAt: null,
  candidates: [],
  ballots: {},
  ipCasts: {},
};

export interface CastOutcome {
  ok: boolean;
  /** Machine-readable why-not, absent on success. */
  reason?: "no_round" | "round_closed" | "unknown_move" | "bad_token" | "rate_limited";
  /** The voter's current choice after this call, so the page can confirm what it recorded. */
  choice?: string;
}

export class VoteDO implements DurableObject {
  private readonly storage: DurableObjectStorage;

  constructor(ctx: DurableObjectState) {
    this.storage = ctx.storage;
  }

  async fetch(request: Request): Promise<Response> {
    const { pathname } = new URL(request.url);

    switch (`${request.method} ${pathname}`) {
      case "POST /cast":
        return this.cast(await request.json());
      case "GET /tally":
        return json(await this.tally());
      case "GET /ballots":
        return json(await this.load());
      case "POST /open":
        return this.open(await request.json());
      default:
        return json({ error: "not_found" }, 404);
    }
  }

  /** Records one visitor's choice. Voting again replaces the earlier ballot. */
  private async cast(body: { token?: unknown; san?: unknown; ip?: unknown }): Promise<Response> {
    const token = typeof body.token === "string" ? body.token.trim() : "";
    const san = typeof body.san === "string" ? body.san.trim() : "";
    const ip = typeof body.ip === "string" ? body.ip : "";

    if (token.length < MIN_TOKEN_LENGTH || token.length > MAX_TOKEN_LENGTH) {
      return json<CastOutcome>({ ok: false, reason: "bad_token" }, 400);
    }

    const round = await this.load();

    if (round.round === 0 || round.closesAt === null) {
      return json<CastOutcome>({ ok: false, reason: "no_round" }, 409);
    }
    if (Date.now() >= round.closesAt) {
      return json<CastOutcome>({ ok: false, reason: "round_closed" }, 409);
    }
    if (!round.candidates.includes(san)) {
      return json<CastOutcome>({ ok: false, reason: "unknown_move" }, 400);
    }

    // Changing your mind is free; only a new voter costs this IP one of its casts.
    const isNewVoter = round.ballots[token] === undefined;
    if (isNewVoter && ip !== "") {
      const used = round.ipCasts[ip] ?? 0;
      if (used >= MAX_CASTS_PER_IP) {
        return json<CastOutcome>({ ok: false, reason: "rate_limited" }, 429);
      }
      round.ipCasts[ip] = used + 1;
    }

    round.ballots[token] = san;
    await this.storage.put("round", round);

    return json<CastOutcome>({ ok: true, choice: san });
  }

  /**
   * The public view. Aggregates only — tokens identify a browser, so they never leave here,
   * and neither does the IP map.
   */
  private async tally() {
    const round = await this.load();
    const counts = new Map<string, number>(round.candidates.map((san) => [san, 0]));

    for (const san of Object.values(round.ballots)) {
      counts.set(san, (counts.get(san) ?? 0) + 1);
    }

    return {
      round: round.round,
      closesAt: round.closesAt,
      open: round.closesAt !== null && Date.now() < round.closesAt,
      voters: Object.keys(round.ballots).length,
      counts: [...counts].map(([san, votes]) => ({ san, votes })),
    };
  }

  /** Opens the next round and clears the previous one's ballots. Referee only. */
  private async open(body: { round?: unknown; candidates?: unknown; closesAt?: unknown }): Promise<Response> {
    const candidates = Array.isArray(body.candidates)
      ? body.candidates.filter((value): value is string => typeof value === "string" && value.trim() !== "")
          .map((value) => value.trim())
      : [];

    if (candidates.length === 0) return json({ error: "no_candidates" }, 400);
    if (typeof body.closesAt !== "number" || !Number.isFinite(body.closesAt)) {
      return json({ error: "bad_deadline" }, 400);
    }

    const previous = await this.load();
    const round: Round = {
      round: typeof body.round === "number" ? body.round : previous.round + 1,
      closesAt: body.closesAt,
      candidates: [...new Set(candidates)],
      ballots: {},
      ipCasts: {},
    };

    await this.storage.put("round", round);
    return json({ ok: true, round: round.round, candidates: round.candidates });
  }

  private async load(): Promise<Round> {
    const held = await this.storage.get<Round>("round");
    return held ?? { ...CLOSED };
  }
}

function json<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json; charset=utf-8" },
  });
}
