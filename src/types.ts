import type { CliError } from "./output.js";

export type JobKind = "refresh" | "test" | "build" | "package" | "code" | "bridge" | "profiler" | "probuilder";

export type JobStatus = "queued" | "running" | "succeeded" | "failed" | "cancelled" | "timeout";

export type { CliError };

export interface CliResult<T = unknown> {
  ok: boolean;
  requestId: string;
  curInstances?: string;
  data?: T;
  warnings?: string[];
  logsRef?: string;
  jobId?: string;
  durationMs?: number;
  error?: CliError;
}

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
