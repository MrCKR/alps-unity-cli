import { getNumber, getString } from "../args.js";
import { failure, success } from "../output.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";
import { jobCancellationSemantics, summarizeJob } from "./unity-job.js";

export async function handleJob(command: string | undefined, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  const id = getString(flags, "id") ?? getString(flags, "job");
  if (!id) {
    return failure(requestId, "JOB_ID_REQUIRED", "Pass --id <jobId>.");
  }

  if (command === "status") {
    const job = await runtime.store.get(id);
    return job ? success(requestId, await summarizeJob(job)) : failure(requestId, "JOB_NOT_FOUND", `Job not found: ${id}`);
  }

  if (command === "logs") {
    return jobLogs(id, getNumber(flags, "tail") ?? 100, runtime, requestId);
  }

  if (command === "describe") {
    const job = await runtime.store.get(id);
    return job
      ? success(requestId, { jobId: id, kind: job.kind, status: job.status, cancellation: jobCancellationSemantics(job.kind), logPath: job.logPath })
      : failure(requestId, "JOB_NOT_FOUND", `Job not found: ${id}`);
  }

  if (command === "cancel") {
    try {
      const job = await runtime.runner.cancel(id);
      return success(requestId, await summarizeJob(job));
    } catch (error) {
      return failure(requestId, "JOB_CANCEL_FAILED", error instanceof Error ? error.message : String(error));
    }
  }

  return failure(requestId, "UNKNOWN_JOB_COMMAND", "Expected job status, job logs, job describe or job cancel.");
}

async function jobLogs(id: string, tail: number, runtime: Runtime, requestId: string): Promise<CliResult> {
  try {
    const logs = await runtime.runner.logs(id, tail);
    return success(requestId, { jobId: id, ...logs });
  } catch (error) {
    if (error instanceof Error && error.message.startsWith("Job not found")) {
      return failure(requestId, "JOB_NOT_FOUND", `Job not found: ${id}`);
    }
    return failure(requestId, "JOB_LOGS_FAILED", error instanceof Error ? error.message : String(error));
  }
}
