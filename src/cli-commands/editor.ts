import { getBoolean } from "../args.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";
import { startUnityJob } from "./unity-job.js";

export function handleEditor(command: string | undefined, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> | undefined {
  if (command !== "refresh") {
    return undefined;
  }

  return startUnityJob("refresh", flags, runtime, requestId, refreshArgs(flags), { action: "refresh", compile: getBoolean(flags, "compile") });
}

function refreshArgs(flags: Record<string, string | boolean>): string[] {
  return ["-executeMethod", getBoolean(flags, "compile") ? "UnityCli.UnityCliBatch.RefreshAndCompile" : "UnityCli.UnityCliBatch.Refresh"];
}
