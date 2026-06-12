#!/usr/bin/env node
import { mkdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { parseArgs, getBoolean, getNumber, getString } from "./args.js";
import { batchJsonSchema, runBatch } from "./batch.js";
import { bridgeCommandName, executeBridgeCommand, type BridgeRuntime } from "./bridge-commands.js";
import { JobRunner } from "./job-runner.js";
import { JobStore } from "./job-store.js";
import { createRequestId, failure, printJson, success } from "./output.js";
import { defaultDataDir, discoverUnity, ensureWindows } from "./platform.js";
import type { CliResult, JobKind, JobRecord } from "./types.js";

interface Runtime extends BridgeRuntime {
  store: JobStore;
  runner: JobRunner;
  dataDir: string;
}

const terminalStatuses = new Set(["succeeded", "failed", "cancelled", "timeout"]);

export async function execute(argv: string[], runtime = createRuntime()): Promise<CliResult> {
  const requestId = createRequestId();
  const parsed = parseArgs(argv);
  const [group, command] = parsed.positional;

  try {
    if (group === "__run-job") {
      return runJobWorker(parsed.flags, requestId);
    }

    ensureWindows();

    if (!group || getBoolean(parsed.flags, "help")) {
      return success(requestId, help());
    }

    if (group === "job") {
      return handleJob(command, parsed.flags, runtime, requestId);
    }

    if (group === "batch") {
      return handleBatch(command, parsed.positional[2], parsed.flags, runtime, requestId);
    }

    const bridgeCommand = bridgeCommandName(group, command);
    if (bridgeCommand) {
      return executeBridgeCommand(requestId, bridgeCommand, parsed.flags, parsed.positional[2], runtime);
    }

    if (group === "editor" && command === "refresh") {
      return startUnityJob("refresh", parsed.flags, runtime, requestId, refreshArgs(parsed.flags), { action: "refresh", compile: getBoolean(parsed.flags, "compile") });
    }

    if (group === "test") {
      if (command === "run") {
        const platform = getString(parsed.flags, "platform") ?? "EditMode";
        const resultPath = resolve(runtime.dataDir, "results", `${requestId}-test-results.xml`);
        return startUnityJob("test", parsed.flags, runtime, requestId, ["-runTests", "-testPlatform", platform, "-testResults", resultPath], {
          action: "test",
          platform,
          resultPath
        });
      }
      if (command === "poll") {
        return handleJob("status", parsed.flags, runtime, requestId);
      }
    }

    if (group === "build") {
      if (command === "run") {
        const target = getString(parsed.flags, "target") ?? "StandaloneWindows64";
        const outputPath = resolve(getString(parsed.flags, "output") ?? "Builds/Windows/AlpsUnityBuild.exe");
        return startUnityJob("build", parsed.flags, runtime, requestId, ["-buildTarget", target, "-executeMethod", "AlpsUnityCli.Batch.BuildProject", "-alpsBuildOutput", outputPath], {
          action: "build",
          target,
          outputPath
        });
      }
      if (command === "status") {
        return handleJob("status", parsed.flags, runtime, requestId);
      }
      if (command === "cancel") {
        return handleJob("cancel", parsed.flags, runtime, requestId);
      }
    }

    return failure(requestId, "UNKNOWN_COMMAND", `Unknown command: ${argv.join(" ")}`, ["Run alps-unity --help."]);
  } catch (error) {
    return failure(requestId, "COMMAND_FAILED", error instanceof Error ? error.message : String(error));
  }
}

function createRuntime(dataDir = defaultDataDir()): Runtime {
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

async function runJobWorker(flags: Record<string, string | boolean>, requestId: string): Promise<CliResult> {
  const id = getString(flags, "id");
  const dataDir = getString(flags, "data-dir");
  if (!id || !dataDir) {
    return failure(requestId, "WORKER_ARGS_REQUIRED", "Internal job worker requires --id and --data-dir.");
  }

  try {
    const runtime = createRuntime(dataDir);
    const job = await runtime.runner.runWorker(id);
    return success(requestId, summarizeJob(job));
  } catch (error) {
    return failure(requestId, "WORKER_FAILED", error instanceof Error ? error.message : String(error));
  }
}

async function handleBatch(command: string | undefined, filePath: string | undefined, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  if (command === "schema") {
    return success(requestId, batchJsonSchema());
  }

  if (command !== "run") {
    return failure(requestId, "UNKNOWN_BATCH_COMMAND", "Expected batch run <file> or batch schema.");
  }

  if (!filePath) {
    return failure(requestId, "BATCH_FILE_REQUIRED", "Pass a batch JSON file: alps-unity batch run <file>.");
  }

  return runBatch({
    filePath,
    requestId,
    runtime,
    failFast: flags["fail-fast"] === undefined ? undefined : getBoolean(flags, "fail-fast"),
    parallel: getNumber(flags, "parallel"),
    dryRun: getBoolean(flags, "preflight") || getBoolean(flags, "dry-run"),
    executeStep: (stepArgv) => execute(stepArgv, runtime)
  });
}

async function handleJob(command: string | undefined, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  const id = getString(flags, "id") ?? getString(flags, "job");
  if (!id) {
    return failure(requestId, "JOB_ID_REQUIRED", "Pass --id <jobId>.");
  }

  if (command === "status") {
    const job = await runtime.store.get(id);
    return job ? success(requestId, summarizeJob(job)) : failure(requestId, "JOB_NOT_FOUND", `Job not found: ${id}`);
  }

  if (command === "logs") {
    const tail = getNumber(flags, "tail") ?? 100;
    const logs = await runtime.runner.logs(id, tail);
    return success(requestId, { jobId: id, ...logs });
  }

  if (command === "cancel") {
    try {
      const job = await runtime.runner.cancel(id);
      return success(requestId, summarizeJob(job));
    } catch (error) {
      return failure(requestId, "JOB_CANCEL_FAILED", error instanceof Error ? error.message : String(error));
    }
  }

  return failure(requestId, "UNKNOWN_JOB_COMMAND", "Expected job status, job logs or job cancel.");
}

function refreshArgs(flags: Record<string, string | boolean>): string[] {
  const args = ["-executeMethod", "AlpsUnityCli.Batch.Refresh"];
  if (getBoolean(flags, "compile")) {
    args[1] = "AlpsUnityCli.Batch.RefreshAndCompile";
  }
  return args;
}

async function startUnityJob(kind: JobKind, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string, unityArgs: string[], result: Record<string, unknown>): Promise<CliResult> {
  const projectPath = resolve(getString(flags, "project") ?? process.cwd());
  const unityPath = getString(flags, "unity") ?? discoverUnity();
  if (!unityPath) {
    return failure(requestId, "UNITY_NOT_FOUND", "Unity.exe was not found.", ["Pass --unity <path-to-Unity.exe> or set UNITY_EXE."]);
  }

  if (typeof result.resultPath === "string") {
    await mkdir(dirname(result.resultPath), { recursive: true });
  }

  const timeoutMs = getNumber(flags, "timeout-ms");
  const job = await runtime.runner.start({ kind, projectPath, unityPath, args: unityArgs, timeoutMs, requestId, result });
  if (!getBoolean(flags, "wait")) {
    return success(requestId, summarizeJob(job));
  }

  const waitTimeoutMs = getNumber(flags, "wait-timeout-ms") ?? timeoutMs ?? 600000;
  const waited = await runtime.runner.wait(job.id, waitTimeoutMs);
  if (!terminalStatuses.has(waited.status)) {
    return failure(requestId, "WAIT_TIMEOUT", `Job did not reach a terminal state within ${waitTimeoutMs}ms.`, [
      `Run alps-unity job status --id ${job.id}.`,
      `Run alps-unity job logs --id ${job.id}.`
    ]);
  }
  return success(requestId, summarizeJob(waited));
}

function summarizeJob(job: JobRecord): Record<string, unknown> {
  return {
    jobId: job.id,
    kind: job.kind,
    status: job.status,
    requestId: job.requestId,
    projectPath: job.projectPath,
    unityPath: job.unityPath,
    logPath: job.logPath,
    pid: job.pid,
    exitCode: job.exitCode,
    createdAt: job.createdAt,
    updatedAt: job.updatedAt,
    startedAt: job.startedAt,
    finishedAt: job.finishedAt,
    command: job.command,
    result: job.result,
    error: job.error
  };
}

function help(): Record<string, unknown> {
  return {
    usage: "alps-unity <command> [options]",
    commands: [
      "job status --id <jobId>",
      "job logs --id <jobId> [--tail 100]",
      "job cancel --id <jobId>",
      "batch schema",
      "batch run <file.json> [--preflight] [--fail-fast true|false] [--parallel 1]",
      "scene hierarchy [--page-size 100] [--bridge-endpoint <url>]",
      "go find --name <name> [--bridge-endpoint <url>]",
      "component set <target> --component <type> --property <name> --value <value>",
      "script create <path> --content-file <file>",
      "screenshot capture <path>",
      "editor refresh --compile [--wait] [--project <path>] [--unity <Unity.exe>]",
      "test run [--platform EditMode|PlayMode] [--wait]",
      "test poll --id <jobId>",
      "build run [--target StandaloneWindows64] [--output <path>] [--wait]",
      "build status --id <jobId>",
      "build cancel --id <jobId>"
    ]
  };
}

if (import.meta.url === `file://${process.argv[1]}`) {
  execute(process.argv.slice(2)).then((result) => {
    printJson(result);
    process.exitCode = result.ok ? 0 : 1;
  });
}
