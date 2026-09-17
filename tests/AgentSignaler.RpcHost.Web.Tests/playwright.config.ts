import { defineConfig } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";

// Keep Chromium's disposable profiles inside this test project, not the user's profile/cache.
if (process.env.AGENT_SIGNALER_RPC_WEB_NETWORK_TESTS === "1") {
  const directory = fileURLToPath(new URL("./.fixtures/browser-work/", import.meta.url));
  mkdirSync(directory, { recursive: true });
  process.env.TEMP = directory;
  process.env.TMP = directory;
}

export default defineConfig({
  testDir: "./browser",
  timeout: 60000,
  expect: { timeout: 10000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: "list",
  use: {
    browserName: "chromium",
    headless: true,
    trace: "off",
    screenshot: "off",
    video: "off",
  },
});
