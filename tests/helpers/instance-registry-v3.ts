import { mkdir, writeFile } from "node:fs/promises";
import { createServer, type IncomingMessage, type Server } from "node:http";
import { join } from "node:path";
import {
  probeProcessObservations,
  type InstanceRegistryV3,
  type RegistryIdentity,
  type RegistrySession,
} from "../../src/instances.js";
import { resolveProjectIdentity } from "../../src/project-identity.js";

export interface StrictHealthServer {
  endpoint: string;
  port: number;
  close(): Promise<void>;
  requests(): number;
}

/** 创建最小 Unity project root，供 CanonicalPath/CWD selector 真实解析。 */
export async function createUnityProject(root: string): Promise<void> {
  await Promise.all([
    mkdir(join(root, "Assets"), { recursive: true }),
    mkdir(join(root, "Packages"), { recursive: true }),
    mkdir(join(root, "ProjectSettings"), { recursive: true }),
  ]);
  await writeFile(join(root, "Packages", "manifest.json"), "{}\n", "utf8");
  await writeFile(join(root, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.62f1\n", "utf8");
}

let currentProcessStartedAtPromise: Promise<string> | undefined;

/** 读取当前 Node 进程的精确 Windows start time；同一测试进程复用一次 OS 证明。 */
export async function currentProcessStartedAt(): Promise<string> {
  currentProcessStartedAtPromise ??= (async () => {
    // 测试夹具允许 PowerShell cold start 重试；产品单次 observation budget 仍固定为 4000ms。
    for (let attempt = 0; attempt < 3; attempt += 1) {
      const observation = (await probeProcessObservations([process.pid])).get(process.pid);
      if (observation?.state === "alive" && observation.processStartedAt) return observation.processStartedAt;
    }
    throw new Error("Current process start time is unavailable.");
  })();
  return currentProcessStartedAtPromise;
}

export function registryIdentity(projectPath: string): RegistryIdentity {
  const identity = resolveProjectIdentity(projectPath);
  return {
    instanceId: identity.instanceId,
    canonicalProjectPath: identity.canonicalProjectPath,
    displayProjectPath: identity.displayProjectPath,
  };
}

export async function makeRegistrySession(
  endpoint: string,
  overrides: Partial<RegistrySession> = {},
): Promise<RegistrySession> {
  const now = new Date().toISOString();
  const url = new URL(endpoint);
  const actualPort = Number(url.port);
  const target = overrides.target ?? "editor";
  const sessionId = overrides.sessionId ?? overrides.editorSessionId ?? overrides.runtimeSessionId ?? `session-${"1".repeat(64)}`;
  return {
    target,
    sessionId,
    ...(target === "editor" ? { editorSessionId: sessionId } : { runtimeSessionId: sessionId }),
    processId: overrides.processId ?? process.pid,
    processStartedAt: overrides.processStartedAt ?? await currentProcessStartedAt(),
    publishedAt: overrides.publishedAt ?? now,
    publicationSequence: overrides.publicationSequence ?? 1,
    lifecycle: overrides.lifecycle ?? "ready",
    lastUnitySnapshot: overrides.lastUnitySnapshot ?? (target === "editor" ? {
      schemaVersion: 1,
      observedAt: now,
      sourceEditorSessionId: sessionId,
      sourceListenerGeneration: overrides.listener?.generation ?? 1,
      fields: {
        compile: { known: false }, import: { known: false }, build: { known: false },
        playMode: { known: false }, prefabStage: { known: false }, activeScene: { known: false }, runtime: { known: false },
      },
    } : {
      schemaVersion: 1,
      observedAt: now,
      sourceRuntimeSessionId: sessionId,
      sourceListenerGeneration: overrides.listener?.generation ?? 1,
      fields: { developmentBuild: { known: true, value: true } },
    }),
    listener: overrides.listener ?? {
      listenerId: `listener-${"2".repeat(32)}`,
      generation: 1,
      startedAt: now,
      endpoint: {
        host: "127.0.0.1",
        preferredPort: actualPort,
        actualPort,
        fallback: false,
        url: endpoint,
      },
    },
    progress: overrides.progress ?? [],
    blockers: overrides.blockers ?? [],
    unityVersion: overrides.unityVersion ?? "2022.3.62f1",
    buildStamp: overrides.buildStamp ?? "test-build",
    toolCatalogFingerprint: overrides.toolCatalogFingerprint ?? "test-tools",
    ...(target === "runtime" ? {
      executablePath: overrides.executablePath ?? process.execPath,
      buildId: overrides.buildId ?? "a".repeat(32),
    } : {}),
  };
}

export function makeRegistry(identity: RegistryIdentity, sessions: RegistrySession[], revision = 1): InstanceRegistryV3 {
  return {
    version: 3,
    revision,
    canonicalizationVersion: 1,
    identity,
    sessions,
    publishedAt: new Date().toISOString(),
  };
}

/** 测试模拟 C# writer，产品 TS 不得复用此函数。 */
export async function writeRegistry(dataDir: string, record: InstanceRegistryV3): Promise<void> {
  const directory = join(dataDir, "instances");
  await mkdir(directory, { recursive: true });
  await writeFile(join(directory, `${record.identity.instanceId}.json`), `${JSON.stringify(record, null, 2)}\n`, "utf8");
}

/** 启动严格v3 health fixture；response identity与main-thread pulse由调用方按session提供。 */
export async function startStrictHealthServer(
  health: (request: IncomingMessage, port: number) => Record<string, unknown>,
): Promise<StrictHealthServer> {
  let requestCount = 0;
  const server: Server = createServer((request, response) => {
    if (request.method === "GET" && request.url === "/health") {
      requestCount += 1;
      const address = server.address();
      const port = address && typeof address === "object" ? address.port : 0;
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify({ ok: true, requestId: "", data: health(request, port) }));
      return;
    }
    response.writeHead(404);
    response.end();
  });
  await new Promise<void>((complete) => server.listen(0, "127.0.0.1", complete));
  const address = server.address();
  if (!address || typeof address !== "object") throw new Error("Health server address is unavailable.");
  return {
    endpoint: `http://127.0.0.1:${address.port}`,
    port: address.port,
    close: () => new Promise<void>((complete, reject) => server.close((error) => error ? reject(error) : complete())),
    requests: () => requestCount,
  };
}
