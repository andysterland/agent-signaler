import { test as base, expect } from "@playwright/test";
import { spawn } from "node:child_process";
import type { ChildProcess } from "node:child_process";
import { createServer } from "node:http";
import type { Server } from "node:http";
import { createServer as createTcpServer } from "node:net";
import { mkdir, readFile, rm, stat, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { randomUUID } from "node:crypto";
import ts from "typescript";

export const projectDirectory = fileURLToPath(new URL("..", import.meta.url));
const repositoryDirectory = path.resolve(projectDirectory, "..", "..");

function requireNetworkGate(): void {
  if (process.env.AGENT_SIGNALER_RPC_WEB_NETWORK_TESTS !== "1") {
    throw new Error("Listener tests require AGENT_SIGNALER_RPC_WEB_NETWORK_TESTS=1; run only in the final network-test phase.");
  }
}

async function listen(server: Server): Promise<number> {
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => { server.off("error", reject); resolve(); });
  });
  const address = server.address();
  if (!address || typeof address === "string") throw new Error("Fixture bind failed");
  return address.port;
}

async function reservePorts(): Promise<[number, number]> {
  const servers = [createTcpServer(), createTcpServer()];
  const ports: number[] = [];
  try {
    for (const server of servers) {
      await new Promise<void>((resolve, reject) => {
        server.once("error", reject);
        server.listen(0, "127.0.0.1", () => { server.off("error", reject); resolve(); });
      });
      const address = server.address();
      if (!address || typeof address === "string") throw new Error("Fixture port reservation failed");
      ports.push(address.port);
    }
    return [ports[0]!, ports[1]!];
  } finally {
    await Promise.all(servers.map(server => new Promise<void>(resolve => server.close(() => resolve()))));
  }
}

export interface Ready {
  rpcPort: number;
  protocolVersion: number;
  hostInstanceId: string;
}

export class HostFixture {
  readonly directory: string;
  readonly rpcPort: number;
  readonly receiverPort: number;
  private child: ChildProcess | undefined;
  private readonly executable: string;
  ready: Ready | undefined;

  private constructor(directory: string, executable: string, ports: [number, number]) {
    this.directory = directory;
    this.executable = executable;
    [this.rpcPort, this.receiverPort] = ports;
  }

  get url(): string { return `ws://127.0.0.1:${this.rpcPort}/rpc`; }

  static async create(): Promise<HostFixture> {
    requireNetworkGate();
    const executable = path.resolve(process.env.AGENT_SIGNALER_RPC_HOST_EXE ??
      path.join(repositoryDirectory, "artifacts", "publish", "RpcHost", "AgentSignaler.RpcHost.exe"));
    if (!(await stat(executable)).isFile()) throw new Error("Publish RpcHost before running browser interoperability tests");
    const ports = await reservePorts();
    const directory = path.join(projectDirectory, ".fixtures", randomUUID());
    await mkdir(directory, { recursive: true });
    const fixture = new HostFixture(directory, executable, ports);
    try {
      await writeFile(path.join(directory, "dashboard-settings.json"), JSON.stringify({
        Port: fixture.receiverPort,
        RpcPort: fixture.rpcPort,
        ConnectionMode: 1,
        AutoStartSharing: false,
        ReceiveDetailedConversations: false,
        AzureCliPath: path.join(directory, "synthetic", "az.exe"),
        DevTunnelCliPath: path.join(directory, "synthetic", "devtunnel.exe"),
      }));
      await fixture.start();
      return fixture;
    } catch (error) {
      await fixture.dispose();
      throw error;
    }
  }

  async start(): Promise<void> {
    if (this.child) throw new Error("Fixture already running");
    const child = spawn(this.executable, ["--data-directory", this.directory, "--rpc-port", String(this.rpcPort)], {
      cwd: this.directory,
      windowsHide: true,
      stdio: ["ignore", "pipe", "pipe"],
      env: {
        ...process.env,
        AGENT_SIGNALER_DATA_DIR: this.directory,
        AGENT_SIGNALER_LIVE_TUNNEL_TEST: "0",
        DOTNET_BUNDLE_EXTRACT_BASE_DIR: path.join(this.directory, "bundle-cache"),
      },
    });
    this.child = child;
    // Do not forward raw host output into browser traces, reports, or console logs.
    child.stderr?.resume();
    this.ready = await new Promise<Ready>((resolve, reject) => {
      let text = "";
      const timeout = setTimeout(() => finish(new Error("Fixture transport-ready watchdog expired")), 30000);
      const exited = () => finish(new Error("Fixture exited before transport readiness"));
      const failed = () => finish(new Error("Fixture could not start"));
      const finish = (error?: Error, ready?: Ready) => {
        clearTimeout(timeout);
        child.off("exit", exited);
        child.off("error", failed);
        child.stdout?.off("data", data);
        child.stdout?.resume();
        if (error) reject(error);
        else resolve(ready!);
      };
      const data = (chunk: Buffer) => {
        text += chunk.toString("utf8");
        if (text.length > 16384) { finish(new Error("Fixture readiness output exceeded limit")); return; }
        const newline = text.indexOf("\n");
        if (newline < 0) return;
        try {
          const ready = JSON.parse(text.slice(0, newline)) as Ready;
          if (ready.rpcPort !== this.rpcPort || ready.protocolVersion !== 1 ||
              typeof ready.hostInstanceId !== "string") throw new Error("Invalid readiness");
          finish(undefined, ready);
        } catch { finish(new Error("Fixture readiness contract mismatch")); }
      };
      child.once("exit", exited);
      child.once("error", failed);
      child.stdout?.on("data", data);
    });
  }

  async waitForExit(): Promise<number | null> {
    const child = this.child;
    if (!child) return null;
    if (child.exitCode !== null || child.signalCode !== null) return child.exitCode;
    return await new Promise<number | null>((resolve, reject) => {
      const timeout = setTimeout(() => {
        child.off("exit", exited);
        reject(new Error("Fixture shutdown watchdog expired"));
      }, 30000);
      const exited = (code: number | null) => { clearTimeout(timeout); resolve(code); };
      child.once("exit", exited);
    });
  }

  async stop(): Promise<void> {
    const child = this.child;
    if (!child) return;
    if (child.pid !== undefined && child.exitCode === null && child.signalCode === null) {
      // The exact subprocess created by this fixture, never an image-name or port-based kill.
      child.kill();
      await this.waitForExit();
    }
    this.child = undefined;
  }

  async dispose(): Promise<void> {
    await this.stop();
    await rm(this.directory, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
}

export const test = base.extend<{ site: string; host: HostFixture }>({
  site: async ({}, use) => {
    requireNetworkGate();
    const modules = new Map<string, string>();
    for (const name of ["rpc-client", "state"]) {
      const source = await readFile(path.join(projectDirectory, "client", `${name}.ts`), "utf8");
      modules.set(`/client/${name}.ts`, ts.transpileModule(source, {
        compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 },
      }).outputText);
    }
    const server = createServer((request, response) => {
      const module = modules.get(request.url ?? "");
      response.setHeader("Cache-Control", "no-store");
      if (module) {
        response.writeHead(200, { "Content-Type": "text/javascript" });
        response.end(module);
      } else if (request.url === "/") {
        response.writeHead(200, { "Content-Type": "text/html" });
        response.end("<!doctype html><meta charset=utf-8><title>Synthetic RPC contract fixture</title>");
      } else {
        response.writeHead(404);
        response.end();
      }
    });
    try { await use(`http://127.0.0.1:${await listen(server)}`); }
    finally {
      server.closeAllConnections();
      await new Promise<void>(resolve => server.close(() => resolve()));
    }
  },
  host: async ({}, use) => {
    const host = await HostFixture.create();
    try { await use(host); }
    finally { await host.dispose(); }
  },
});

export { expect };
