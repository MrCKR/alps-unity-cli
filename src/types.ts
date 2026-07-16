import type { Envelope, ToolError } from "./protocol/envelope.js";

export type JobKind = "refresh" | "test" | "build" | "package" | "code" | "bridge" | "profiler" | "probuilder";

export type JobStatus = "queued" | "running" | "cancelling" | "succeeded" | "failed" | "cancelled" | "timeout";

/** CLI 内部名称仅作为唯一 wire envelope 的类型别名。 */
export type CliResult<T = unknown, TDetails = unknown> = Envelope<T, TDetails>;

/** CLI 内部名称仅作为唯一 wire error 的类型别名。 */
export type CliError<TDetails = unknown> = ToolError<TDetails>;

export interface JobRecord {
  id: string;
  kind: JobKind;
  status: JobStatus;
  requestId: string;
  createdAt: string;
  updatedAt: string;
  startedAt?: string;
  finishedAt?: string;
  projectPath: string;
  unityPath: string;
  logPath: string;
  pid?: number;
  exitCode?: number | null;
  timeoutMs?: number;
  command: string[];
  result?: Record<string, unknown>;
  error?: {
    code: string;
    message: string;
    details?: unknown;
  };
}

export interface JobStoreData {
  jobs: JobRecord[];
}

export interface RunJobOptions {
  kind: JobKind;
  projectPath: string;
  unityPath: string;
  args: string[];
  timeoutMs?: number;
  requestId: string;
  result?: Record<string, unknown>;
}
