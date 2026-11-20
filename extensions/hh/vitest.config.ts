import { defineConfig } from "vitest/config";

// tests/ — расширение, web/tests/ — веб (#130)
export default defineConfig({
  test: { environment: "happy-dom", include: ["tests/**/*.test.ts", "web/tests/**/*.test.ts"] },
});
