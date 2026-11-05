import { defineConfig } from "vitest/config";

// Client tests run in plain Node (no DOM package). They are dev-only and never reach the build output:
// vite.config.ts and tsconfig.json (which includes only src/) are untouched by this file.
export default defineConfig({
  test: {
    environment: "node",
    include: ["test/**/*.test.ts"],
  },
});
