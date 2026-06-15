import { getBoolean, getNumber } from "../args.js";
import { batchJsonSchema, runBatch } from "../batch.js";
import { failure, success } from "../output.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";

export function handleBatch(command: string | undefined, filePath: string | undefined, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> | CliResult {
  if (command === "schema") {
    return success(requestId, batchJsonSchema());
  }

  if (command !== "run") {
    return failure(requestId, "UNKNOWN_BATCH_COMMAND", "Expected batch run <file> or batch schema.");
  }

  if (!filePath) {
    return failure(requestId, "BATCH_FILE_REQUIRED", "Pass a batch JSON file: unity_cli batch run <file>.");
  }

  return runBatch({
    filePath,
    requestId,
    runtime,
    failFast: flags["fail-fast"] === undefined ? undefined : getBoolean(flags, "fail-fast"),
    parallel: getNumber(flags, "parallel"),
    dryRun: getBoolean(flags, "preflight") || getBoolean(flags, "dry-run"),
    executeStep: async (stepArgv) => {
      const { execute } = await import("../cli.js");
      return execute(stepArgv, runtime);
    }
  });
}
