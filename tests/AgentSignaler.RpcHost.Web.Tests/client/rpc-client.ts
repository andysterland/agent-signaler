import { DomainView, RpcError, revision } from "./state.ts";
import type { Invalidation, Snapshot } from "./state.ts";

type Pending = {
  resolve: (value: unknown) => void;
  reject: (error: unknown) => void;
  cancelTimer: () => void;
  control: boolean;
};

export type Call = { method: string; params?: Record<string, unknown>; notification?: boolean };
type Observer = Pick<DomainView<unknown>, "reconnect" | "invalidate" | "disconnect" | "dispose">;

export interface RpcTransport {
  readonly readyState: number;
  send(text: string): void;
  close(): void;
  onOpen(listener: () => void): void;
  onError(listener: () => void): void;
  onClose(listener: () => void): void;
  onMessage(listener: (data: unknown) => void): void;
}

export interface RpcScheduler {
  schedule(callback: () => void, delay: number): () => void;
}

const scheduler: RpcScheduler = {
  schedule(callback, delay) {
    const timer = setTimeout(callback, delay);
    return () => clearTimeout(timer);
  },
};

function browserTransport(url: string): RpcTransport {
  const socket = new WebSocket(url);
  return {
    get readyState() { return socket.readyState; },
    send: text => socket.send(text),
    close: () => socket.close(),
    onOpen: listener => socket.addEventListener("open", listener, { once: true }),
    onError: listener => socket.addEventListener("error", listener, { once: true }),
    onClose: listener => socket.addEventListener("close", listener, { once: true }),
    onMessage: listener => socket.addEventListener("message", event => listener(event.data)),
  };
}

const controlMethods = new Set([
  "operations.cancel", "system.shutdown", "system.cancelStartup", "sharing.stop",
  "sharing.cancel", "prerequisites.cancel", "devboxes.cancel", "windowsApp.cancel",
]);

export function requestDeadline(method: string): number {
  if (method === "windowsApp.signIn") return 180000;
  if (["devboxes.refresh", "windowsApp.map", "windowsApp.refresh", "windowsApp.open", "sharing.start",
    "sharing.delete", "sharing.logout", "prerequisites.check", "prerequisites.checkAll"].includes(method)) return 300000;
  return 10000;
}

/** Test-only browser example; localhost Origin is not authentication. */
export class RpcClient {
  private socket: RpcTransport | undefined;
  private nextId = 0n;
  private epoch = 0;
  private pending = new Map<string, Pending>();
  private observers = new Map<string, Observer>();
  private buffered = new Map<string, Invalidation>();
  private instance: string | undefined;
  private initialized = false;
  private readonly socketFactory: (url: string) => RpcTransport;
  private readonly scheduler: RpcScheduler;

  constructor(socketFactory: (url: string) => RpcTransport = browserTransport, timer: RpcScheduler = scheduler) {
    this.socketFactory = socketFactory;
    this.scheduler = timer;
  }

  watch<T>(view: DomainView<T>): DomainView<T> {
    const key = JSON.stringify([view.domain, view.machineId ?? null]);
    if (this.observers.has(key)) throw new Error("Domain/entity already watched");
    if (this.observers.size >= 64) throw new Error("Client view limit");
    this.observers.set(key, view);
    if (this.initialized && this.instance) view.reconnect(this.instance);
    return view;
  }

  async connect(url = "ws://localhost:51821/rpc"): Promise<void> {
    this.disconnect();
    const epoch = this.epoch;
    const socket = this.socketFactory(url);
    this.socket = socket;
    await new Promise<void>((resolve, reject) => {
      const cancelTimer = this.scheduler.schedule(() => {
        socket.close();
        reject(new Error("Connect timeout"));
      }, 10000);
      socket.onOpen(() => { cancelTimer(); resolve(); });
      socket.onError(() => { cancelTimer(); reject(new Error("Transport unavailable")); });
      socket.onClose(() => {
        cancelTimer();
        reject(new Error("Transport closed"));
        if (epoch === this.epoch) this.rejectPending();
      });
      // Subscribe before sending the first query, including notifications racing its reply.
      socket.onMessage(data => {
        if (epoch !== this.epoch) return;
        try {
          if (typeof data !== "string" || new TextEncoder().encode(data).byteLength > 4 * 1048576) {
            throw new Error("Invalid inbound message");
          }
          this.receive(JSON.parse(data));
        } catch {
          this.disconnect();
        }
      });
    });
    if (epoch !== this.epoch) throw new Error("Connection replaced");
    let status: Snapshot<unknown>;
    try {
      status = await this.request<Snapshot<unknown>>("system.getStatus");
      if (epoch !== this.epoch) throw new Error("Connection replaced");
      if (status.protocolVersion !== 1 || status.domain !== "system" ||
          typeof status.isStale !== "boolean" ||
          typeof status.hostInstanceId !== "string" ||
          !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(status.hostInstanceId)) {
        throw new Error("Unsupported snapshot identity");
      }
      revision(status.revision);
    } catch (error) {
      if (epoch === this.epoch) this.disconnect();
      throw error;
    }
    this.instance = status.hostInstanceId;
    this.initialized = true;
    for (const observer of this.observers.values()) observer.reconnect(this.instance);
    for (const event of this.buffered.values()) this.invalidate(event);
    this.buffered.clear();
  }

  request<T>(method: string, params?: Record<string, unknown>): Promise<T> {
    try {
      return this.batch([{ method, ...(params ? { params } : {}) }])[0] as Promise<T>;
    } catch (error) {
      return Promise.reject(error);
    }
  }

  notify(method: string, params?: Record<string, unknown>): void {
    this.batch([{ method, ...(params ? { params } : {}), notification: true }]);
  }

  /** Batch entries have no execution-order guarantee; mutations are never retried. */
  batch(calls: readonly Call[]): Promise<unknown>[] {
    if (!this.socket || this.socket.readyState !== 1) throw new Error("Not connected");
    if (calls.length < 1 || calls.length > 32) throw new Error("Client request limit");
    let applicationCount = 0;
    let controlCount = 0;
    for (const pending of this.pending.values()) {
      if (pending.control) controlCount++;
      else applicationCount++;
    }
    for (const call of calls) {
      if (controlMethods.has(call.method)) controlCount++;
      else applicationCount++;
    }
    if (applicationCount > 32 || controlCount > 4) throw new Error("Client request limit");
    const promises: Promise<unknown>[] = [];
    const ids: string[] = [];
    const messages = calls.map(call => {
      const message = { jsonrpc: "2.0", method: call.method, ...(call.params ? { params: call.params } : {}) };
      if (call.notification) return message;
      const id = `web-${++this.nextId}`;
      ids.push(id);
      return { ...message, id };
    });
    let text: string;
    try {
      text = JSON.stringify(messages.length === 1 ? messages[0] : messages);
    } catch {
      throw new Error("Request serialization failed");
    }
    if (new TextEncoder().encode(text).byteLength > 1048576) throw new Error("Client message limit");
    for (const message of messages) {
      if (!("id" in message)) continue;
      const id = message.id;
      promises.push(new Promise((resolve, reject) => {
        const cancelTimer = this.scheduler.schedule(() => {
          this.pending.delete(id);
          reject(new Error("Request timeout; commit state unknown"));
        }, requestDeadline(message.method));
        this.pending.set(id, { resolve, reject, cancelTimer, control: controlMethods.has(message.method) });
      }));
    }
    try {
      this.socket.send(text);
    } catch {
      const error = new Error("Transport send failed; commit state unknown");
      // These promises cannot be returned when batch throws, but still release every reserved slot.
      for (const promise of promises) void promise.catch(() => {});
      for (const id of ids) this.settle(id, undefined, error);
      throw error;
    }
    return promises;
  }

  private receive(message: unknown): void {
    if (Array.isArray(message)) {
      if (message.length < 1 || message.length > 32) throw new Error("Invalid response batch");
      for (const entry of message) this.receive(entry);
      return;
    }
    if (!message || typeof message !== "object") throw new Error("Invalid RPC message");
    const value = message as Record<string, unknown>;
    if (value.jsonrpc !== "2.0") throw new Error("Invalid RPC version");
    if (!Object.hasOwn(value, "id") && typeof value.method === "string") {
      if (value.method.endsWith(".changed")) {
        const event = value.params as Invalidation;
        revision(event?.revision);
        if (typeof event.hostInstanceId !== "string" || typeof event.domain !== "string" ||
            (event.machineId !== undefined && typeof event.machineId !== "string")) throw new Error("Invalid invalidation");
        if (this.initialized) this.invalidate(event);
        else {
          const key = JSON.stringify([event.hostInstanceId, event.domain, event.machineId ?? null]);
          const previous = this.buffered.get(key);
          if (!previous && this.buffered.size >= 64) throw new Error("Client invalidation limit");
          if (!previous || revision(previous.revision) < revision(event.revision)) this.buffered.set(key, event);
        }
      }
      return;
    }
    if (typeof value.id !== "string" || Object.hasOwn(value, "result") === Object.hasOwn(value, "error")) {
      throw new Error("Invalid response");
    }
    if (Object.hasOwn(value, "error")) {
      const error = value.error as Record<string, unknown>;
      if (!error || !Number.isInteger(error.code)) throw new Error("Invalid RPC error");
      this.settle(value.id, undefined, new RpcError(error.code as number, error.data));
    } else this.settle(value.id, value.result);
  }

  private invalidate(event: Invalidation): void {
    if (event.hostInstanceId !== this.instance) return;
    for (const observer of this.observers.values()) observer.invalidate(event);
  }

  private settle(id: string, result?: unknown, error?: unknown): void {
    const pending = this.pending.get(id);
    if (!pending) return;
    this.pending.delete(id);
    pending.cancelTimer();
    if (error !== undefined) pending.reject(error);
    else pending.resolve(result);
  }

  private rejectPending(): void {
    this.initialized = false;
    for (const observer of this.observers.values()) observer.disconnect();
    for (const id of this.pending.keys()) this.settle(id, undefined, new Error("Disconnected; commit state unknown"));
  }

  disconnect(): void {
    this.epoch++;
    this.socket?.close();
    this.socket = undefined;
    this.buffered.clear();
    this.rejectPending();
  }

  dispose(): void {
    this.disconnect();
    for (const observer of this.observers.values()) observer.dispose();
    this.observers.clear();
  }
}
