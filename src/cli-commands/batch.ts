import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { getBoolean, getNumber, getString } from "../args.js";
import { batchJsonSchema, runBatch } from "../batch.js";
import { confirmDestructiveExecution } from "../security.js";
import { failure, success } from "../output.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";

export async function handleBatch(command: string | undefined, filePath: string | undefined, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  if (command === "schema") {
    return success(requestId, batchJsonSchema());
  }

  if (command !== "run") {
    return failure(requestId, "UNKNOWN_BATCH_COMMAND", "Expected batch run <file>, batch run --input <json>, or batch schema.");
  }

  const input = getString(flags, "input");
  if (!filePath && input === undefined) {
    return failure(requestId, "BATCH_FILE_REQUIRED", "Pass a batch JSON file or inline JSON: unity_cli batch run <file> or unity_cli batch run --input '<json>'.");
  }

  let batchPath = filePath;
  if (!batchPath) {
    const inlineBatch = await writeInlineBatch(input!, runtime.dataDir, requestId);
    if (inlineBatch.ok === false) return inlineBatch.error;
    batchPath = inlineBatch.filePath;
  }

  const confirmation = await confirmDestructiveExecution(flags, {
    command: "batch.run",
    summary: "Batch Run",
    destructiveHint: true,
    supportsDryRun: true,
    requestId
  });
  if (confirmation) {
    return confirmation;
  }

  return runBatch({
    filePath: batchPath,
    requestId,
    runtime,
    failFast: flags["fail-fast"] === undefined && flags["fail_fast"] === undefined ? undefined : (getBoolean(flags, "fail-fast") || getBoolean(flags, "fail_fast")),
    parallel: getNumber(flags, "parallel"),
    dryRun: getBoolean(flags, "preflight") || getBoolean(flags, "dry-run"),
    executeStep: async (stepArgv) => {
      const { execute } = await import("../cli.js");
      return execute(stepArgv, runtime);
    }
  });
}

/** T 节：将 batch run --input 的内联 JSON 落到临时文件，复用既有 runBatch 文件路径实现。 */
async function writeInlineBatch(input: string, dataDir: string, requestId: string): Promise<{ ok: true; filePath: string } | { ok: false; error: CliResult }> {
  try {
    JSON.parse(input);
  } catch (error) {
    return {
      ok: false,
      error: failure(requestId, "BATCH_INPUT_JSON_INVALID", `--input must be valid batch JSON. ${error instanceof Error ? error.message : String(error)}`, [
        "Pass a JSON object with a steps array, or run unity_cli batch schema for the expected shape."
      ])
    };
  }
  const dir = join(dataDir, "batches", "inline");
  await mkdir(dir, { recursive: true });
  const filePath = join(dir, `${requestId}.json`);
  await writeFile(filePath, `${input}\n`, "utf8");
  return { ok: true, filePath };
}
