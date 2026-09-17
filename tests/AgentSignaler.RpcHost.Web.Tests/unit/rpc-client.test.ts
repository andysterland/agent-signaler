import assert from "node:assert/strict";
import { test } from "node:test";
import { requestDeadline, RpcClient } from "../client/rpc-client.ts";
import type { RpcScheduler, RpcTransport } from "../client/rpc-client.ts";
import { RpcError, snapshotView } from "../client/state.ts";
import type { Snapshot } from "../client/state.ts";

const host = "11111111-1111-4111-8111-111111111111";
const flush = () => new Promise<void>(resolve => setImmediate(resolve));
const state = (domain: string, revision = "1") =>
  ({ protocolVersion: 1, hostInstanceId: host, domain, revision, state: { synthetic: true }, isStale: false });
type Request = { id?: string; method: string; params?: unknown };

class FakeSocket implements RpcTransport {
  readyState = 0;
  sent: Request[][] = [];
  private opened: () => void = () => {};
  private closed: () => void = () => {};
  private messageReceived: (data: unknown) => void = () => {};
  onSend: (requests: Request[]) => void = requests => {
    for (const request of requests) {
      if (request.id) this.message({ jsonrpc: "2.0", id: request.id, result: state("system") });
    }
  };
  constructor() {
    queueMicrotask(() => { this.readyState = 1; this.opened(); });
  }
  onOpen(listener: () => void) { this.opened = listener; }
  onClose(listener: () => void) { this.closed = listener; }
  onError(_listener: () => void) {}
  onMessage(listener: (data: unknown) => void) { this.messageReceived = listener; }
  send(text: string) {
    const decoded = JSON.parse(text) as Request | Request[];
    const requests = Array.isArray(decoded) ? decoded : [decoded];
    this.sent.push(requests);
    this.onSend(requests);
  }
  message(value: unknown) { this.messageReceived(JSON.stringify(value)); }
  close() {
    if (this.readyState === 3) return;
    this.readyState = 3;
    this.closed();
  }
}

class FakeScheduler implements RpcScheduler {
  private now = 0;
  private callbacks = new Map<() => void, number>();
  schedule(callback: () => void, delay: number): () => void {
    this.callbacks.set(callback, this.now + delay);
    return () => { this.callbacks.delete(callback); };
  }
  advance(milliseconds: number) {
    this.now += milliseconds;
    for (const [callback, deadline] of this.callbacks) {
      if (deadline <= this.now) {
        this.callbacks.delete(callback);
        callback();
      }
    }
  }
}

test("mixed batches correlate out-of-order replies, preserve string revisions, never expect notification replies", async () => {
  const socket = new FakeSocket();
  const client = new RpcClient(() => socket);
  await client.connect();
  socket.onSend = requests => {
    assert.equal(requests[1]!.id, undefined);
    socket.message([
      { jsonrpc: "2.0", id: requests[2]!.id, error: { code: -32601, message: "Method not found" } },
      { jsonrpc: "2.0", id: requests[0]!.id, result: state("settings", "9007199254740993") },
    ]);
  };
  const replies = client.batch([
    { method: "settings.get" },
    { method: "system.getStatus", notification: true },
    { method: "not.aMethod" },
  ]);
  assert.equal(replies.length, 2);
  const results = await Promise.allSettled(replies);
  assert.equal(results[0]!.status, "fulfilled");
  assert.equal((results[0] as PromiseFulfilledResult<{ revision: string }>).value.revision, "9007199254740993");
  const error = (results[1] as PromiseRejectedResult).reason;
  assert.ok(error instanceof RpcError);
  assert.equal(error.code, -32601);
  const ids = socket.sent.flat().filter(request => request.id).map(request => request.id);
  assert.equal(new Set(ids).size, ids.length);
  assert.ok(ids.every(id => typeof id === "string"));
  client.dispose();
});

test("connection registers invalidations before first status reply and refetch converges", async () => {
  const socket = new FakeSocket();
  let machineQueries = 0;
  socket.onSend = requests => {
    for (const request of requests) {
      if (request.method === "system.getStatus") {
        socket.message({ jsonrpc: "2.0", method: "machines.changed",
          params: { hostInstanceId: host, domain: "machines", revision: "3" } });
        socket.message({ jsonrpc: "2.0", id: request.id, result: state("system") });
      } else {
        machineQueries++;
        socket.message({ jsonrpc: "2.0", id: request.id,
          result: state("machines", machineQueries === 1 ? "2" : "3") });
      }
    }
  };
  const client = new RpcClient(() => socket);
  const view = client.watch(snapshotView("machines", () => client.request<Snapshot<unknown>>("machines.list")));
  await client.connect();
  await view.refresh();
  assert.equal(view.revision, "3");
  assert.equal(machineQueries, 2);
  client.dispose();
});

test("disconnect rejects outstanding mutations without repeating them and ignores old transport replies", async () => {
  const sockets: FakeSocket[] = [];
  const client = new RpcClient(() => {
    const socket = new FakeSocket();
    sockets.push(socket);
    return socket;
  });
  await client.connect();
  const old = sockets[0]!;
  old.onSend = () => {};
  const mutation = client.request("settings.update", { synthetic: true });
  const rejection = assert.rejects(mutation, /commit state unknown/);
  await client.connect();
  await rejection;
  old.message({ jsonrpc: "2.0", id: old.sent.at(-1)![0]!.id, result: state("settings", "999") });
  await flush();
  assert.equal(sockets[1]!.sent.flat().filter(request => request.method === "settings.update").length, 0);
  client.dispose();
});

test("RPC error data preserves commit state but excludes remote text from exception message", async () => {
  const socket = new FakeSocket();
  const client = new RpcClient(() => socket);
  await client.connect();
  socket.onSend = requests => socket.message({ jsonrpc: "2.0", id: requests[0]!.id,
    error: { code: 1006, message: "synthetic-private-detail", data: { commitState: "committed", retryable: false } } });
  await assert.rejects(client.request("settings.update"), error => {
    assert.ok(error instanceof RpcError);
    assert.equal(error.message, "RPC error 1006");
    assert.deepEqual(error.data, { commitState: "committed", retryable: false });
    return true;
  });
  client.dispose();
});

test("bounds reject empty/oversized batches and malformed response envelopes close transport", async () => {
  const socket = new FakeSocket();
  const client = new RpcClient(() => socket);
  await client.connect();
  assert.throws(() => client.batch([]), /limit/);
  assert.throws(() => client.batch(Array.from({ length: 33 }, () => ({ method: "system.getStatus" }))), /limit/);
  socket.onSend = requests => socket.message({ jsonrpc: "2.0", id: requests[0]!.id,
    result: {}, error: { code: -32603 } });
  await assert.rejects(client.request("settings.get"), /Disconnected/);
  assert.equal(socket.readyState, 3);
  client.dispose();
});

test("invalid initialization revision releases the controller instead of leaking a failed connection", async () => {
  const socket = new FakeSocket();
  socket.onSend = requests => socket.message({ jsonrpc: "2.0", id: requests[0]!.id,
    result: { ...state("system"), revision: 9007199254740992 } });
  const client = new RpcClient(() => socket);
  await assert.rejects(client.connect(), /Invalid revision/);
  assert.equal(socket.readyState, 3);
  client.dispose();
});

test("request deadlines match the host local, sign-in, and CLI operation profiles", () => {
  assert.equal(requestDeadline("windowsApp.signIn"), 180000);
  for (const method of [
    "devboxes.refresh", "windowsApp.map", "windowsApp.refresh", "windowsApp.open", "sharing.start",
    "sharing.delete", "sharing.logout", "prerequisites.check", "prerequisites.checkAll",
  ]) assert.equal(requestDeadline(method), 300000, method);
  for (const method of ["settings.get", "settings.update", "operations.cancel", "system.shutdown"]) {
    assert.equal(requestDeadline(method), 10000, method);
  }
});

test("discovery survives ten seconds and sign-in gets its full three-minute request deadline", async () => {
  const socket = new FakeSocket();
  const timer = new FakeScheduler();
  const client = new RpcClient(() => socket, timer);
  await client.connect();
  socket.onSend = () => {};
  const completed: string[] = [];
  const operations = ["settings.get", "windowsApp.signIn", "devboxes.refresh"].map(method =>
    client.request(method).catch(error => {
      assert.match(String(error), /Request timeout; commit state unknown/);
      completed.push(method);
    }));
  timer.advance(10000);
  await flush();
  assert.deepEqual(completed, ["settings.get"]);
  timer.advance(169999);
  await flush();
  assert.deepEqual(completed, ["settings.get"]);
  timer.advance(1);
  await flush();
  assert.deepEqual(completed, ["settings.get", "windowsApp.signIn"]);
  timer.advance(119999);
  await flush();
  assert.equal(completed.length, 2);
  timer.advance(1);
  await Promise.all(operations);
  assert.deepEqual(completed, ["settings.get", "windowsApp.signIn", "devboxes.refresh"]);
  client.dispose();
});

test("32 outstanding applications preserve four reserved cancel/shutdown slots and reject a fifth control", async () => {
  const socket = new FakeSocket();
  const client = new RpcClient(() => socket);
  await client.connect();
  socket.onSend = () => {};
  const applications = client.batch(Array.from({ length: 32 }, () => ({ method: "devboxes.refresh" })));
  const settledApplications = Promise.allSettled(applications);
  await assert.rejects(client.request("settings.get"), /Client request limit/);
  const controls = client.batch([
    { method: "operations.cancel", params: { requestId: socket.sent[1]![0]!.id } },
    { method: "system.shutdown" },
    { method: "system.cancelStartup" },
    { method: "sharing.stop" },
  ]);
  const settledControls = Promise.allSettled(controls);
  assert.equal(controls.length, 4);
  await assert.rejects(client.request("operations.cancel", { requestId: "unknown" }), /Client request limit/);
  assert.throws(() => client.notify("system.shutdown"), /Client request limit/);
  const controlId = socket.sent.at(-1)![0]!.id;
  socket.message({ jsonrpc: "2.0", id: controlId, result: { status: "cancelRequested" } });
  const replacement = client.request("operations.cancel", { requestId: "unknown" });
  const settledReplacement = Promise.allSettled([replacement]);
  assert.equal(socket.sent.at(-1)![0]!.method, "operations.cancel");
  client.dispose();
  assert.equal((await settledApplications).length, 32);
  assert.equal((await settledControls).length, 4);
  await settledReplacement;
});

test("mixed batch admission is atomic and application slots never borrow reserved controls", async () => {
  const socket = new FakeSocket();
  const client = new RpcClient(() => socket);
  await client.connect();
  socket.onSend = () => {};
  const pending = Promise.allSettled(client.batch(Array.from({ length: 32 }, () => ({ method: "devboxes.refresh" }))));
  const sends = socket.sent.length;
  assert.throws(() => client.batch([{ method: "operations.cancel" }, { method: "settings.get" }]), /Client request limit/);
  assert.equal(socket.sent.length, sends);
  const control = Promise.allSettled(client.batch([{ method: "system.shutdown" }]));
  client.dispose();
  await Promise.all([pending, control]);
});

test("notify explicitly surfaces serialization, size, and send failures without leaking pending slots", async () => {
  const socket = new FakeSocket();
  const client = new RpcClient(() => socket);
  await client.connect();
  const circular: Record<string, unknown> = {};
  circular.self = circular;
  assert.throws(() => client.notify("settings.update", circular), /Request serialization failed/);
  assert.throws(() => client.notify("settings.update", { synthetic: "x".repeat(1048576) }), /Client message limit/);
  const successfulSend = socket.onSend;
  socket.onSend = () => { throw new Error("synthetic transport failure"); };
  assert.throws(() => client.notify("settings.update"), /Transport send failed; commit state unknown/);
  assert.throws(() => client.batch([
    { method: "settings.get" }, { method: "settings.get", notification: true },
  ]), /Transport send failed/);
  await assert.rejects(client.request("settings.get"), /Transport send failed/);
  socket.onSend = successfulSend;
  const responses = await Promise.all(client.batch(Array.from({ length: 32 }, () => ({ method: "system.getStatus" }))));
  assert.equal(responses.length, 32);
  client.dispose();
});
