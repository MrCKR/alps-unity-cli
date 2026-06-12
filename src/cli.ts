#!/usr/bin/env node
import { mkdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs, getBoolean, getNumber, getString } from "./args.js";
import { batchJsonSchema, runBatch } from "./batch.js";
import { bridgeCommandName, executeBridgeCommand, type BridgeRuntime } from "./bridge-commands.js";
import { JobRunner } from "./job-runner.js";
import { handleCode } from "./runtime-code.js";
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

    if (group === "package") {
      return await handlePackage(command, parsed.positional.slice(2), parsed.flags, runtime, requestId);
    }

    if (group === "batch") {
      return handleBatch(command, parsed.positional[2], parsed.flags, runtime, requestId);
    }

    if (group === "code") {
      return handleCode(command, parsed.flags, runtime, requestId);
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
    if (isPackageManagerError(error)) {
      return failure(requestId, error.code, error.message, error.nextActions);
    }
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

async function handlePackage(command: string | undefined, args: string[], flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  const projectPath = resolve(getString(flags, "project") ?? process.cwd());
  const {
    addScopedRegistry,
    clearRegistry,
    installPackage,
    packageInfo,
    parsePackageSpec,
    parseScopes,
    readSnapshot,
    removePackage,
    removeScopedRegistry,
    searchRegistry,
    setRegistry,
    validatePackageName,
    validateRegistryUrl
  } = await import("./package-manager.js");

  if (command === "list") {
    return success(requestId, await readSnapshot(projectPath));
  }

  if (command === "info") {
    const name = validatePackageName(args[0] ?? getString(flags, "name"));
    const info = await packageInfo(projectPath, name);
    return info ? success(requestId, { projectPath, package: info }) : failure(requestId, "PACKAGE_NOT_FOUND", `Package not found: ${name}`, ["Run alps-unity package list to inspect installed packages."]);
  }

  if (command === "search") {
    const query = validatePackageName(args[0] ?? getString(flags, "query"));
    const registry = validateRegistryUrl(getString(flags, "registry") ?? (await readSnapshot(projectPath)).registry ?? "https://packages.unity.com");
    return success(requestId, { registry, query, packages: await searchRegistry(registry, query) });
  }

  if (command === "install") {
    const spec = parsePackageSpec(validatePackageName(args[0] ?? getString(flags, "name")), getString(flags, "version"));
    const manifest = await installPackage(projectPath, spec);
    return maybeResolvePackage(command, flags, runtime, requestId, projectPath, { action: "install", package: spec.name, ...manifest });
  }

  if (command === "remove") {
    const name = validatePackageName(args[0] ?? getString(flags, "name"));
    const manifest = await removePackage(projectPath, name);
    return maybeResolvePackage(command, flags, runtime, requestId, projectPath, { action: "remove", package: name, ...manifest });
  }

  if (command === "resolve") {
    return maybeResolvePackage(command, flags, runtime, requestId, projectPath, { action: "resolve" });
  }

  if (command === "registry") {
    const action = args[0] ?? getString(flags, "action") ?? "get";
    if (action === "get") {
      const snapshot = await readSnapshot(projectPath);
      return success(requestId, { projectPath, manifestPath: snapshot.manifestPath, registry: snapshot.registry });
    }
    if (action === "set") {
      return success(requestId, { projectPath, ...(await setRegistry(projectPath, validateRegistryUrl(getString(flags, "url") ?? args[1]))) });
    }
    if (action === "clear") {
      return success(requestId, { projectPath, ...(await clearRegistry(projectPath)) });
    }
    return failure(requestId, "UNKNOWN_PACKAGE_REGISTRY_COMMAND", "Expected package registry get, set or clear.");
  }

  if (command === "scoped-registry") {
    const action = args[0] ?? getString(flags, "action") ?? "list";
    if (action === "list") {
      const snapshot = await readSnapshot(projectPath);
      return success(requestId, { projectPath, manifestPath: snapshot.manifestPath, scopedRegistries: snapshot.scopedRegistries });
    }
    if (action === "add") {
      const name = validatePackageName(getString(flags, "name") ?? args[1]);
      const url = validateRegistryUrl(getString(flags, "url"));
      const scopes = parseScopes(getString(flags, "scope") ?? getString(flags, "scopes"));
      return success(requestId, { projectPath, ...(await addScopedRegistry(projectPath, { name, url, scopes })) });
    }
    if (action === "remove") {
      const name = validatePackageName(getString(flags, "name") ?? args[1]);
      return success(requestId, { projectPath, ...(await removeScopedRegistry(projectPath, name)) });
    }
    return failure(requestId, "UNKNOWN_SCOPED_REGISTRY_COMMAND", "Expected package scoped-registry list, add or remove.");
  }

  return failure(requestId, "UNKNOWN_PACKAGE_COMMAND", "Expected package list, info, search, install, remove, resolve, registry or scoped-registry.");
}

async function maybeResolvePackage(command: string, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string, projectPath: string, result: Record<string, unknown>): Promise<CliResult> {
  const unityPath = getString(flags, "unity") ?? discoverUnity();
  if (!unityPath && !getBoolean(flags, "job")) {
    return success(requestId, { ...result, projectPath, status: "manifest_updated", resolve: "skipped", nextActions: ["Pass --unity <path-to-Unity.exe> or set UNITY_EXE to run Unity PackageManager resolve as a job."] });
  }
  if (!unityPath) {
    return failure(requestId, "UNITY_NOT_FOUND", "Unity.exe was not found.", ["Pass --unity <path-to-Unity.exe> or set UNITY_EXE."]);
  }
  const { ensurePackageBridge } = await import("./package-manager.js");
  const bridge = await ensurePackageBridge(projectPath);
  return startUnityJob("package", flags, runtime, requestId, ["-executeMethod", "AlpsUnityCli.Batch.ResolvePackages", "-alpsPackageAction", command], { ...result, bridge });
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

function isPackageManagerError(error: unknown): error is Error & { code: string; nextActions: string[] } {
  return error instanceof Error && typeof (error as { code?: unknown }).code === "string" && Array.isArray((error as { nextActions?: unknown }).nextActions);
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
    ...(job.error ? { error: job.error } : {})
  };
}

function help(): Record<string, unknown> {
  return {
    usage: "alps-unity <command> [options]",
    commands: [
      "job status --id <jobId>",
      "job logs --id <jobId> [--tail 100]",
      "job cancel --id <jobId>",
      "package list [--project <path>]",
      "package info <name> [--project <path>]",
      "package search <query> [--registry <url>]",
      "package install <name[@version]> [--project <path>] [--unity <Unity.exe>] [--wait]",
      "package remove <name> [--project <path>] [--unity <Unity.exe>] [--wait]",
      "package resolve [--project <path>] [--unity <Unity.exe>] [--wait]",
      "package registry get|set|clear [--url <url>]",
      "package scoped-registry list|add|remove --name <name> --url <url> --scope <scope>",
      "batch schema",
      "batch run <file.json> [--preflight] [--fail-fast true|false] [--parallel 1]",
      "code exec (--file <path>|--stdin|--code <text>) [--channel live|batchmode] [--refs <list>] [--define <symbol>] [--args <json>] [--timeout-ms <n>] [--job] [--artifact-dir <path>]",
      "scene hierarchy [--page-size 100] [--bridge-endpoint <url>]",
      "scene screenshot <path> [--width 1280 --height 720 --format png --overwrite] [--bridge-endpoint <url>]",
      "prefab info <path> [--bridge-endpoint <url>]",
      "prefab create <source> --path <Assets/Prefab.prefab> [--overwrite] [--bridge-endpoint <url>]",
      "prefab stage-open <path> [--bridge-endpoint <url>]",
      "prefab stage-save [--path <path>] [--bridge-endpoint <url>]",
      "prefab stage-close [--path <path>] [--save] [--bridge-endpoint <url>]",
      "prefab hierarchy <path> [--page-size 100 --cursor <n>] [--bridge-endpoint <url>]",
      "material info <path> [--bridge-endpoint <url>]",
      "material create <path> --shader <name> [--overwrite] [--bridge-endpoint <url>]",
      "material assign --target <go> --material <path> [--slot <n>] [--bridge-endpoint <url>]",
      "material set <path> --property <name> --value <value> [--type color|float|vector|texture] [--bridge-endpoint <url>]",
      "graphics quality [--quality-level <n>] [--v-sync-count <n>] [--bridge-endpoint <url>]",
      "graphics settings [--anti-aliasing <n>] [--shadow-distance <n>] [--bridge-endpoint <url>]",
      "graphics screenshot <path> [--include-diagnostics] [--bridge-endpoint <url>]",
      "physics settings [--mode 3d|2d] [--gravity-y <n>] [--bridge-endpoint <url>]",
      "physics raycast [--origin-x 0 --origin-y 0 --origin-z 0 --direction-z 1 --distance 100] [--bridge-endpoint <url>]",
      "physics simulation [--steps 1 --delta-time 0.02 --simulate] [--bridge-endpoint <url>]",
      "camera list [--bridge-endpoint <url>]",
      "camera info <target> [--bridge-endpoint <url>]",
      "camera screenshot <target> --output <path> [--width 1280 --height 720 --format png --transparent --overwrite] [--bridge-endpoint <url>]",
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

export function isCliEntryPoint(metaUrl: string, argvPath: string | undefined): boolean {
  if (!argvPath) {
    return false;
  }
  return resolve(fileURLToPath(metaUrl)) === resolve(argvPath);
}

if (isCliEntryPoint(import.meta.url, process.argv[1])) {
  execute(process.argv.slice(2)).then((result) => {
    printJson(result);
    process.exitCode = result.ok ? 0 : 1;
  });
}
