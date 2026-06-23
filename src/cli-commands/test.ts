import { resolve } from "node:path";
import { getString } from "../args.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";
import { startUnityJob } from "./unity-job.js";

export async function handleTest(command: string | undefined, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult | undefined> {
  if (command === "run") {
    const platform = getString(flags, "platform") ?? "EditMode";
    const resultPath = resolve(runtime.dataDir, "results", `${requestId}-test-results.xml`);
    return startUnityJob("test", flags, runtime, requestId, ["-runTests", "-testPlatform", platform, "-testResults", resultPath], {
      action: "test",
      platform,
      resultPath
    });
  }

  return undefined;
}
