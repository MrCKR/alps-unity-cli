import { defineTool } from "../define.js";
import { buildJobSummary, readBuildJob, resolveBuildJobId } from "./shared.js";
import type { BuildJobCancellation } from "./shared.js";
import { StructuredToolError } from "../errors.js";
import type { AnyJson } from "../../schema/any-json.js";

export interface BuildCancelRequest {
  jobId: string;
  id?: string;
  job?: string;
}

export interface BuildCancelObject {
}

export interface BuildCancelError {
  code: string;
  message: string;
  details?: AnyJson;
}

export interface BuildCancelResult {
  command: string;
  jobId: string;
  kind: string;
  status: string;
  requestId: string;
  projectPath: string;
  unityPath: string;
  logPath: string;
  pid?: number;
  exitCode?: number;
  createdAt: string;
  updatedAt: string;
  startedAt?: string;
  finishedAt?: string;
  result?: BuildCancelObject;
  error?: BuildCancelError;
  cancellation: BuildJobCancellation;
  jobCommand: string[];
  logTail?: string[];
}

export default defineTool<BuildCancelRequest, BuildCancelResult>({
  name: "build.cancel",
  summary: "Cancel a build job by build jobId.",
  description: "Cancel a build job by build jobId. This is the build-domain equivalent of jobs cancel and only accepts jobs created by build.run; it attempts immediate process termination when a pid is known and returns cancellation semantics.",
  group: "build",
  destructiveHint: false,
  sideEffects: ["job-cancel"],
  playModeSupport: "session-affecting",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [{ description: "Cancel a build job", argv: ["tools", "run", "build.cancel", "--input", "{\"jobId\":\"job_xxx\"}"] }],
  /** 取消已存在的 build job,仅更新 CLI job store/尝试终止已知 pid,不修改 Unity 项目文件。 */
  async execute(request, context) {
    await readBuildJob(context, request);
    if (!context.jobRunner) {
      throw new StructuredToolError("BUILD_JOB_RUNTIME_MISSING", "Build cancel requires the CLI job runner runtime.", [
        "Run this through unity_cli tools run build.cancel, not by calling the tool definition directly."
      ]);
    }
    const jobId = resolveBuildJobId(request);
    try {
      const cancelled = await context.jobRunner.cancel(jobId);
      const summary = await buildJobSummary("build.cancel", cancelled);
      return summary as unknown as BuildCancelResult;
    } catch (error) {
      throw new StructuredToolError("BUILD_CANCEL_FAILED", error instanceof Error ? error.message : String(error), [
        "Check build.status for the current job state."
      ]);
    }
  }
});
