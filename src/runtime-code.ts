import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, relative, resolve } from "node:path";
import { stdin } from "node:process";
import { getBoolean, getNumber, getString } from "./args.js";
import { executeBridgePayload, type BridgeRuntime } from "./bridge-commands.js";
import { failure, success } from "./output.js";
import { discoverUnity } from "./platform.js";
import type { CliResult, JobRecord } from "./types.js";
import { ensurePackageBridge } from "./package-manager.js";
import type { JobRunner } from "./job-runner.js";

interface RuntimeCodeRuntime extends BridgeRuntime {
  dataDir: string;
  runner: JobRunner;
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

export async function handleCode(command: string | undefined, flags: Record<string, string | boolean>, runtime: RuntimeCodeRuntime, requestId: string): Promise<CliResult> {
  if (command !== "exec") {
    return failure(requestId, "UNKNOWN_CODE_COMMAND", "Expected code exec --file <path>, code exec --stdin or code exec --code <text>.");
  }

  const source = await readCodeSource(flags, requestId);
  if (!source.ok) {
    return source.result;
  }

  const channel = normalizeChannel(getString(flags, "channel"));
  if (!channel.ok) {
    return failure(requestId, channel.code, channel.message, ["Pass --channel live or --channel batchmode."]);
  }

  const timeoutMs = normalizeTimeout(getNumber(flags, "timeout-ms"), channel.value);
  const artifactDir = resolve(getString(flags, "artifact-dir") ?? runtimeArtifactDir(runtime.dataDir, requestId));
  const projectPath = resolve(getString(flags, "project") ?? process.cwd());
  const argsJson = getString(flags, "args") ?? "{}";
  const parsedArgs = parseJsonObject(argsJson);
  if (!parsedArgs.ok) {
    return failure(requestId, "E_SCHEMA_INVALID", parsedArgs.message, ["Pass --args as a JSON object."]);
  }

  const payload = {
    command: "code.exec",
    sourceKind: source.value.kind,
    sourcePath: source.value.path,
    sourceText: source.value.text,
    sourceSha256: source.value.sha256,
    sourceSizeBytes: source.value.sizeBytes,
    channel: channel.value,
    timeoutMs,
    artifactDir,
    projectPath,
    argsJson: JSON.stringify(parsedArgs.value),
    defines: getString(flags, "define") ?? "",
    refs: getString(flags, "refs") ?? ""
  };

  const sourceArtifactResult = await writeSourceArtifact(artifactDir, source.value);
  if (!sourceArtifactResult.ok) {
    return failure(requestId, "E_ARTIFACT_WRITE_FAILED", `Failed to write Runtime Code source artifact: ${sourceArtifactResult.message}`, ["Check --artifact-dir permissions and free disk space."], { artifactDir, artifactPath: sourceArtifactResult.path });
  }

  const manifest = manifestData(artifactDir, requestId, undefined, projectPath, source.value, channel.value, timeoutMs, parsedArgs.value);
  const manifestResult = await writeManifest(artifactDir, manifest);
  if (!manifestResult.ok) {
    return failure(requestId, "E_ARTIFACT_WRITE_FAILED", `Failed to write Runtime Code artifact manifest: ${manifestResult.message}`, ["Check --artifact-dir permissions and free disk space."], { artifactDir, manifestPath: resolve(artifactDir, "manifest.json") });
  }

  if (channel.value === "live" && !getBoolean(flags, "job")) {
    return executeBridgePayload(requestId, "code.exec", payload, flags, runtime);
  }

  return startBatchCodeJob(flags, runtime, requestId, projectPath, source.value, artifactDir, timeoutMs, payload);
}

async function readCodeSource(flags: Record<string, string | boolean>, requestId: string): Promise<{ ok: true; value: CodeSource } | { ok: false; result: CliResult }> {
  const filePath = getString(flags, "file");
  const inline = getString(flags, "code");
  const useStdin = getBoolean(flags, "stdin");
  const selected = [filePath !== undefined, inline !== undefined, useStdin].filter(Boolean).length;
  if (selected !== 1) {
    return { ok: false, result: failure(requestId, "E_CODE_SOURCE_INVALID", "Pass exactly one code source: --file <path>, --stdin or --code <text>.") };
  }

  try {
    if (filePath) {
      const path = resolve(filePath);
      const text = await readFile(path, "utf8");
      return codeSource("file", text, requestId, path);
    }
    if (inline !== undefined) {
      return codeSource("inline", inline, requestId);
    }
    return codeSource("stdin", await readAllStdin(), requestId);
  } catch (error) {
    return { ok: false, result: failure(requestId, "E_CODE_SOURCE_INVALID", error instanceof Error ? error.message : String(error), ["Check that the source path exists and is UTF-8 readable."]) };
  }
}

function codeSource(kind: CodeSource["kind"], text: string, requestId: string, path?: string): { ok: true; value: CodeSource } | { ok: false; result: CliResult } {
  if (text.trim().length === 0) {
    return { ok: false, result: failure(requestId, "E_CODE_SOURCE_INVALID", "Code source is empty.") };
  }
  return {
    ok: true,
    value: {
      kind,
      path,
      text,
      sha256: createHash("sha256").update(text, "utf8").digest("hex"),
      sizeBytes: Buffer.byteLength(text, "utf8")
    }
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
  return resolve(process.env.ALPS_UNITY_HOME ?? dataDir, "artifacts", requestId);
}

function parseJsonObject(value: string): { ok: true; value: Record<string, unknown> } | { ok: false; message: string } {
  try {
    const parsed = JSON.parse(value) as unknown;
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
      return { ok: false, message: "--args must be a JSON object." };
    }
    return { ok: true, value: parsed as Record<string, unknown> };
  } catch (error) {
    return { ok: false, message: error instanceof Error ? error.message : String(error) };
  }
}

function manifestData(artifactDir: string, requestId: string, jobId: string | undefined, projectPath: string, source: CodeSource, channel: string, timeoutMs: number, args: Record<string, unknown>): Record<string, unknown> {
  const sourceArtifactPath = source.path ?? resolve(artifactDir, "source.csx");
  return {
    requestId,
    jobId: jobId ?? null,
    command: "code.exec",
    createdAt: new Date().toISOString(),
    producer: "alps-unity-cli",
    projectPath,
    artifact: [
      {
        kind: "source",
        path: sourceArtifactPath,
        relativePath: relative(artifactDir, sourceArtifactPath) || ".",
        sha256: source.sha256,
        sizeBytes: source.sizeBytes
      }
    ],
    artifacts: [
      {
        kind: "source",
        path: sourceArtifactPath,
        relativePath: relative(artifactDir, sourceArtifactPath) || ".",
        sha256: source.sha256,
        sizeBytes: source.sizeBytes
      }
    ],
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
  flags: Record<string, string | boolean>,
  runtime: RuntimeCodeRuntime,
  requestId: string,
  projectPath: string,
  source: CodeSource,
  artifactDir: string,
  timeoutMs: number,
  payload: Record<string, unknown>
): Promise<CliResult> {
  const unityPath = getString(flags, "unity") ?? discoverUnity();
  if (!unityPath) {
    return failure(requestId, "UNITY_NOT_FOUND", "Unity.exe was not found.", ["Pass --unity <path-to-Unity.exe> or set UNITY_EXE."]);
  }

  const bridge = await ensurePackageBridge(projectPath);
  const sourcePath = source.path ?? resolve(artifactDir, "source.csx");
  const resultPath = resolve(artifactDir, "result.json");
  const result = {
    command: "code.exec",
    mode: "agent-job",
    execution: { channel: "batchmode", timeoutMs, exitState: "queued" },
    source: { kind: source.kind, path: source.path, sha256: source.sha256, sizeBytes: source.sizeBytes },
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
      "-executeMethod", "AlpsUnityCli.Batch.ExecuteCode",
      "-alpsRequestId", requestId,
      "-alpsCodeSource", sourcePath,
      "-alpsCodeArgs", String(payload.argsJson ?? "{}"),
      "-alpsCodeDefines", String(payload.defines ?? ""),
      "-alpsCodeRefs", String(payload.refs ?? ""),
      "-alpsArtifactDir", artifactDir,
      "-alpsTimeoutMs", String(timeoutMs),
      "-alpsProjectPath", projectPath,
      "-alpsCodeResult", resultPath
    ],
    result
  });
  const manifestResult = await writeManifest(artifactDir, manifestData(artifactDir, requestId, job.id, projectPath, source, "batchmode", timeoutMs, payload.argsJson && typeof payload.argsJson === "string" ? JSON.parse(payload.argsJson) as Record<string, unknown> : {}));
  if (!manifestResult.ok) {
    const cancelled = await runtime.runner.cancel(job.id).catch(() => undefined);
    return failure(requestId, "E_ARTIFACT_WRITE_FAILED", `Failed to write Runtime Code artifact manifest: ${manifestResult.message}`, ["Check --artifact-dir permissions and free disk space before rerunning code exec."], {
      jobId: job.id,
      artifactDir,
      manifestPath: resolve(artifactDir, "manifest.json"),
      cancelAttempted: true,
      cancelled: cancelled?.status === "cancelled",
      processKilled: Boolean(cancelled?.error?.details && typeof cancelled.error.details === "object" && (cancelled.error.details as { processKilled?: unknown }).processKilled),
      domainReloadObserved: false
    });
  }

  if (!getBoolean(flags, "wait")) {
    return success(requestId, summarizeJob(job));
  }

  const waitTimeoutMs = getNumber(flags, "wait-timeout-ms") ?? timeoutMs;
  const waited = await runtime.runner.wait(job.id, waitTimeoutMs);
  if (!["succeeded", "failed", "cancelled", "timeout"].includes(waited.status)) {
    return failure(requestId, "E_EXECUTION_TIMEOUT", `Job did not reach a terminal state within ${waitTimeoutMs}ms.`, [
      `Run alps-unity job status --id ${job.id}.`,
      `Run alps-unity job logs --id ${job.id}.`,
      `Run alps-unity job cancel --id ${job.id} if the Unity process should be stopped.`
    ], {
      jobId: job.id,
      timeoutMs: waitTimeoutMs,
      cancelAttempted: false,
      cancelled: false,
      processKilled: false,
      domainReloadObserved: false
    });
  }
  return success(requestId, summarizeJob(waited));
}

function summarizeJob(job: JobRecord): Record<string, unknown> {
  return {
    jobId: job.id,
    kind: job.kind,
    status: job.status,
    requestId: job.requestId,
    projectPath: job.projectPath,
    unityPath: job.unityPath,
    logPath: job.logPath,
    pid: job.pid,
    exitCode: job.exitCode,
    createdAt: job.createdAt,
    updatedAt: job.updatedAt,
    startedAt: job.startedAt,
    finishedAt: job.finishedAt,
    command: job.command,
    result: job.result,
    ...(job.error ? { error: job.error } : {})
  };
}
