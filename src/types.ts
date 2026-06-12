export type JobKind = "refresh" | "test" | "build";

export type JobStatus = "queued" | "running" | "succeeded" | "failed" | "cancelled" | "timeout";

export interface CliError {
  code: string;
  message: string;
  requestId: string;
  next_actions?: string[];
}

export interface CliResult<T = unknown> {
  ok: boolean;
  requestId: string;
  data?: T;
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
