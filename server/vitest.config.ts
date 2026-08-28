import { cloudflareTest } from "@cloudflare/vitest-pool-workers";
import { defineConfig } from "vitest/config";

// Tests run inside workerd, the same runtime that serves production, rather than against a
// mock of it. A route that passes here is a route that works when deployed.
//
// As of vitest-pool-workers 0.22 this is a Vite plugin; older docs show a
// `test.poolOptions.workers` block, which no longer exists.
export default defineConfig({
  plugins: [
    cloudflareTest({
      wrangler: { configPath: "./wrangler.jsonc" },
      miniflare: {
        // The referee secret is a real secret in production (`wrangler secret put`), so it
        // is declared here rather than committed to wrangler.jsonc or read from .dev.vars —
        // that keeps the suite self-contained on a fresh clone.
        bindings: { REFEREE_SECRET: "test-referee-secret" },
      },
    }),
  ],
});
