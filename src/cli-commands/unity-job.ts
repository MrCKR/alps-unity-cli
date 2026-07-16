import { existsSync, renameSync } from "node:fs";
import { mkdir, readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { getBoolean, getNumber, getString } from "../args.js";
import { failure, success } from "../output.js";
import { discoverUnity } from "../platform.js";
import type { CliResult, JobKind, JobRecord } from "../types.js";
import type { Runtime } from "./types.js";
import type { JobRunner } from "../job-runner.js";
import { StructuredToolError } from "../tools/errors.js";

/** 检查 UnityLockfile 是否被进程持有；可原子改名的残留文件不构成 active lock。 */
export function isProjectLocked(projectPath: string): boolean {
  const lockPath = resolve(projectPath, "Temp", "UnityLockfile");
  if (!existsSync(lockPath)) return false;
  const probePath = `${lockPath}.unity-cli-probe-${process.pid}-${Date.now()}`;
  let moved = false;
  try {
    renameSync(lockPath, probePath);
    moved = true;
    renameSync(probePath, lockPath);
    moved = false;
    return false;
  } catch {
    if (moved) {
      try { renameSync(probePath, lockPath); } catch { }
    }
    return true;
  }
}

const terminalStatuses = new Set(["succeeded", "failed", "cancelled", "timeout"]);

export interface UnityJobRequest {
  kind: JobKind;
  requestId: string;
  project?: string;
  unity?: string;
  args: string[];
  result: Record<string, unknown>;
  timeoutMs?: number;
  wait?: boolean;
  waitTimeoutMs?: number;
}

/** 使用明确参数启动 Unity batchmode，并返回 job 业务数据。 */
export async function runUnityJob(request: UnityJobRequest, runner: JobRunner): Promise<Record<string, unknown>> {
  const projectPath = resolve(request.project ?? process.cwd());
  const unityPath = request.unity ?? discoverUnity();
  if (!unityPath) {
    throw new StructuredToolError("UNITY_NOT_FOUND", "Unity.exe was not found.", ["Pass --unity <path-to-Unity.exe> or set UNITY_EXE."]);
  }

  if (isProjectLocked(projectPath)) {
    throw new StructuredToolError("UNITY_BUSY", `Cannot start Unity batchmode: the project is currently open in the Unity Editor (${projectPath}).`, [
      "Close the Unity Editor and retry.",
      "Wait for the process holding <project>/Temp/UnityLockfile to exit, then retry",
      "Run 'unity_cli jobs status <jobId>' to check existing jobs."
    ], {
      projectPath,
      kind: request.kind,
      lockFilePath: resolve(projectPath, "Temp", "UnityLockfile")
    });
  }

  if (typeof request.result.resultPath === "string") {
    await mkdir(dirname(request.result.resultPath), { recursive: true });
  }

  const job = await runner.start({
    kind: request.kind,
    projectPath,
    unityPath,
    args: request.args,
    timeoutMs: request.timeoutMs,
    requestId: request.requestId,
    result: request.result
  });
  if (!request.wait) return summarizeJob(job);

  const waitTimeoutMs = request.waitTimeoutMs ?? request.timeoutMs ?? 600000;
  const waited = await runner.wait(job.id, waitTimeoutMs);
  if (!terminalStatuses.has(waited.status)) {
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

export async function startUnityJob(kind: JobKind, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string, unityArgs: string[], result: Record<string, unknown>): Promise<CliResult> {
  try {
    const data = await runUnityJob({
      kind,
      requestId,
      project: getString(flags, "project"),
      unity: getString(flags, "unity"),
      args: unityArgs,
      result,
      timeoutMs: getNumber(flags, "timeout-ms"),
      wait: getBoolean(flags, "wait"),
      waitTimeoutMs: getNumber(flags, "wait-timeout-ms")
    }, runtime.runner);
    return success(requestId, data);
  } catch (error) {
    if (error instanceof StructuredToolError) {
      return failure(requestId, error.code, error.message, error.nextActions, error.details);
    }
    throw error;
  }
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
    description: `${kind} jobs run as external Unity batchmode processes. unity_cli jobs cancel attempts an immediate process-tree termination when a pid is known; if the process is still queued or the pid is unavailable, the job is marked cancelled and the underlying Unity operation is expected to finish or already be gone.`,
    cancelCommand: "unity_cli jobs cancel <jobId> --yes",
    domainReloadObserved: false
  };
}
