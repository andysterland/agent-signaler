export interface Snapshot<T> {
  protocolVersion: number;
  hostInstanceId: string;
  domain: string;
  revision: string;
  state: T;
  isStale: boolean;
}

export interface Invalidation {
  hostInstanceId: string;
  domain: string;
  revision: string;
  machineId?: string;
}

export interface Page<T> {
  items: T[];
  nextOffset: number | null;
}

export interface PageQuery {
  offset: number;
  limit: number;
  expectedRevision?: string;
}

export class RpcError extends Error {
  readonly code: number;
  readonly data: unknown;

  constructor(code: number, data?: unknown) {
    // Do not copy remote error text, paths, or payloads into diagnostics.
    super(`RPC error ${code}`);
    this.code = code;
    this.data = data;
  }
}

export function revision(value: string): bigint {
  if (typeof value !== "string" || !/^(0|[1-9][0-9]{0,19})$/.test(value)) {
    throw new Error("Invalid revision");
  }
  const result = BigInt(value);
  if (result > 18446744073709551615n) throw new Error("Invalid revision");
  return result;
}

export interface Clock {
  now(): number;
  schedule(callback: () => void, delay: number): () => void;
}

const clock: Clock = {
  now: () => performance.now(),
  schedule: (callback, delay) => {
    const timer = setTimeout(callback, delay);
    return () => clearTimeout(timer);
  },
};

interface Loaded<T> {
  snapshot: Snapshot<T>;
}

/** One displayed domain/entity. Partial page assemblies are never published. */
export class DomainView<T> {
  readonly domain: string;
  readonly machineId: string | undefined;
  data: T | undefined;
  revision: string | undefined;
  stale = true;
  error: unknown;
  private watermark = 0n;
  private hostInstanceId: string | undefined;
  private epoch = 0;
  private dirty = true;
  private flight: Promise<void> | undefined;
  private cooldownUntil = 0;
  private cancelTimer: (() => void) | undefined;
  private disposed = false;
  private connected = false;
  private readonly load: () => Promise<Loaded<T>>;
  private readonly clock: Clock;

  constructor(domain: string, load: () => Promise<Loaded<T>>, machineId?: string, timer: Clock = clock) {
    this.domain = domain;
    this.machineId = machineId;
    this.load = load;
    this.clock = timer;
  }

  reconnect(hostInstanceId: string): void {
    if (this.disposed) return;
    if (this.hostInstanceId !== hostInstanceId) {
      this.watermark = 0n;
      this.revision = undefined;
      this.data = undefined;
    }
    this.hostInstanceId = hostInstanceId;
    this.connected = true;
    this.epoch++;
    this.cooldownUntil = 0;
    this.dirty = this.stale = true;
    void this.refresh();
  }

  invalidate(event: Invalidation): void {
    if (event.hostInstanceId !== this.hostInstanceId || event.domain !== this.domain ||
        event.machineId !== this.machineId || this.disposed || !this.connected) return;
    const value = revision(event.revision);
    if (value <= this.watermark && this.revision !== undefined && value <= revision(this.revision)) return;
    this.watermark = value > this.watermark ? value : this.watermark;
    if (this.revision !== undefined && value <= revision(this.revision)) return;
    this.dirty = this.stale = true;
    void this.refresh();
  }

  /** Demand during cooldown schedules one retry, not an unbounded polling loop. */
  refresh(): Promise<void> {
    if (this.disposed || !this.connected || !this.hostInstanceId) return Promise.resolve();
    if (this.flight) return this.flight;
    const delay = this.cooldownUntil - this.clock.now();
    if (delay > 0) {
      this.cancelTimer ??= this.clock.schedule(() => {
        this.cancelTimer = undefined;
        void this.refresh();
      }, delay);
      return Promise.resolve();
    }
    this.cancelTimer?.();
    this.cancelTimer = undefined;
    const epoch = this.epoch;
    this.flight = this.fetch(epoch).finally(() => {
      this.flight = undefined;
      if (epoch !== this.epoch && !this.disposed) void this.refresh();
    });
    return this.flight;
  }

  private async fetch(epoch: number): Promise<void> {
    for (let attempt = 0; attempt < 3 && !this.disposed; attempt++) {
      this.dirty = false;
      try {
        const { snapshot } = await this.load();
        if (epoch !== this.epoch || this.disposed) return;
        if (snapshot.protocolVersion !== 1 || snapshot.domain !== this.domain ||
            snapshot.hostInstanceId !== this.hostInstanceId || typeof snapshot.isStale !== "boolean") {
          throw new Error("Snapshot identity mismatch");
        }
        const value = revision(snapshot.revision);
        // A completed but already invalidated response must not replace the last good view.
        if (value < this.watermark || (this.revision !== undefined && value < revision(this.revision))) {
          this.dirty = this.stale = true;
          continue;
        }
        this.data = snapshot.state;
        this.revision = snapshot.revision;
        this.watermark = value > this.watermark ? value : this.watermark;
        this.stale = snapshot.isStale === true;
        this.dirty = false;
        this.error = undefined;
        return;
      } catch (error) {
        if (epoch !== this.epoch || this.disposed) return;
        this.stale = true;
        this.error = error;
        if (!(error instanceof RpcError && error.code === 1004)) return;
        this.dirty = true;
      }
    }
    this.stale = true;
    this.cooldownUntil = this.clock.now() + 1000;
  }

  dispose(): void {
    this.disposed = true;
    this.disconnect();
  }

  disconnect(): void {
    this.connected = false;
    this.stale = true;
    this.epoch++;
    this.cancelTimer?.();
    this.cancelTimer = undefined;
  }
}

export function snapshotView<T>(
  domain: string, query: () => Promise<Snapshot<T>>, machineId?: string, timer?: Clock,
): DomainView<T> {
  return new DomainView(domain, async () => ({ snapshot: await query() }), machineId, timer);
}

export function paginatedView<T>(
  domain: string, query: (params: PageQuery) => Promise<Snapshot<Page<T>>>, machineId?: string, timer?: Clock,
): DomainView<readonly T[]> {
  return new DomainView(domain, async () => {
    let first: Snapshot<Page<T>> | undefined;
    let offset = 0;
    const items: T[] = [];
    // Bound a faulty peer as well as legitimate server collections.
    for (let pages = 0; pages < 4096; pages++) {
      const response = await query({
        offset, limit: 250, ...(first ? { expectedRevision: first.revision } : {}),
      });
      revision(response.revision);
      if (first && (response.revision !== first.revision ||
          response.hostInstanceId !== first.hostInstanceId || response.domain !== first.domain)) {
        throw new RpcError(1004);
      }
      if (response.protocolVersion !== 1 || typeof response.isStale !== "boolean" || !Array.isArray(response.state.items) ||
          response.state.items.length > 250) throw new Error("Invalid page");
      first ??= response;
      items.push(...response.state.items);
      if (items.length > 100000) throw new Error("Client collection limit");
      if (response.state.nextOffset === null) {
        return { snapshot: { ...first, state: Object.freeze(items) } };
      }
      if (!Number.isSafeInteger(response.state.nextOffset) || response.state.nextOffset <= offset ||
          response.state.nextOffset !== offset + response.state.items.length) throw new Error("Invalid page offset");
      offset = response.state.nextOffset;
    }
    throw new Error("Client page limit");
  }, machineId, timer);
}
