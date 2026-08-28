import { describe, expect, it } from "vitest";
import { SELF } from "cloudflare:test";
import { isAllowedOrigin, type OriginConfig } from "../src/index";

const SITE = "https://chessbin.com";

describe("origin rules", () => {
  // A pure function, tested directly, so every branch is reachable without standing up a
  // Worker per case.
  const production: OriginConfig = { ALLOWED_ORIGINS: `${SITE},https://www.chessbin.com` };

  it("allows the site", () => {
    expect(isAllowedOrigin(SITE, production)).toBe(true);
    expect(isAllowedOrigin("https://www.chessbin.com", production)).toBe(true);
  });

  it("refuses anywhere else", () => {
    expect(isAllowedOrigin("https://evil.example", production)).toBe(false);
    expect(isAllowedOrigin("https://chessbin.com.evil.example", production)).toBe(false);
    expect(isAllowedOrigin("http://chessbin.com", production)).toBe(false);
  });

  it("lets a caller with no Origin through, which is how the referee reaches us", () => {
    expect(isAllowedOrigin(null, production)).toBe(true);
  });

  it("allows loopback only when development explicitly asks for it", () => {
    expect(isAllowedOrigin("http://localhost:5000", production)).toBe(false);

    const development: OriginConfig = { ...production, ALLOW_LOCALHOST: "1" };
    expect(isAllowedOrigin("http://localhost:5000", development)).toBe(true);
    expect(isAllowedOrigin("https://localhost:7089", development)).toBe(true);
    expect(isAllowedOrigin("http://127.0.0.1:5173", development)).toBe(true);
    expect(isAllowedOrigin("https://notlocalhost.example", development)).toBe(false);
  });

  it("refuses everything when nothing is configured", () => {
    expect(isAllowedOrigin(SITE, {})).toBe(false);
  });
});

describe("the deployed surface", () => {
  it("answers /health", async () => {
    const response = await SELF.fetch(`https://api.test/health`, {
      headers: { Origin: SITE },
    });

    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ ok: true, service: "chessbin-api" });
    expect(response.headers.get("Access-Control-Allow-Origin")).toBe(SITE);
    expect(response.headers.get("Vary")).toBe("Origin");
  });

  it("refuses another origin", async () => {
    const response = await SELF.fetch(`https://api.test/health`, {
      headers: { Origin: "https://evil.example" },
    });

    expect(response.status).toBe(403);
    expect(response.headers.get("Access-Control-Allow-Origin")).toBeNull();
  });

  it("answers a preflight", async () => {
    const response = await SELF.fetch(`https://api.test/vote/cast`, {
      method: "OPTIONS",
      headers: { Origin: SITE },
    });

    expect(response.status).toBe(204);
    expect(response.headers.get("Access-Control-Allow-Methods")).toContain("POST");
  });

  it("sends no CORS headers when there was no Origin to answer", async () => {
    const response = await SELF.fetch(`https://api.test/health`);

    expect(response.status).toBe(200);
    expect(response.headers.get("Access-Control-Allow-Origin")).toBeNull();
  });

  it("404s an unknown route", async () => {
    const response = await SELF.fetch(`https://api.test/nope`, {
      headers: { Origin: SITE },
    });

    expect(response.status).toBe(404);
    expect(await response.json()).toEqual({ error: "not_found" });
  });
});
