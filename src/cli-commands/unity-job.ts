import { mkdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { getBoolean, getNumber, getString } from "../args.js";
import { failure, success } from "../output.js";
import { discoverUnity } from "../platform.js";
import type { CliResult, JobKind, JobRecord } from "../types.js";
import type { Runtime } from "./types.js";

const terminalStatuses = new Set(["succeeded", "failed", "cancelled", "timeout"]);

export async function startUnityJob(kind: JobKind, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string, unityArgs: string[], result: Record<string, unknown>): Promise<CliResult> {
  const projectPath = resolve(getString(flags, "project") ?? process.cwd());
  const unityPath = getString(flags, "unity") ?? discoverUnity();
  if (!unityPath) {
    return failure(requestId, "UNITY_NOT_FOUND", "Unity.exe was not found.", ["Pass --unity <path-to-Unity.exe> or set UNITY_EXE."]);
  }

  if (typeof result.resultPath === "string") {
    await mkdir(dirname(result.resultPath), { recursive: true });
  }

  const timeoutMs = getNumber(flags, "timeout-ms");
  const job = await runtime.runner.start({ kind, projectPath, unityPath, args: unityArgs, timeoutMs, requestId, result });
  if (!getBoolean(flags, "wait")) {
    return success(requestId, summarizeJob(job));
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
  return success(requestId, summarizeJob(waited));
}

export function summarizeJob(job: JobRecord): Record<string, unknown> {
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
