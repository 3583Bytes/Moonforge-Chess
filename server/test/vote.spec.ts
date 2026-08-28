import { beforeEach, describe, expect, it } from "vitest";
import { SELF, reset } from "cloudflare:test";

const SITE = "https://chessbin.com";
const SECRET = "test-referee-secret";
const VOTER = "voter-token-000001";

/** Candidate moves as the referee would publish them: already vetted by the real engine. */
const CANDIDATES = ["e4", "d4", "Nf3"];

// The ballot box is one Durable Object, so without this every test inherits the previous
// test's votes and round number. Earlier versions of the pool isolated storage per test
// automatically; 0.22 replaced that with an explicit reset.
beforeEach(reset);

async function openRound(closesAt: number, candidates = CANDIDATES, secret = SECRET) {
  return SELF.fetch("https://api.test/vote/next", {
    method: "POST",
    headers: { Authorization: `Bearer ${secret}` },
    body: JSON.stringify({ candidates, closesAt }),
  });
}

async function cast(token: string, san: string, ip = "203.0.113.7") {
  return SELF.fetch("https://api.test/vote/cast", {
    method: "POST",
    headers: { Origin: SITE, "Content-Type": "application/json", "CF-Connecting-IP": ip },
    body: JSON.stringify({ token, san }),
  });
}

async function tally() {
  const response = await SELF.fetch("https://api.test/vote/tally", { headers: { Origin: SITE } });
  return response.json() as Promise<{
    round: number;
    open: boolean;
    voters: number;
    counts: { san: string; votes: number }[];
  }>;
}

const votesFor = (counts: { san: string; votes: number }[], san: string) =>
  counts.find((entry) => entry.san === san)?.votes;

const inAnHour = () => Date.now() + 3_600_000;

describe("casting a vote", () => {
  beforeEach(async () => {
    await openRound(inAnHour());
  });

  it("records a choice", async () => {
    const response = await cast(VOTER, "e4");

    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ ok: true, choice: "e4" });

    const counts = await tally();
    expect(counts.voters).toBe(1);
    expect(votesFor(counts.counts, "e4")).toBe(1);
    expect(votesFor(counts.counts, "d4")).toBe(0);
  });

  it("lets a voter change their mind without counting twice", async () => {
    await cast(VOTER, "e4");
    await cast(VOTER, "d4");

    const counts = await tally();
    expect(counts.voters).toBe(1);
    expect(votesFor(counts.counts, "e4")).toBe(0);
    expect(votesFor(counts.counts, "d4")).toBe(1);
  });

  it("counts different voters separately", async () => {
    await cast("voter-token-aaaaaa", "e4");
    await cast("voter-token-bbbbbb", "e4");
    await cast("voter-token-cccccc", "Nf3");

    const counts = await tally();
    expect(counts.voters).toBe(3);
    expect(votesFor(counts.counts, "e4")).toBe(2);
    expect(votesFor(counts.counts, "Nf3")).toBe(1);
  });

  it("refuses a move the referee did not offer", async () => {
    // The Worker cannot judge legality, so its only defence is the published candidate list.
    const response = await cast(VOTER, "Qh5");

    expect(response.status).toBe(400);
    expect(await response.json()).toMatchObject({ ok: false, reason: "unknown_move" });
    expect((await tally()).voters).toBe(0);
  });

  it.each(["", "short", "x".repeat(65)])("refuses an implausible token (%j)", async (token) => {
    const response = await cast(token, "e4");

    expect(response.status).toBe(400);
    expect(await response.json()).toMatchObject({ ok: false, reason: "bad_token" });
  });

  it("refuses a body that is not JSON", async () => {
    const response = await SELF.fetch("https://api.test/vote/cast", {
      method: "POST",
      headers: { Origin: SITE },
      body: "not json at all",
    });

    expect(response.status).toBe(400);
  });

  it("never reveals who voted", async () => {
    await cast(VOTER, "e4");
    const body = await (await SELF.fetch("https://api.test/vote/tally", { headers: { Origin: SITE } })).text();

    // Tokens identify a browser and addresses identify a person's connection. Neither is
    // anyone else's business, so neither may appear in the public view.
    expect(body).not.toContain(VOTER);
    expect(body).not.toContain("203.0.113.7");
  });
});

describe("when no round is open", () => {
  it("refuses votes rather than storing them for later", async () => {
    const response = await cast(VOTER, "e4");

    expect(response.status).toBe(409);
    expect(await response.json()).toMatchObject({ ok: false, reason: "no_round" });
  });

  it("reports itself closed", async () => {
    const counts = await tally();

    expect(counts.round).toBe(0);
    expect(counts.open).toBe(false);
  });
});

describe("when the deadline has passed", () => {
  it("refuses a late vote", async () => {
    await openRound(Date.now() - 1_000);
    const response = await cast(VOTER, "e4");

    expect(response.status).toBe(409);
    expect(await response.json()).toMatchObject({ ok: false, reason: "round_closed" });
  });

  it("still shows the round, marked closed", async () => {
    await openRound(Date.now() - 1_000);
    const counts = await tally();

    expect(counts.round).toBe(1);
    expect(counts.open).toBe(false);
  });
});

describe("the address cap", () => {
  it("stops one address from inventing an unlimited electorate", async () => {
    await openRound(inAnHour());

    const allowed = [];
    for (let i = 0; i < 8; i++) {
      allowed.push(await cast(`stuffer-token-${i}0000`, "e4", "198.51.100.9"));
    }
    expect(allowed.every((response) => response.ok)).toBe(true);

    const ninth = await cast("stuffer-token-990000", "e4", "198.51.100.9");
    expect(ninth.status).toBe(429);
    expect(await ninth.json()).toMatchObject({ reason: "rate_limited" });
    expect((await tally()).voters).toBe(8);
  });

  it("does not charge a voter for changing their mind", async () => {
    await openRound(inAnHour());
    for (let i = 0; i < 8; i++) await cast(`settled-token-${i}0000`, "e4", "198.51.100.10");

    // Eight voters have used the allowance; one of them switching must still work.
    const change = await cast("settled-token-00000", "d4", "198.51.100.10");
    expect(change.ok).toBe(true);
  });

  it("counts each address separately", async () => {
    await openRound(inAnHour());
    for (let i = 0; i < 8; i++) await cast(`office-token-${i}0000`, "e4", "198.51.100.11");

    const elsewhere = await cast("home-token-000001", "d4", "198.51.100.12");
    expect(elsewhere.ok).toBe(true);
  });
});

describe("referee-only routes", () => {
  it.each([
    ["no header", undefined],
    ["wrong secret", "Bearer nope"],
    ["right length, wrong value", `Bearer ${"x".repeat(SECRET.length)}`],
  ])("refuse the ballots with %s", async (_label, authorization) => {
    const response = await SELF.fetch("https://api.test/vote/round", {
      headers: authorization === undefined ? {} : { Authorization: authorization },
    });

    expect(response.status).toBe(403);
  });

  it("hands the ballots to the referee", async () => {
    await openRound(inAnHour());
    await cast(VOTER, "Nf3");

    const response = await SELF.fetch("https://api.test/vote/round", {
      headers: { Authorization: `Bearer ${SECRET}` },
    });

    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({
      round: 1,
      candidates: CANDIDATES,
      ballots: { [VOTER]: "Nf3" },
    });
  });

  it("refuses to open a round with nothing to vote on", async () => {
    const response = await openRound(inAnHour(), []);

    expect(response.status).toBe(400);
    expect(await response.json()).toMatchObject({ error: "no_candidates" });
  });

  it("refuses a round with no usable deadline", async () => {
    const response = await SELF.fetch("https://api.test/vote/next", {
      method: "POST",
      headers: { Authorization: `Bearer ${SECRET}` },
      body: JSON.stringify({ candidates: CANDIDATES }),
    });

    expect(response.status).toBe(400);
  });

  it("clears the previous round's ballots when opening the next", async () => {
    await openRound(inAnHour());
    await cast(VOTER, "e4");
    expect((await tally()).voters).toBe(1);

    await openRound(inAnHour(), ["Nc3", "c4"]);
    const counts = await tally();

    expect(counts.round).toBe(2);
    expect(counts.voters).toBe(0);
    expect(counts.counts.map((entry) => entry.san)).toEqual(["Nc3", "c4"]);
  });

  it("frees the address cap for the new round", async () => {
    await openRound(inAnHour());
    for (let i = 0; i < 8; i++) await cast(`round1-token-${i}0000`, "e4", "198.51.100.13");
    expect((await cast("round1-token-990000", "e4", "198.51.100.13")).status).toBe(429);

    await openRound(inAnHour());
    expect((await cast("round2-token-000001", "e4", "198.51.100.13")).ok).toBe(true);
  });

  it("de-duplicates a candidate list", async () => {
    await openRound(inAnHour(), ["e4", "e4", "d4"]);

    expect((await tally()).counts.map((entry) => entry.san)).toEqual(["e4", "d4"]);
  });
});
