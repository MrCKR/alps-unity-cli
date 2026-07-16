import { summarizeJob } from "../../cli-commands/unity-job.js";
import type { JobRecord } from "../../types.js";
import type { ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface BuildJobIdRequest {
  jobId?: string;
  id?: string;
  job?: string;
}

export interface BuildJobCancellation {
  mode: string;
  description: string;
  cancelCommand: string;
  domainReloadObserved: boolean;
}

/** 解析 build jobId,保持 build.status/build.cancel 的别名语义一致。 */
export function resolveBuildJobId(request: BuildJobIdRequest): string {
  const value = request.jobId ?? request.id ?? request.job;
  if (typeof value !== "string" || value.length === 0) {
    throw new StructuredToolError("BUILD_JOB_ID_REQUIRED", "Pass a build jobId.", [
      "Run tools run build.run first, then pass the returned jobId."
    ]);
  }
  return value;
}

/** 从 job store 读取并校验 build job,避免 build 域工具误操作其它 job 类型。 */
export async function readBuildJob(context: ToolContext, request: BuildJobIdRequest): Promise<JobRecord> {
  const jobId = resolveBuildJobId(request);
  if (!context.jobStore) {
    throw new StructuredToolError("BUILD_JOB_RUNTIME_MISSING", "Build job tools require the CLI job store runtime.", [
      "Run this through unity_cli tools run build.status or build.cancel, not by calling the tool definition directly."
    ]);
  }
  const job = await context.jobStore.get(jobId);
  if (!job) {
    throw new StructuredToolError("BUILD_JOB_NOT_FOUND", `Build job not found: ${jobId}.`, [
      "Pass a build jobId returned by tools run build.run."
    ]);
  }
  if (job.kind !== "build") {
    throw new StructuredToolError("BUILD_JOB_KIND_MISMATCH", `Job is not a build job: ${job.kind}.`, [
      "Pass a jobId returned by tools run build.run."
    ]);
  }
  return job;
}

/** 把通用 job summary 改写成 build 域输出,保留旧 handler 的 command/jobCommand 形态。 */
export async function buildJobSummary(command: "build.status" | "build.cancel", job: JobRecord): Promise<Record<string, unknown>> {
  const { command: jobCommand, ...rest } = await summarizeJob(job);
  return { command, ...rest, jobCommand };
}
