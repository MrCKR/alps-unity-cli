import type { BridgeRuntime } from "../bridge/index.js";
import type { DoctorRuntime } from "../doctor.js";
import type { ToolsRuntime } from "../tools/dispatcher.js";
import type { CliResult } from "../types.js";

export interface Runtime extends BridgeRuntime, ToolsRuntime, DoctorRuntime {
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
