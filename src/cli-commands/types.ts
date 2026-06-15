import type { BridgeRuntime } from "../bridge/index.js";
import type { DoctorRuntime } from "../doctor.js";
import type { JobRunner } from "../job-runner.js";
import type { JobStore } from "../job-store.js";
import type { ToolsRuntime } from "../tools.js";
import type { CliResult } from "../types.js";

export interface Runtime extends BridgeRuntime, ToolsRuntime, DoctorRuntime {
  store: JobStore;
  runner: JobRunner;
  dataDir: string;
}

export interface CliCommandContext {
  command: string | undefined;
  args: string[];
  flags: Record<string, string | boolean>;
  runtime: Runtime;
  requestId: string;
}

export interface CliCommandModule {
  run(context: CliCommandContext): CliResult | Promise<CliResult | undefined> | undefined;
}
