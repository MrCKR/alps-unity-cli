import { defineTool } from "../define.js";
import { buildJobSummary, readBuildJob } from "./shared.js";
import type { BuildJobCancellation } from "./shared.js";
import type { AnyJson } from "../../schema/any-json.js";

export interface BuildStatusRequest {
  jobId: string;
  id?: string;
  job?: string;
}

export interface BuildStatusObject {
  [key: string]: AnyJson;
}

export interface BuildStatusError {
  code: string;
  message: string;
  details?: AnyJson;
}

export interface BuildStatusResult {
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
  result?: BuildStatusObject;
  error?: BuildStatusError;
  cancellation: BuildJobCancellation;
  jobCommand: string[];
  logTail?: string[];
}

export default defineTool<BuildStatusRequest, BuildStatusResult>({
  name: "build.status",
  summary: "Read build job status using build-domain language.",
  description: "Read build job status using build-domain language. This wraps the generic job store but only accepts jobs created by build.run, returning target/outputPath/logPath/result fields without requiring AI to switch to top-level jobs status.",
  group: "build",
  destructiveHint: false,
  sideEffects: [],
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [{ description: "Check a build job", argv: ["tools", "run", "build.status", "--input", "{\"jobId\":\"job_xxx\"}"] }],
  /** 读取 build job 的持久化状态,不连接 Unity、不启动新进程。 */
  async execute(request, context) {
    const job = await readBuildJob(context, request);
    const summary = await buildJobSummary("build.status", job);
    return summary as unknown as BuildStatusResult;
  }
});
