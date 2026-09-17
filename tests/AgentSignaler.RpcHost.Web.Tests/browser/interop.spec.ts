import { test, expect } from "./fixtures.ts";
import type { Page as BrowserPage } from "@playwright/test";
import type { RpcClient } from "../client/rpc-client.ts";
import type { DomainView, Page, Snapshot } from "../client/state.ts";

declare global {
  interface Window {
    contract: {
      client: RpcClient;
      settings?: DomainView<unknown>;
      machines?: DomainView<readonly unknown[]>;
    };
  }
}

async function connect(page: BrowserPage, url: string): Promise<void> {
  await page.evaluate(async endpoint => {
    const module = "/client/rpc-client.ts";
    const { RpcClient } = await import(module) as typeof import("../client/rpc-client.ts");
    window.contract?.client.dispose();
    const client = new RpcClient();
    window.contract = { client };
    await client.connect(endpoint);
  }, url);
}

test("real Chromium native WebSocket reads independent snapshots and bounded machine pages", async ({ page, site, host }) => {
  await page.goto(site);
  await connect(page, host.url);
  await expect.poll(() => page.evaluate(async () =>
    (await window.contract.client.request<Snapshot<{ initialAttemptCompleted: boolean }>>("system.getStatus"))
      .state.initialAttemptCompleted), { timeout: 30000 }).toBe(true);
  const snapshots = await page.evaluate(async () => {
    const client = window.contract.client;
    const methods = [
      "system.getStatus", "settings.get", "receiver.getStatus", "sharing.getStatus",
      "prerequisites.getStatus", "devboxes.getCatalog", "machines.list",
    ];
    return await Promise.all(client.batch(methods.map(method => ({ method })))) as Snapshot<unknown>[];
  });
  expect(snapshots.map(snapshot => snapshot.domain)).toEqual([
    "system", "settings", "receiver", "sharing", "prerequisites", "devboxes", "machines",
  ]);
  for (const snapshot of snapshots) {
    expect(snapshot.protocolVersion).toBe(1);
    expect(snapshot.hostInstanceId).toBe(host.ready!.hostInstanceId);
    expect(snapshot.revision).toMatch(/^(0|[1-9][0-9]*)$/);
    expect(snapshot.isStale).toBe(false);
    expect(snapshot).toHaveProperty("state");
  }
  expect(snapshots[0]!.state).toMatchObject({ lifecycle: "operational", initialAttemptCompleted: true });
  for (const snapshot of snapshots.filter(value => ["machines", "devboxes"].includes(value.domain))) {
    expect(snapshot.state).toMatchObject({ items: [], offset: 0, limit: 100, nextOffset: null, totalCount: 0 });
  }
  const machines = await page.evaluate(async () => {
    const module = "/client/state.ts";
    const { paginatedView } = await import(module) as typeof import("../client/state.ts");
    const client = window.contract.client;
    const view = client.watch(paginatedView("machines", params =>
      client.request<Snapshot<Page<unknown>>>("machines.list", { ...params })));
    window.contract.machines = view;
    await view.refresh();
    return { data: view.data, stale: view.stale, error: view.error ? String(view.error) : null };
  });
  expect(machines).toEqual({ data: [], stale: false, error: null });
});

test("real host mixed request/notification batch reports errors without closing controller", async ({ page, site, host }) => {
  await page.goto(site);
  await connect(page, host.url);
  const results = await page.evaluate(async () => {
    const replies = window.contract.client.batch([
      { method: "system.getStatus" },
      { method: "system.getStatus", notification: true },
      { method: "synthetic.unknownMethod" },
      { method: "machines.list", params: { limit: "invalid" } },
    ]);
    const settled = await Promise.allSettled(replies);
    return settled.map(result => result.status === "fulfilled"
      ? { ok: true, value: result.value }
      : { ok: false, code: result.reason.code });
  });
  expect(results).toHaveLength(3);
  expect(results[0]!.ok).toBe(true);
  expect(results[1]).toEqual({ ok: false, code: -32601 });
  expect(results[2]).toEqual({ ok: false, code: -32602 });
  const status = await page.evaluate(() => window.contract.client.request<Snapshot<unknown>>("system.getStatus"));
  expect(status.hostInstanceId).toBe(host.ready!.hostInstanceId);
});

test("actual host rejects a second browser controller while the first remains usable", async ({ page, site, host }) => {
  await page.goto(site);
  await connect(page, host.url);
  const rejected = await page.evaluate(endpoint => new Promise<boolean>((resolve, reject) => {
    const socket = new WebSocket(endpoint);
    const timer = setTimeout(() => { socket.close(); reject(new Error("Controller rejection timeout")); }, 10000);
    socket.onopen = () => { clearTimeout(timer); socket.close(); resolve(false); };
    socket.onerror = () => { clearTimeout(timer); resolve(true); };
  }), host.url);
  expect(rejected).toBe(true);
  expect((await page.evaluate(() => window.contract.client.request<Snapshot<unknown>>("system.getStatus")))
    .hostInstanceId).toBe(host.ready!.hostInstanceId);
});

test("real settings invalidation refetches the displayed domain and stale edits fail without retry", async ({ page, site, host }) => {
  await page.goto(site);
  await connect(page, host.url);
  const updated = await page.evaluate(async () => {
    const module = "/client/state.ts";
    const { snapshotView } = await import(module) as typeof import("../client/state.ts");
    const client = window.contract.client;
    const view = client.watch(snapshotView("settings", () => client.request<Snapshot<unknown>>("settings.get")));
    window.contract.settings = view;
    await view.refresh();
    const before = await client.request<Snapshot<unknown>>("settings.get");
    const after = await client.request<Snapshot<unknown>>("settings.update", {
      hostInstanceId: before.hostInstanceId,
      expectedRevision: before.revision,
      settings: { receiveDetailedConversations: true },
    });
    let staleCode: number | undefined;
    try {
      await client.request("settings.update", {
        hostInstanceId: before.hostInstanceId,
        expectedRevision: before.revision,
        settings: { receiveDetailedConversations: false },
      });
    } catch (error) { staleCode = (error as { code: number }).code; }
    return { revision: after.revision, staleCode };
  });
  expect(updated.staleCode).toBe(1004);
  await expect.poll(() => page.evaluate(() => ({
    revision: window.contract.settings!.revision,
    stale: window.contract.settings!.stale,
    enabled: (window.contract.settings!.data as { saved: { receiveDetailedConversations: boolean } })
      .saved.receiveDetailedConversations,
  }))).toEqual({ revision: updated.revision, stale: false, enabled: true });
});

test("actual host echoes string and explicit null IDs, distinguishes notifications, and rejects empty batches", async ({ page, site, host }) => {
  await page.goto(site);
  const result = await page.evaluate(async endpoint => {
    const socket = new WebSocket(endpoint);
    await new Promise<void>((resolve, reject) => {
      socket.onopen = () => resolve();
      socket.onerror = () => reject(new Error("Fixture socket unavailable"));
    });
    const packets: unknown[] = [];
    const exchange = (text: string) => new Promise<unknown>((resolve, reject) => {
      const timer = setTimeout(() => { socket.removeEventListener("message", receive); reject(new Error("Response timeout")); }, 10000);
      const receive = (event: MessageEvent<string>) => {
        const packet = JSON.parse(event.data);
        if (!Array.isArray(packet) && !Object.hasOwn(packet, "id")) return;
        clearTimeout(timer);
        socket.removeEventListener("message", receive);
        packets.push(packet);
        resolve(packet);
      };
      socket.addEventListener("message", receive);
      socket.send(text);
    });
    try {
      await exchange(JSON.stringify([
        { jsonrpc: "2.0", id: "9007199254740993", method: "system.getStatus" },
        { jsonrpc: "2.0", id: null, method: "settings.get" },
        { jsonrpc: "2.0", method: "system.getStatus" },
      ]));
      await exchange("[]");
      await exchange("{");
      return packets;
    } finally { socket.close(); }
  }, host.url);
  const batch = result[0] as { id: unknown; result?: unknown }[];
  expect(batch).toHaveLength(2);
  expect(batch.map(reply => reply.id)).toEqual(expect.arrayContaining(["9007199254740993", null]));
  expect(result[1]).toMatchObject({ jsonrpc: "2.0", id: null, error: { code: -32600 } });
  expect(result[2]).toMatchObject({ jsonrpc: "2.0", id: null, error: { code: -32700 } });
});

test("reconnect same process refetches and process restart discards old instance state", async ({ page, site, host }) => {
  await page.goto(site);
  await connect(page, host.url);
  await page.evaluate(async () => {
    const module = "/client/state.ts";
    const { snapshotView } = await import(module) as typeof import("../client/state.ts");
    const client = window.contract.client;
    const view = client.watch(snapshotView("settings", () => client.request<Snapshot<unknown>>("settings.get")));
    window.contract.settings = view;
    await view.refresh();
  });
  const firstInstance = host.ready!.hostInstanceId;
  await page.evaluate(() => window.contract.client.disconnect());
  // HTTP 409 is expected during disconnect drain; retry connection only, never a mutation.
  await expect(async () => {
    await page.evaluate(async endpoint => {
      await window.contract.client.connect(endpoint);
      await window.contract.settings!.refresh();
    }, host.url);
  }).toPass({ timeout: 20000, intervals: [100, 250, 500] });
  expect(await page.evaluate(() => window.contract.settings!.stale)).toBe(false);
  expect(host.ready!.hostInstanceId).toBe(firstInstance);
  await page.evaluate(() => window.contract.client.disconnect());
  await host.stop();
  await host.start();
  expect(host.ready!.hostInstanceId).not.toBe(firstInstance);
  await page.evaluate(async endpoint => {
    await window.contract.client.connect(endpoint);
    await window.contract.settings!.refresh();
  }, host.url);
  expect(await page.evaluate(() => window.contract.settings!.stale)).toBe(false);
  const status = await page.evaluate(() => window.contract.client.request<Snapshot<unknown>>("system.getStatus"));
  expect(status.hostInstanceId).toBe(host.ready!.hostInstanceId);
});

test("explicit shutdown acknowledges before closing the actual host", async ({ page, site, host }) => {
  await page.goto(site);
  await connect(page, host.url);
  const result = await page.evaluate(() => window.contract.client.request("system.shutdown"));
  expect(result).toBeDefined();
  expect(await host.waitForExit()).toBe(0);
});
