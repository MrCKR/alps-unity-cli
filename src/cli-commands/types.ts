import type { BridgeRuntime } from "../bridge/index.js";
import type { DoctorRuntime } from "../doctor.js";
import type { ToolsRuntime } from "../tools/dispatcher.js";
import type { CliResult } from "../types.js";
import type { UnityProjectCloseResult, UnityProjectProcess } from "../unity-project-processes.js";

export interface Runtime extends BridgeRuntime, ToolsRuntime, DoctorRuntime {
  dataDir: string;
  userHome?: string;
  findUnityProjectProcesses?: (instanceId: string, canonicalProjectPath: string, timeoutMs?: number) => Promise<UnityProjectProcess[]>;
  closeUnityProjectProcesses?: (processes: UnityProjectProcess[], timeoutMs: number) => Promise<UnityProjectCloseResult>;
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
