import { beforeEach, describe, expect, it } from "vitest";
import { SELF, reset } from "cloudflare:test";

const SITE = "https://chessbin.com";

const ALICE = "alice-token-0001";
const BOB = "bob-token-000001";
const CAROL = "carol-token-0001";

const BLITZ = { initialMs: 180_000, incrementMs: 2_000 };
const RAPID = { initialMs: 600_000, incrementMs: 0 };
const UNTIMED = { initialMs: 0, incrementMs: 0 };

// The lobby and every match are Durable Objects, so without this each test inherits the
// previous test's queue and games.
beforeEach(reset);

async function post(path: string, payload: unknown) {
  return SELF.fetch(`https://api.test${path}`, {
    method: "POST",
    headers: { Origin: SITE, "Content-Type": "application/json" },
    body: JSON.stringify(payload),
  });
}

interface SeekResult {
  ok: boolean;
  paired?: boolean;
  queued?: boolean;
  matchId?: string;
  seat?: "white" | "black";
  waiting?: number;
  reason?: string;
}

const seek = async (token: string, clock = BLITZ): Promise<SeekResult> =>
  (await post("/play/seek", { token, clock })).json();

interface MatchView {
  status: "waiting" | "playing" | "finished";
  outcome: "none" | "white" | "black" | "draw" | "aborted";
  reason: string;
  seat: "white" | "black" | null;
  moves: string[];
  uci: string[];
  toMove: "white" | "black";
  whiteMs: number;
  blackMs: number;
  drawOfferedBy: "white" | "black" | null;
  drawOfferedToYou: boolean;
  opponentJoined: boolean;
}

interface Envelope {
  ok: boolean;
  reason?: string;
  seat?: "white" | "black";
  state?: MatchView;
}

const join = async (id: string, token: string): Promise<Envelope> =>
  (await post(`/play/${id}/join`, { token })).json();

const state = async (id: string, token: string): Promise<Envelope> =>
  (await SELF.fetch(`https://api.test/play/${id}/state?token=${token}`, { headers: { Origin: SITE } })).json();

const move = async (id: string, token: string, san: string, uci: string, ends?: unknown): Promise<Envelope> =>
  (await post(`/play/${id}/move`, { token, san, uci, ends })).json();

const act = async (id: string, action: string, payload: unknown): Promise<Envelope> =>
  (await post(`/play/${id}/${action}`, payload)).json();

/** Pairs two players and seats them both, which is the start of every game test. */
async function startedGame(clock = BLITZ) {
  await seek(ALICE, clock);
  const paired = await seek(BOB, clock);
  const id = paired.matchId!;

  // Both seats already belong to them — the lobby assigned those when it paired. Joining only
  // says "I am here", and the game starts once both have.
  await join(id, ALICE);
  await join(id, BOB);
  return id;
}

describe("the lobby", () => {
  it("queues the first player to ask", async () => {
    const result = await seek(ALICE);

    expect(result.queued).toBe(true);
    expect(result.paired).toBeUndefined();
    expect(result.waiting).toBe(1);
  });

  it("pairs the second player with the first", async () => {
    await seek(ALICE);
    const bob = await seek(BOB);

    expect(bob.paired).toBe(true);
    expect(bob.matchId).toMatch(/^[0-9a-f-]{36}$/);
    expect(bob.seat).toBe("black");
  });

  it("tells the waiting player about the game when they ask again", async () => {
    await seek(ALICE);
    const bob = await seek(BOB);

    // Alice was queued and never got an answer; asking again is how she finds out.
    const alice = await seek(ALICE);
    expect(alice.paired).toBe(true);
    expect(alice.matchId).toBe(bob.matchId);
    expect(alice.seat).toBe("white");
  });

  it("gives White to whoever waited, not to whoever arrived", async () => {
    await seek(ALICE);
    const bob = await seek(BOB);
    const alice = await seek(ALICE);

    expect(alice.seat).toBe("white");
    expect(bob.seat).toBe("black");
  });

  it("does not pair players who asked for different time controls", async () => {
    await seek(ALICE, BLITZ);
    const bob = await seek(BOB, RAPID);

    expect(bob.queued).toBe(true);
    expect(bob.paired).toBeUndefined();
  });

  it("pairs the longest waiter first", async () => {
    await seek(ALICE);
    await seek(BOB);          // pairs with Alice
    await seek(CAROL);        // nobody left, so queues

    const carol = await seek(CAROL);
    expect(carol.queued).toBe(true);
  });

  it("never pairs a player with themselves", async () => {
    await seek(ALICE);
    const again = await seek(ALICE);

    expect(again.queued).toBe(true);
    expect(again.paired).toBeUndefined();
  });

  it("replaces an earlier seek rather than leaving it behind", async () => {
    await seek(ALICE, BLITZ);
    await seek(ALICE, RAPID);

    // The blitz seek is gone, so a blitz seeker finds nobody waiting.
    const bob = await seek(BOB, BLITZ);
    expect(bob.queued).toBe(true);
  });

  it("withdraws a seek on cancel", async () => {
    await seek(ALICE);
    const cancelled = await (await post("/play/cancel", { token: ALICE })).json<{ cancelled: boolean }>();
    expect(cancelled.cancelled).toBe(true);

    const bob = await seek(BOB);
    expect(bob.queued).toBe(true);
  });

  it("refuses a token too short to be unguessable", async () => {
    const result = await seek("short");
    expect(result.ok).toBe(false);
    expect(result.reason).toBe("bad_token");
  });
});

describe("seating", () => {
  it("starts the game once both players have arrived", async () => {
    await seek(ALICE);
    const id = (await seek(BOB)).matchId!;

    const white = await join(id, ALICE);
    expect(white.seat).toBe("white");
    expect(white.state!.status).toBe("waiting");

    const black = await join(id, BOB);
    expect(black.seat).toBe("black");
    expect(black.state!.status).toBe("playing");
    expect(black.state!.opponentJoined).toBe(true);
  });

  it("gives each player the colour the lobby promised them", async () => {
    // The bug this replaces: the lobby told Alice she was White while the game handed White to
    // whoever knocked first. Both players were then told it was not their turn, forever.
    await seek(ALICE);
    const paired = await seek(BOB);
    const alice = await seek(ALICE);

    // Bob arrives first even though Alice waited longer.
    const bobSeat = await join(paired.matchId!, BOB);
    const aliceSeat = await join(paired.matchId!, ALICE);

    expect(alice.seat).toBe("white");
    expect(paired.seat).toBe("black");
    expect(aliceSeat.seat).toBe("white");
    expect(bobSeat.seat).toBe("black");
  });

  it("lets a player rejoin, which is how a reconnect works", async () => {
    const id = await startedGame();

    const again = await join(id, ALICE);
    expect(again.ok).toBe(true);
    expect(again.seat).toBe("white");
  });

  it("refuses anyone the lobby did not seat", async () => {
    const id = await startedGame();

    const gatecrasher = await join(id, CAROL);
    expect(gatecrasher.ok).toBe(false);
    expect(gatecrasher.reason).toBe("unknown_player");
  });

  it("never publishes the opponent's token", async () => {
    const id = await startedGame();
    const seen = await state(id, ALICE);

    // A seat token is the only thing authorising a move, so publishing one would let anyone
    // play the opponent's pieces.
    expect(JSON.stringify(seen)).not.toContain(BOB);
  });
});

describe("playing a move", () => {
  it("records the move and passes the turn", async () => {
    const id = await startedGame();

    const played = await move(id, ALICE, "e4", "e2e4");
    expect(played.ok).toBe(true);
    expect(played.state!.moves).toEqual(["e4"]);
    expect(played.state!.uci).toEqual(["e2e4"]);
    expect(played.state!.toMove).toBe("black");
  });

  it("refuses a move from the player whose turn it is not", async () => {
    const id = await startedGame();

    const jumped = await move(id, BOB, "e5", "e7e5");
    expect(jumped.ok).toBe(false);
    expect(jumped.reason).toBe("not_your_turn");
  });

  it("refuses a move from someone who is not in the game", async () => {
    const id = await startedGame();

    const stranger = await move(id, CAROL, "e4", "e2e4");
    expect(stranger.ok).toBe(false);
    expect(stranger.reason).toBe("unknown_player");
  });

  it("charges the mover's clock and adds the increment", async () => {
    const id = await startedGame();
    await new Promise((resolve) => setTimeout(resolve, 60));

    const played = await move(id, ALICE, "e4", "e2e4");

    // Some time was spent, and the 2s increment was added on top.
    expect(played.state!.whiteMs).toBeGreaterThan(BLITZ.initialMs);
    expect(played.state!.whiteMs).toBeLessThanOrEqual(BLITZ.initialMs + BLITZ.incrementMs);
    expect(played.state!.blackMs).toBe(BLITZ.initialMs);
  });

  it("leaves both clocks alone in an untimed game", async () => {
    const id = await startedGame(UNTIMED);
    await new Promise((resolve) => setTimeout(resolve, 40));

    const played = await move(id, ALICE, "e4", "e2e4");
    expect(played.state!.whiteMs).toBe(0);
    expect(played.state!.blackMs).toBe(0);
  });

  it("accepts the mover's claim that the move ended the game", async () => {
    const id = await startedGame();

    // The claim is the mover's engine talking. The opponent's engine checks it, and disputes
    // it if it disagrees — this object cannot tell mate from a bluff.
    const mated = await move(id, ALICE, "Qxf7#", "d1f7", { outcome: "white", reason: "checkmate" });

    expect(mated.state!.status).toBe("finished");
    expect(mated.state!.outcome).toBe("white");
    expect(mated.state!.reason).toBe("checkmate");
  });

  it("ignores a result claim it does not recognise", async () => {
    const id = await startedGame();

    const played = await move(id, ALICE, "e4", "e2e4", { outcome: "white", reason: "vibes" });
    expect(played.state!.status).toBe("playing");
    expect(played.state!.outcome).toBe("none");
  });

  it("refuses a move once the game is over", async () => {
    const id = await startedGame();
    await act(id, "resign", { token: ALICE });

    const late = await move(id, BOB, "e5", "e7e5");
    expect(late.ok).toBe(false);
    expect(late.reason).toBe("not_running");
  });
});

describe("ending a game", () => {
  it("gives the win to the opponent on resignation", async () => {
    const id = await startedGame();

    const resigned = await act(id, "resign", { token: ALICE });
    expect(resigned.state!.status).toBe("finished");
    expect(resigned.state!.outcome).toBe("black");
    expect(resigned.state!.reason).toBe("resignation");
  });

  it("agrees a draw when both sides offer", async () => {
    const id = await startedGame();

    const offered = await act(id, "draw", { token: ALICE });
    expect(offered.state!.status).toBe("playing");
    expect(offered.state!.drawOfferedBy).toBe("white");

    const accepted = await act(id, "draw", { token: BOB });
    expect(accepted.state!.status).toBe("finished");
    expect(accepted.state!.outcome).toBe("draw");
    expect(accepted.state!.reason).toBe("agreement");
  });

  it("tells only the player being offered a draw that there is one", async () => {
    const id = await startedGame();
    await act(id, "draw", { token: ALICE });

    expect((await state(id, ALICE)).state!.drawOfferedToYou).toBe(false);
    expect((await state(id, BOB)).state!.drawOfferedToYou).toBe(true);
  });

  it("does not let a player accept their own offer", async () => {
    const id = await startedGame();
    await act(id, "draw", { token: ALICE });

    const again = await act(id, "draw", { token: ALICE });
    expect(again.state!.status).toBe("playing");
  });

  it("clears an offer when a move is played, so it cannot be accepted twenty moves later", async () => {
    const id = await startedGame();
    await act(id, "draw", { token: ALICE });
    await move(id, ALICE, "e4", "e2e4");

    const seen = await state(id, BOB);
    expect(seen.state!.drawOfferedBy).toBeNull();
  });

  it("lets the opponent decline an offer", async () => {
    const id = await startedGame();
    await act(id, "draw", { token: ALICE });

    const declined = await act(id, "decline", { token: BOB });
    expect(declined.state!.drawOfferedBy).toBeNull();
    expect(declined.state!.status).toBe("playing");
  });

  it("aborts a game nobody has moved in", async () => {
    const id = await startedGame();

    const aborted = await act(id, "abort", { token: ALICE });
    expect(aborted.state!.status).toBe("finished");
    expect(aborted.state!.outcome).toBe("aborted");
  });

  it("refuses to abort a real game, which has to be resigned", async () => {
    const id = await startedGame();
    await move(id, ALICE, "e4", "e2e4");

    const refused = await act(id, "abort", { token: ALICE });
    expect(refused.ok).toBe(false);
  });

  it("ends the game on time when the side to move runs out", async () => {
    // A 60ms clock so the flag actually falls inside a test.
    const id = await startedGame({ initialMs: 60, incrementMs: 0 });
    await new Promise((resolve) => setTimeout(resolve, 120));

    const seen = await state(id, BOB);
    expect(seen.state!.status).toBe("finished");
    expect(seen.state!.outcome).toBe("black");
    expect(seen.state!.reason).toBe("timeout");
  });

  it("refuses a move made after the flag fell", async () => {
    const id = await startedGame({ initialMs: 60, incrementMs: 0 });
    await new Promise((resolve) => setTimeout(resolve, 120));

    // Sitting on a losing position and then moving must not work.
    const late = await move(id, ALICE, "e4", "e2e4");
    expect(late.ok).toBe(false);
    expect(late.reason).toBe("clock_expired");
  });
});

describe("disputes", () => {
  it("aborts the game when a player's engine rejects the opponent's move", async () => {
    const id = await startedGame();
    await move(id, ALICE, "Ke2", "e1e2");

    // Bob's copy of Moonforge says that move is not legal. With no engine of its own, the
    // server cannot say who is right — so nobody wins.
    const disputed = await act(id, "dispute", { token: BOB, ply: 1 });

    expect(disputed.state!.status).toBe("finished");
    expect(disputed.state!.outcome).toBe("aborted");
    expect(disputed.state!.reason).toBe("disputed");
  });

  it("lets a claimed checkmate be disputed after the game ends", async () => {
    const id = await startedGame();
    await move(id, ALICE, "e4", "e2e4", { outcome: "white", reason: "checkmate" });

    // A bogus mate claim is exactly the cheat worth catching, and it arrives with the game
    // already marked finished — so the window has to stay open a little longer than that.
    const disputed = await act(id, "dispute", { token: BOB, ply: 1 });
    expect(disputed.state!.outcome).toBe("aborted");
    expect(disputed.state!.reason).toBe("disputed");
  });

  it("does not let a player dispute their own move", async () => {
    const id = await startedGame();
    await move(id, ALICE, "e4", "e2e4");

    const refused = await act(id, "dispute", { token: ALICE, ply: 1 });
    expect(refused.ok).toBe(false);
  });

  it("refuses a dispute from someone not in the game", async () => {
    const id = await startedGame();
    await move(id, ALICE, "e4", "e2e4");

    const refused = await act(id, "dispute", { token: CAROL, ply: 1 });
    expect(refused.ok).toBe(false);
    expect(refused.reason).toBe("unknown_player");
  });
});

describe("routing", () => {
  it("refuses a match id that is not one the lobby could have issued", async () => {
    // Otherwise a caller could name an arbitrary Durable Object and make us create it. These
    // are literal ids rather than traversal strings, because `new URL` normalises "../" away
    // before the route pattern ever sees it — testing that would prove nothing about the guard.
    for (const id of ["lobby", "not-a-uuid", "vote", "00000000-0000-0000-0000-00000000000"]) {
      const response = await SELF.fetch(`https://api.test/play/${id}/state?token=x`, { headers: { Origin: SITE } });
      expect(response.status, `id "${id}" should not route`).toBe(404);
    }
  });

  it("refuses an action that is not one of the ones a game supports", async () => {
    const id = await startedGame();

    const response = await SELF.fetch(`https://api.test/play/${id}/delete`, {
      method: "POST",
      headers: { Origin: SITE, "Content-Type": "application/json" },
      body: JSON.stringify({ token: ALICE }),
    });

    expect(response.status).toBe(404);
  });

  it("refuses a play route from an origin that is not the site", async () => {
    const response = await SELF.fetch("https://api.test/play/seek", {
      method: "POST",
      headers: { Origin: "https://evil.example", "Content-Type": "application/json" },
      body: JSON.stringify({ token: ALICE, clock: BLITZ }),
    });

    expect(response.status).toBe(403);
  });

  it("answers the preflight a browser sends before posting a move", async () => {
    const response = await SELF.fetch("https://api.test/play/seek", {
      method: "OPTIONS",
      headers: { Origin: SITE },
    });

    expect(response.status).toBe(204);
    expect(response.headers.get("Access-Control-Allow-Origin")).toBe(SITE);
  });
});
