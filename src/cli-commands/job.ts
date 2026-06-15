import { getNumber, getString } from "../args.js";
import { failure, success } from "../output.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";
import { summarizeJob } from "./unity-job.js";

export function handleJob(command: string | undefined, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> | CliResult {
  const id = getString(flags, "id") ?? getString(flags, "job");
  if (!id) {
    return failure(requestId, "JOB_ID_REQUIRED", "Pass --id <jobId>.");
  }

  if (command === "status") {
    return runtime.store.get(id).then((job) => job ? success(requestId, summarizeJob(job)) : failure(requestId, "JOB_NOT_FOUND", `Job not found: ${id}`));
  }

  if (command === "logs") {
    return jobLogs(id, getNumber(flags, "tail") ?? 100, runtime, requestId);
  }

  if (command === "cancel") {
    return runtime.runner.cancel(id)
      .then((job) => success(requestId, summarizeJob(job)))
      .catch((error) => failure(requestId, "JOB_CANCEL_FAILED", error instanceof Error ? error.message : String(error)));
  }

  return failure(requestId, "UNKNOWN_JOB_COMMAND", "Expected job status, job logs or job cancel.");
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
