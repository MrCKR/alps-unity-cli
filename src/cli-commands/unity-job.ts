import { existsSync } from "node:fs";
import { mkdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { getBoolean, getNumber, getString } from "../args.js";
import { failure, success } from "../output.js";
import { discoverUnity } from "../platform.js";
import type { CliResult, JobKind, JobRecord } from "../types.js";
import type { Runtime } from "./types.js";

/**
 * Check if the project is currently open in the Unity Editor by looking for the lock file.
 * Unity creates `Temp/UnityLockfile` when a project is open; stale files may remain after crashes.
 */
export function isProjectLocked(projectPath: string): boolean {
  return existsSync(resolve(projectPath, "Temp", "UnityLockfile"));
}

const terminalStatuses = new Set(["succeeded", "failed", "cancelled", "timeout"]);

export async function startUnityJob(kind: JobKind, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string, unityArgs: string[], result: Record<string, unknown>): Promise<CliResult> {
  const projectPath = resolve(getString(flags, "project") ?? process.cwd());
  const unityPath = getString(flags, "unity") ?? discoverUnity();
  if (!unityPath) {
    return failure(requestId, "UNITY_NOT_FOUND", "Unity.exe was not found.", ["Pass --unity <path-to-Unity.exe> or set UNITY_EXE."]);
  }

  // Pre-flight: check if the project is locked by another Unity instance.
  // Unity batchmode cannot open a project that is already open in the Editor;
  // it will hang or exit immediately with empty output, leading to confusing timeouts.
  if (isProjectLocked(projectPath)) {
    return failure(requestId, "UNITY_BUSY", `Cannot start Unity batchmode: the project is currently open in the Unity Editor (${projectPath}).`, [
      "Close the Unity Editor and retry.",
      "If Unity is not running, delete the stale lock file: <project>/Temp/UnityLockfile",
      `Run 'unity_cli job status' to check existing jobs.`
    ], {
      projectPath,
      kind,
      lockFilePath: resolve(projectPath, "Temp", "UnityLockfile")
    });
  }

  if (typeof result.resultPath === "string") {
    await mkdir(dirname(result.resultPath), { recursive: true });
  }

  const timeoutMs = getNumber(flags, "timeout-ms");
  const job = await runtime.runner.start({ kind, projectPath, unityPath, args: unityArgs, timeoutMs, requestId, result });
  if (!getBoolean(flags, "wait")) {
    return success(requestId, await summarizeJob(job));
  }

  const waitTimeoutMs = getNumber(flags, "wait-timeout-ms") ?? timeoutMs ?? 600000;
  const waited = await runtime.runner.wait(job.id, waitTimeoutMs);
  if (!terminalStatuses.has(waited.status)) {
    return failure(requestId, "E_EXECUTION_TIMEOUT", `Job did not reach a terminal state within ${waitTimeoutMs}ms.`, [
      `Run unity_cli job status --id ${job.id}.`,
      `Run unity_cli job logs --id ${job.id}.`,
      `Run unity_cli job cancel --id ${job.id} if the Unity process should be stopped.`
    ], {
      jobId: job.id,
      timeoutMs: waitTimeoutMs,
      cancelAttempted: false,
      cancelled: false,
      processKilled: false,
      domainReloadObserved: false
    });
  }
  return success(requestId, await summarizeJob(waited));
}

export async function summarizeJob(job: JobRecord): Promise<Record<string, unknown>> {
  const base: Record<string, unknown> = {
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
    cancellation: jobCancellationSemantics(job.kind),
    ...(job.error ? { error: job.error } : {})
  };

  // For terminal jobs, embed the last N log lines so callers can see the failure
  // reason without a separate `job logs` round-trip.
  if (terminalStatuses.has(job.status) && job.logPath) {
    try {
      const { readFile } = await import("node:fs/promises");
      const raw = await readFile(job.logPath, "utf8");
      const lines = raw.split(/\r?\n/).filter(Boolean);
      base.logTail = lines.slice(Math.max(0, lines.length - 40));
    } catch {
      // Log file may not exist or be unreadable — non-critical.
    }
  }

  return base;
}

export function jobCancellationSemantics(kind: JobKind): Record<string, unknown> {
  return {
    mode: "immediate",
    description: `${kind} jobs run as external Unity batchmode processes. unity_cli job cancel attempts an immediate process-tree termination when a pid is known; if the process is still queued or the pid is unavailable, the job is marked cancelled and the underlying Unity operation is expected to finish or already be gone.`,
    cancelCommand: "unity_cli job cancel --id <jobId>",
    domainReloadObserved: false
  };
}
