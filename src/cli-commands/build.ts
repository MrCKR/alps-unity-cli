import { resolve } from "node:path";
import { getString } from "../args.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";
import { handleJob } from "./job.js";
import { startUnityJob } from "./unity-job.js";

export function handleBuild(command: string | undefined, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> | undefined {
  if (command === "run") {
    const target = getString(flags, "target") ?? "StandaloneWindows64";
    const outputPath = resolve(getString(flags, "output") ?? "Builds/Windows/UnityCliBuild.exe");
    return startUnityJob("build", flags, runtime, requestId, ["-buildTarget", target, "-executeMethod", "UnityCli.UnityCliBatch.BuildProject", "-unityCliBuildOutput", outputPath], {
      action: "build",
      target,
      outputPath
    });
  }

  if (command === "status") {
    return Promise.resolve(handleJob("status", flags, runtime, requestId));
  }

  if (command === "logs") {
    return Promise.resolve(handleJob("logs", flags, runtime, requestId));
  }

  if (command === "cancel") {
    return Promise.resolve(handleJob("cancel", flags, runtime, requestId));
  }

  return undefined;
}
