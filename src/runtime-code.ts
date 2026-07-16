import { createHash } from "node:crypto";
import { constants } from "node:fs";
import { access, mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, relative, resolve } from "node:path";
import { stdin } from "node:process";
import { executeBridgePayload, type BridgeRuntime } from "./bridge/index.js";
import { discoverUnity } from "./platform.js";
import type { CliResult } from "./types.js";
import { ensurePackageBridge } from "./package-manager.js";
import type { JobRunner } from "./job-runner.js";
import { summarizeJob } from "./cli-commands/unity-job.js";
import { StructuredToolError } from "./tools/errors.js";

export interface RuntimeCodeRuntime extends BridgeRuntime {
  dataDir: string;
  runner?: JobRunner;
}

export interface RuntimeCodeRequest {
  file?: string;
  stdin?: boolean;
  code?: string;
  channel?: string;
  refs?: string[];
  define?: string[];
  args?: Record<string, unknown>;
  timeoutMs?: number;
  artifactDir?: string;
  project?: string;
  unity?: string;
  job?: boolean;
  wait?: boolean;
  waitTimeoutMs?: number;
  bridgeEndpoint?: string;
  target?: string;
  instance?: string;
  retry?: number;
  dryRun?: boolean;
}

interface CodeSource {
  kind: "file" | "stdin" | "inline";
  path?: string;
  text: string;
  sha256: string;
  sizeBytes: number;
}

const liveDefaultTimeoutMs = 60_000;
const batchDefaultTimeoutMs = 300_000;

export async function executeRuntimeCode(request: RuntimeCodeRequest, runtime: RuntimeCodeRuntime, requestId: string): Promise<Record<string, unknown>> {
  const source = await readCodeSource(request);
  const channel = normalizeChannel(request.channel);
  if (!channel.ok) {
    throw new StructuredToolError(channel.code, channel.message, ["Pass --channel live or --channel batchmode."]);
  }

  const timeoutMs = normalizeTimeout(request.timeoutMs, channel.value);
  const artifactDir = resolve(request.artifactDir ?? runtimeArtifactDir(runtime.dataDir, requestId));
  const projectPath = resolve(request.project ?? process.cwd());
  const parsedArgs = request.args ?? {};
  const refs = await validateExtraReferences(request.refs);

  const payload = {
    command: "code.exec",
    sourceKind: source.kind,
    sourcePath: source.path,
    sourceText: source.text,
    sourceSha256: source.sha256,
    sourceSizeBytes: source.sizeBytes,
    channel: channel.value,
    timeoutMs,
    artifactDir,
    projectPath,
    argsJson: JSON.stringify(parsedArgs),
    defines: request.define?.join(",") ?? "",
    refs
  };

  if (request.dryRun) {
    return {
      command: "code.exec",
      dryRun: true,
      preview: {
        sourceKind: source.kind,
        ...(source.path ? { sourcePath: source.path } : {}),
        sourceSha256: source.sha256,
        sourceSizeBytes: source.sizeBytes,
        channel: channel.value,
        timeoutMs,
        artifactDir,
        projectPath,
        args: parsedArgs
      },
      plannedEffects: [{ type: "code.exec", channel: channel.value, artifactDir, sideEffects: "user-code-not-executed" }]
    };
  }

  const sourceArtifactResult = await writeSourceArtifact(artifactDir, source);
  if (!sourceArtifactResult.ok) {
    throw new StructuredToolError("E_ARTIFACT_WRITE_FAILED", `Failed to write Runtime Code source artifact: ${sourceArtifactResult.message}`, ["Check --artifact-dir permissions and free disk space."], { artifactDir, artifactPath: sourceArtifactResult.path });
  }

  const manifest = manifestData(artifactDir, requestId, undefined, projectPath, source, channel.value, timeoutMs, parsedArgs);
  const manifestResult = await writeManifest(artifactDir, manifest);
  if (!manifestResult.ok) {
    throw new StructuredToolError("E_ARTIFACT_WRITE_FAILED", `Failed to write Runtime Code artifact manifest: ${manifestResult.message}`, ["Check --artifact-dir permissions and free disk space."], { artifactDir, manifestPath: resolve(artifactDir, "manifest.json") });
  }

  if (channel.value === "live" && !request.job) {
    const result = await executeBridgePayload(requestId, "code.exec", payload, {
      "bridge-endpoint": request.bridgeEndpoint ?? runtime.bridgeEndpoint ?? "",
      instance: request.instance ?? "",
      target: request.target ?? "",
      "timeout-ms": String(timeoutMs),
      retry: request.retry === undefined ? "0" : String(request.retry),
      __destructiveConfirmed: true
    }, runtime);
    if (!result.ok) throwCliFailure(result);
    return (result.data ?? {}) as Record<string, unknown>;
  }

  return startBatchCodeJob(request, runtime, requestId, projectPath, source, artifactDir, timeoutMs, payload);
}

async function readCodeSource(request: RuntimeCodeRequest): Promise<CodeSource> {
  const selected = [request.file !== undefined, request.code !== undefined, request.stdin === true].filter(Boolean).length;
  if (selected !== 1) {
    throw new StructuredToolError("E_CODE_SOURCE_INVALID", "Pass exactly one code source: --file <path>, --stdin or --code <text>.");
  }

  try {
    if (request.file) {
      const path = resolve(request.file);
      return codeSource("file", await readFile(path, "utf8"), path);
    }
    if (request.code !== undefined) return codeSource("inline", request.code);
    return codeSource("stdin", await readAllStdin());
  } catch (error) {
    if (error instanceof StructuredToolError) throw error;
    throw new StructuredToolError("E_CODE_SOURCE_INVALID", error instanceof Error ? error.message : String(error), ["Check that the source path exists and is UTF-8 readable."]);
  }
}

function codeSource(kind: CodeSource["kind"], text: string, path?: string): CodeSource {
  if (text.trim().length === 0) {
    throw new StructuredToolError("E_CODE_SOURCE_INVALID", "Code source is empty.");
  }
  return {
    kind,
    path,
    text,
    sha256: createHash("sha256").update(text, "utf8").digest("hex"),
    sizeBytes: Buffer.byteLength(text, "utf8")
  };
}

async function readAllStdin(): Promise<string> {
  let text = "";
  stdin.setEncoding("utf8");
  for await (const chunk of stdin) {
    text += chunk;
  }
  return text;
}

function normalizeChannel(channel: string | undefined): { ok: true; value: "live" | "batchmode" } | { ok: false; code: string; message: string } {
  if (!channel || channel === "live") {
    return { ok: true, value: "live" };
  }
  if (channel === "batchmode") {
    return { ok: true, value: "batchmode" };
  }
  return { ok: false, code: "E_SCHEMA_INVALID", message: `Unsupported code execution channel: ${channel}.` };
}

function normalizeTimeout(value: number | undefined, channel: "live" | "batchmode"): number {
  const fallback = channel === "batchmode" ? batchDefaultTimeoutMs : liveDefaultTimeoutMs;
  const maximum = channel === "batchmode" ? 1_800_000 : 600_000;
  if (!value || value <= 0) {
    return fallback;
  }
  return Math.min(value, maximum);
}

function runtimeArtifactDir(dataDir: string, requestId: string): string {
  return resolve(process.env.UNITY_CLI_HOME ?? dataDir, "artifacts", requestId);
}

async function validateExtraReferences(refs: string[] | undefined): Promise<string> {
  if (!refs) return "";
  const normalized = refs.map((part) => part.trim()).filter(Boolean);
  for (const reference of normalized) {
    try {
      await access(reference, constants.R_OK);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      throw new StructuredToolError("E_REFERENCE_RESOLUTION_FAILED", `Runtime Code reference is not readable: ${reference}.`, [
        message,
        "Pass --refs with existing readable assembly paths."
      ]);
    }
  }
  return normalized.join(",");
}

function manifestData(artifactDir: string, requestId: string, jobId: string | undefined, projectPath: string, source: CodeSource, channel: string, timeoutMs: number, args: Record<string, unknown>, extraArtifacts: Array<Record<string, unknown>> = []): Record<string, unknown> {
  const sourceArtifactPath = source.path ?? resolve(artifactDir, "source.csx");
  const sourceArtifact = {
    kind: "source",
    path: sourceArtifactPath,
    relativePath: normalizeRelativePath(relative(artifactDir, sourceArtifactPath) || "."),
    sha256: source.sha256,
    sizeBytes: source.sizeBytes
  };
  return {
    requestId,
    jobId: jobId ?? null,
    command: "code.exec",
    createdAt: new Date().toISOString(),
    producer: "unity_cli",
    projectPath,
    artifact: [sourceArtifact, ...extraArtifacts],
    artifacts: [sourceArtifact, ...extraArtifacts],
    source: { kind: source.kind, path: source.path, sha256: source.sha256, sizeBytes: source.sizeBytes },
    execution: { channel, timeoutMs },
    input: { args }
  };
}

async function writeSourceArtifact(artifactDir: string, source: CodeSource): Promise<{ ok: true } | { ok: false; message: string; path: string }> {
  const artifactPath = source.path ?? resolve(artifactDir, "source.csx");
  if (source.path) {
    return { ok: true };
  }
  try {
    await mkdir(dirname(artifactPath), { recursive: true });
    await writeFile(artifactPath, source.text, "utf8");
    return { ok: true };
  } catch (error) {
    return { ok: false, message: error instanceof Error ? error.message : String(error), path: artifactPath };
  }
}

async function writeManifest(artifactDir: string, manifest: Record<string, unknown>): Promise<{ ok: true } | { ok: false; message: string }> {
  try {
    await mkdir(artifactDir, { recursive: true });
    await writeFile(resolve(artifactDir, "manifest.json"), `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
    return { ok: true };
  } catch (error) {
    return { ok: false, message: error instanceof Error ? error.message : String(error) };
  }
}

async function startBatchCodeJob(
  request: RuntimeCodeRequest,
  runtime: RuntimeCodeRuntime,
  requestId: string,
  projectPath: string,
  source: CodeSource,
  artifactDir: string,
  timeoutMs: number,
  payload: Record<string, unknown>
): Promise<Record<string, unknown>> {
  if (!runtime.runner) {
    throw new StructuredToolError("E_TOOL_HANDLER_UNSUPPORTED", "code.exec requires a job runner runtime for batchmode execution.");
  }
  const unityPath = request.unity ?? discoverUnity();
  if (!unityPath) {
    throw new StructuredToolError("UNITY_NOT_FOUND", "Unity.exe was not found.", ["Pass --unity <path-to-Unity.exe> or set UNITY_EXE."]);
  }

  const bridge = await ensurePackageBridge(projectPath);
  const sourcePath = source.path ?? resolve(artifactDir, "source.csx");
  const resultPath = resolve(artifactDir, "result.json");
  const result = {
    command: "code.exec",
    mode: "agent-job",
    execution: { channel: "batchmode", timeoutMs, exitState: "queued" },
    source: { kind: source.kind, path: source.path, sha256: source.sha256, sizeBytes: source.sizeBytes },
    artifacts: [{
      kind: "source",
      path: sourcePath,
      relativePath: normalizeRelativePath(relative(artifactDir, sourcePath) || "."),
      sha256: source.sha256,
      sizeBytes: source.sizeBytes
    }],
    artifactDir,
    resultPath,
    bridge
  };
  const job = await runtime.runner.start({
    kind: "code",
    projectPath,
    unityPath,
    timeoutMs,
    requestId,
    args: [
      "-executeMethod", "UnityCli.UnityCliBatch.ExecuteCode",
      "-unityCliRequestId", requestId,
      "-unityCliCodeSource", sourcePath,
      "-unityCliCodeArgs", String(payload.argsJson ?? "{}"),
      "-unityCliCodeDefines", String(payload.defines ?? ""),
      "-unityCliCodeRefs", codeRefsArg(payload.refs),
      "-unityCliArtifactDir", artifactDir,
      "-unityCliTimeoutMs", String(timeoutMs),
      "-unityCliProjectPath", projectPath,
      "-unityCliCodeResult", resultPath
    ],
    result
  });
  const manifestResult = await writeManifest(artifactDir, manifestData(artifactDir, requestId, job.id, projectPath, source, "batchmode", timeoutMs, payload.argsJson && typeof payload.argsJson === "string" ? JSON.parse(payload.argsJson) as Record<string, unknown> : {}));
  if (!manifestResult.ok) {
    const cancelled = await runtime.runner.cancel(job.id).catch(() => undefined);
    throw new StructuredToolError("E_ARTIFACT_WRITE_FAILED", `Failed to write Runtime Code artifact manifest: ${manifestResult.message}`, ["Check --artifact-dir permissions and free disk space before rerunning code exec."], {
      jobId: job.id,
      artifactDir,
      manifestPath: resolve(artifactDir, "manifest.json"),
      cancelAttempted: true,
      cancelled: cancelled?.status === "cancelled",
      processKilled: Boolean(cancelled?.error?.details && typeof cancelled.error.details === "object" && (cancelled.error.details as { processKilled?: unknown }).processKilled),
      domainReloadObserved: false
    });
  }

  if (!request.wait) return summarizeJob(job);

  const waitTimeoutMs = request.waitTimeoutMs ?? timeoutMs;
  const waited = await runtime.runner.wait(job.id, waitTimeoutMs);
  if (!["succeeded", "failed", "cancelled", "timeout"].includes(waited.status)) {
    throw new StructuredToolError("E_EXECUTION_TIMEOUT", `Job did not reach a terminal state within ${waitTimeoutMs}ms.`, [
      `Run unity_cli jobs status ${job.id}.`,
      `Run unity_cli jobs logs ${job.id}.`,
      `Run unity_cli jobs cancel ${job.id} --yes if the Unity process should be stopped.`
    ], {
      jobId: job.id,
      timeoutMs: waitTimeoutMs,
      cancelAttempted: false,
      cancelled: false,
      processKilled: false,
      domainReloadObserved: false
    });
  }
  return summarizeJob(waited);
}

function throwCliFailure(result: CliResult): never {
  if (result.ok) {
    throw new StructuredToolError("CODE_EXEC_FAILED", "code.exec returned a success envelope where a failure was required.");
  }
  throw StructuredToolError.from(result.error);
}

function normalizeRelativePath(path: string): string {
  return path.split("\\").join("/");
}

function codeRefsArg(refs: unknown): string {
  return typeof refs === "string" && refs.length > 0 ? refs : "__UNITY_CLI_EMPTY__";
}
