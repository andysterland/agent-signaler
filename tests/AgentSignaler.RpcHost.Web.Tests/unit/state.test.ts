import assert from "node:assert/strict";
import { test } from "node:test";
import { paginatedView, revision, RpcError, snapshotView } from "../client/state.ts";
import type { Clock, Page, Snapshot } from "../client/state.ts";

const firstHost = "11111111-1111-4111-8111-111111111111";
const secondHost = "22222222-2222-4222-8222-222222222222";
const flush = () => new Promise<void>(resolve => setImmediate(resolve));
const snapshot = <T>(domain: string, rev: string, state: T, host = firstHost): Snapshot<T> =>
  ({ protocolVersion: 1, hostInstanceId: host, domain, revision: rev, state, isStale: false });
const invalidation = (domain: string, rev: string, machineId?: string) =>
  ({ hostInstanceId: firstHost, domain, revision: rev, ...(machineId ? { machineId } : {}) });

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

class FakeClock implements Clock {
  time = 0;
  scheduled = new Map<() => void, number>();
  now() { return this.time; }
  schedule(callback: () => void, delay: number) {
    this.scheduled.set(callback, this.time + delay);
    return () => { this.scheduled.delete(callback); };
  }
  advance(milliseconds: number) {
    this.time += milliseconds;
    for (const [callback, due] of this.scheduled) {
      if (due <= this.time) {
        this.scheduled.delete(callback);
        callback();
      }
    }
  }
}

test("revisions stay exact above JavaScript safe integers and reject numeric/malformed revisions", () => {
  assert.equal(revision("9007199254740993"), 9007199254740993n);
  assert.ok(revision("9007199254740993") > revision("9007199254740992"));
  for (const value of ["01", "-1", "1e3", "1.0", "", "18446744073709551616", 42]) {
    assert.throws(() => revision(value as string), /Invalid revision/);
  }
});

test("notification before response coalesces high watermark and permits one refetch in flight", async () => {
  const pending: ReturnType<typeof deferred<Snapshot<string>>>[] = [];
  const view = snapshotView("machines", () => {
    const request = deferred<Snapshot<string>>();
    pending.push(request);
    return request.promise;
  });
  view.reconnect(firstHost);
  const flight = view.refresh();
  view.invalidate(invalidation("machines", "9007199254740993"));
  view.invalidate(invalidation("machines", "9007199254740992"));
  assert.strictEqual(view.refresh(), flight);
  assert.equal(pending.length, 1);
  pending[0]!.resolve(snapshot("machines", "9007199254740992", "old"));
  await flush();
  assert.equal(view.data, undefined);
  assert.equal(pending.length, 2);
  pending[1]!.resolve(snapshot("machines", "9007199254740993", "new"));
  await flight;
  assert.equal(view.data, "new");
  assert.equal(view.stale, false);
  view.dispose();
});

test("notification after response dirties only its independent domain or entity", async () => {
  let calls = 0;
  const machines = snapshotView("machines", async () => snapshot("machines", String(++calls), calls));
  const settings = snapshotView("settings", async () => snapshot("settings", "999", "settings"));
  let entityCalls = 0;
  const entity = snapshotView("machines", async () => snapshot("machines", String(++entityCalls), entityCalls), "machine-a");
  for (const view of [machines, settings, entity]) view.reconnect(firstHost);
  await Promise.all([machines.refresh(), settings.refresh(), entity.refresh()]);
  for (const view of [machines, settings, entity]) view.invalidate(invalidation("machines", "2"));
  assert.equal(machines.stale, true);
  assert.equal(settings.stale, false);
  assert.equal(entity.stale, false);
  await machines.refresh();
  assert.equal(machines.data, 2);
  entity.invalidate(invalidation("machines", "2", "machine-b"));
  assert.equal(entityCalls, 1);
  entity.invalidate(invalidation("machines", "2", "machine-a"));
  await entity.refresh();
  assert.equal(entity.data, 2);
  for (const view of [machines, settings, entity]) view.dispose();
});

test("reconnect same instance always refetches, new instance clears old watermarks", async () => {
  let host = firstHost;
  let rev = "9007199254740993";
  let calls = 0;
  const view = snapshotView("settings", async () => snapshot("settings", rev, ++calls, host));
  view.reconnect(host);
  await view.refresh();
  view.reconnect(host);
  await view.refresh();
  assert.equal(calls, 2);
  host = secondHost;
  rev = "1";
  view.reconnect(host);
  assert.equal(view.data, undefined);
  await view.refresh();
  assert.equal(view.revision, "1");
  assert.equal(view.stale, false);
  view.invalidate(invalidation("settings", "9999999999999999"));
  assert.equal(view.stale, false);
  view.dispose();
});

test("late response from replaced connection never overwrites a new host view", async () => {
  const old = deferred<Snapshot<string>>();
  let calls = 0;
  const view = snapshotView("system", () => ++calls === 1
    ? old.promise : Promise.resolve(snapshot("system", "1", "new host", secondHost)));
  view.reconnect(firstHost);
  view.reconnect(secondHost);
  assert.equal(calls, 1);
  old.resolve(snapshot("system", "9999", "old host"));
  await flush();
  assert.equal(view.data, "new host");
  assert.equal(calls, 2);
  view.dispose();
});

test("late older snapshot cannot replace a newer complete view", async () => {
  let rev = "10";
  const view = snapshotView("settings", async () => snapshot("settings", rev, rev));
  view.reconnect(firstHost);
  await view.refresh();
  rev = "9";
  await view.refresh();
  assert.equal(view.data, "10");
  assert.equal(view.revision, "10");
  assert.equal(view.stale, true);
  view.dispose();
});

test("disconnect retains stale complete data without querying until reconnect", async () => {
  let calls = 0;
  const view = snapshotView("settings", async () => snapshot("settings", String(++calls), "complete"));
  view.reconnect(firstHost);
  await view.refresh();
  view.disconnect();
  assert.equal(view.data, "complete");
  assert.equal(view.stale, true);
  await view.refresh();
  assert.equal(calls, 1);
  view.reconnect(firstHost);
  await view.refresh();
  assert.equal(calls, 2);
  assert.equal(view.stale, false);
  view.dispose();
});

test("paginated view publishes atomically and subsequent pages carry expectedRevision", async () => {
  const last = deferred<Snapshot<Page<string>>>();
  const offsets: number[] = [];
  const view = paginatedView("machines", async params => {
    offsets.push(params.offset);
    if (params.offset === 0) {
      assert.equal(params.expectedRevision, undefined);
      return snapshot("machines", "9007199254740993", { items: ["first"], nextOffset: 1 });
    }
    assert.equal(params.expectedRevision, "9007199254740993");
    return last.promise;
  });
  view.reconnect(firstHost);
  await flush();
  assert.deepEqual(offsets, [0, 1]);
  assert.equal(view.data, undefined);
  last.resolve(snapshot("machines", "9007199254740993", { items: ["last"], nextOffset: null }));
  await view.refresh();
  assert.deepEqual(view.data, ["first", "last"]);
  assert.ok(Object.isFrozen(view.data));
  view.dispose();
});

test("three pagination churn restarts retain stale last complete view then wait one second for demand", async () => {
  const timer = new FakeClock();
  let churn = false;
  let starts = 0;
  const view = paginatedView("machines", async params => {
    if (params.offset === 0) starts++;
    if (!churn) return snapshot("machines", String(starts), { items: ["complete"], nextOffset: null });
    if (params.offset === 0) return snapshot("machines", String(starts), { items: ["partial"], nextOffset: 1 });
    throw new RpcError(1004);
  }, undefined, timer);
  view.reconnect(firstHost);
  await view.refresh();
  churn = true;
  view.invalidate(invalidation("machines", "2"));
  await view.refresh();
  assert.equal(starts, 4);
  assert.deepEqual(view.data, ["complete"]);
  assert.equal(view.stale, true);
  timer.advance(999);
  await flush();
  assert.equal(starts, 4);
  await view.refresh();
  assert.equal(timer.scheduled.size, 1);
  churn = false;
  timer.advance(1);
  await flush();
  assert.equal(starts, 5);
  assert.equal(view.stale, false);
  view.dispose();
});

test("invalidation during a page walk discards the completed older walk and restarts at offset zero", async () => {
  const oldTail = deferred<Snapshot<Page<string>>>();
  let starts = 0;
  const view = paginatedView("machines", async params => {
    if (params.offset !== 0) return oldTail.promise;
    starts++;
    return starts === 1
      ? snapshot("machines", "1", { items: ["old-head"], nextOffset: 1 })
      : snapshot("machines", "2", { items: ["new-complete"], nextOffset: null });
  });
  view.reconnect(firstHost);
  await flush();
  view.invalidate(invalidation("machines", "2"));
  assert.equal(starts, 1);
  oldTail.resolve(snapshot("machines", "1", { items: ["old-tail"], nextOffset: null }));
  await view.refresh();
  assert.equal(starts, 2);
  assert.deepEqual(view.data, ["new-complete"]);
  assert.equal(view.revision, "2");
  view.dispose();
});

test("churn does not automatically poll; next invalidation after cooldown triggers a retry", async () => {
  const timer = new FakeClock();
  let calls = 0;
  let fail = true;
  const view = paginatedView("machines", async () => {
    calls++;
    if (fail) throw new RpcError(1004);
    return snapshot("machines", "5", { items: ["recovered"], nextOffset: null });
  }, undefined, timer);
  view.reconnect(firstHost);
  await view.refresh();
  assert.equal(calls, 3);
  timer.advance(5000);
  await flush();
  assert.equal(calls, 3);
  fail = false;
  view.invalidate(invalidation("machines", "5"));
  await view.refresh();
  assert.equal(calls, 4);
  assert.deepEqual(view.data, ["recovered"]);
  view.dispose();
});

test("page revision mismatch discards partial assembly even if server omitted stale error", async () => {
  let requests = 0;
  const view = paginatedView("machines", async params => {
    requests++;
    return snapshot("machines", params.offset === 0 ? "1" : "2",
      { items: ["partial"], nextOffset: params.offset === 0 ? 1 : null });
  });
  view.reconnect(firstHost);
  await view.refresh();
  assert.equal(requests, 6);
  assert.equal(view.data, undefined);
  assert.equal(view.stale, true);
  view.dispose();
});

test("non-stale errors and malformed offsets do not retry automatically", async () => {
  let calls = 0;
  const denied = snapshotView("settings", async () => { calls++; throw new RpcError(1009); });
  const malformed = paginatedView("machines", async () =>
    snapshot("machines", "1", { items: ["partial"], nextOffset: 0 }));
  denied.reconnect(firstHost);
  malformed.reconnect(firstHost);
  await Promise.all([denied.refresh(), malformed.refresh()]);
  assert.equal(calls, 1);
  assert.ok(denied.error instanceof RpcError);
  assert.equal(malformed.data, undefined);
  assert.match(String(malformed.error), /Invalid page offset/);
  denied.dispose();
  malformed.dispose();
});

test("server-stale snapshots remain visibly stale and explicit null nextOffset completes the page", async () => {
  const view = paginatedView("machines", async () =>
    ({ ...snapshot("machines", "1", { items: ["last-good"], nextOffset: null }), isStale: true }));
  view.reconnect(firstHost);
  await view.refresh();
  assert.deepEqual(view.data, ["last-good"]);
  assert.equal(view.stale, true);
  view.dispose();
});
