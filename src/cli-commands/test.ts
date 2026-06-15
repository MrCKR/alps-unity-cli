import { resolve } from "node:path";
import { getString } from "../args.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";
import { handleJob } from "./job.js";
import { startUnityJob } from "./unity-job.js";

export function handleTest(command: string | undefined, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> | undefined {
  if (command === "run") {
    const platform = getString(flags, "platform") ?? "EditMode";
    const resultPath = resolve(runtime.dataDir, "results", `${requestId}-test-results.xml`);
    return startUnityJob("test", flags, runtime, requestId, ["-runTests", "-testPlatform", platform, "-testResults", resultPath], {
      action: "test",
      platform,
      resultPath
    });
  }

  if (command === "poll" || command === "status") {
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
