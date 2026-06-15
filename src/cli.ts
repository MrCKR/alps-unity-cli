#!/usr/bin/env node
import { realpathSync } from "node:fs";
import { mkdir, readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs, getBoolean, getNumber, getString } from "./args.js";
import { batchJsonSchema, runBatch } from "./batch.js";
import { bridgeCommandName, executeBridgeCommand, executeBridgePayload, type BridgeRuntime } from "./bridge-commands.js";
import { handleCapabilitiesCommand } from "./capabilities.js";
import { handleDoctorCommand, type DoctorRuntime } from "./doctor.js";
import { handleInstancesCommand } from "./instances.js";
import { handleDocsCommand, handleReflectCommand } from "./docs-reflect.js";
import { handleDocsTopicCommand } from "./docs-topic.js";
import { JobRunner } from "./job-runner.js";
import { handleCode } from "./runtime-code.js";
import { JobStore } from "./job-store.js";
import { createRequestId, failure, printJson, success } from "./output.js";
import { defaultDataDir, discoverUnity, ensureWindows } from "./platform.js";
import { handleToolsCommand, type ToolsRuntime } from "./tools.js";
import type { CliResult, JobKind, JobRecord } from "./types.js";

interface Runtime extends BridgeRuntime, ToolsRuntime, DoctorRuntime {
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

    if (getBoolean(parsed.flags, "version")) {
      return success(requestId, { version: await packageVersion() });
    }

    if (!group || getBoolean(parsed.flags, "help")) {
      return success(requestId, help());
    }

    ensureWindows();

    if (group === "job") {
      return handleJob(command, parsed.flags, runtime, requestId);
    }

    if (group === "init") {
      return await handleInit(parsed.flags, runtime, requestId);
    }

    if (group === "package") {
      return await handlePackage(command, parsed.positional.slice(2), parsed.flags, runtime, requestId);
    }

    if (group === "batch") {
      return handleBatch(command, parsed.positional[2], parsed.flags, runtime, requestId);
    }

    if (group === "doctor") {
      return handleDoctorCommand(parsed.flags, runtime, requestId);
    }

    if (group === "instances") {
      return handleInstancesCommand(command, parsed.flags, runtime.dataDir, requestId);
    }

    if (group === "code") {
      return handleCode(command, parsed.flags, runtime, requestId);
    }

    if (group === "tools") {
      return handleToolsCommand(command, parsed.positional.slice(2), parsed.flags, runtime, requestId);
    }

    if (group === "capabilities") {
      return handleCapabilitiesCommand(parsed.flags, runtime, requestId);
    }

    if (group === "docs") {
      return handleDocsCommand(command, parsed.positional.slice(2), parsed.flags, runtime.dataDir, requestId);
    }

    if (group === "reflect") {
      return handleReflectCommand(command, parsed.positional.slice(2), parsed.flags, runtime.dataDir, requestId);
    }

    if (group === "docs-topic") {
      return handleDocsTopicCommand(command, parsed.flags, requestId);
    }

    const bridgeCommand = bridgeCommandName(group, command);
    if (bridgeCommand) {
      if (shouldRunBridgeAsJob(group, command, parsed.flags)) {
        const args = await bridgeJobArgs(bridgeCommand, parsed.flags, parsed.positional[2]);
        const jobArgs = ["-executeMethod", "AlpsUnityCli.Batch.RunBridgeCommand", "-alpsBridgeCommand", bridgeCommand, "-alpsBridgeRequestId", requestId, "-alpsBridgeArgs", JSON.stringify(args)];
        return startUnityJob(group === "profiler" ? "profiler" : "probuilder", parsed.flags, runtime, requestId, jobArgs, {
          action: bridgeCommand,
          bridgeCommand,
          args
        });
      }
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

    return failure(requestId, "UNKNOWN_COMMAND", `Unknown command: ${argv.join(" ")}`, ["Run unity_cli --help."]);
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
    return failure(requestId, "BATCH_FILE_REQUIRED", "Pass a batch JSON file: unity_cli batch run <file>.");
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
    try {
      const logs = await runtime.runner.logs(id, tail);
      return success(requestId, { jobId: id, ...logs });
    } catch (error) {
      if (error instanceof Error && error.message.startsWith("Job not found")) {
        return failure(requestId, "JOB_NOT_FOUND", `Job not found: ${id}`);
      }
      return failure(requestId, "JOB_LOGS_FAILED", error instanceof Error ? error.message : String(error));
    }
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

async function bridgeJobArgs(command: string, flags: Record<string, string | boolean>, positionalTarget: string | undefined): Promise<Record<string, unknown>> {
  const runtime: BridgeRuntime = { bridgeExecute: async (payload) => ({ ok: true, data: { bridgeArgs: payload.args } }) };
  const result = await executeBridgeCommand("dry_run", command, flags, positionalTarget, runtime);
  return ((result.data as { bridgeArgs?: Record<string, unknown> } | undefined)?.bridgeArgs ?? {}) as Record<string, unknown>;
}

function shouldRunBridgeAsJob(group: string | undefined, command: string | undefined, flags: Record<string, string | boolean>): boolean {
  return getBoolean(flags, "job") && ((group === "profiler" && command === "capture") || group === "probuilder");
}

function refreshArgs(flags: Record<string, string | boolean>): string[] {
  return ["-executeMethod", getBoolean(flags, "compile") ? "AlpsUnityCli.Batch.RefreshAndCompile" : "AlpsUnityCli.Batch.Refresh"];
}

async function handleInit(flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  const projectPath = resolve(getString(flags, "project") ?? process.cwd());
  const { checkPackageBridge, ensurePackageBridge } = await import("./package-manager.js");

  if (getBoolean(flags, "check")) {
    const bridge = await checkPackageBridge(projectPath);
    return success(requestId, {
      action: "init_check",
      status: "installed",
      projectPath,
      bridge,
      nextActions: ["Unity package com.unity_cli is installed via a local manifest file: dependency."]
    });
  }

  const bridge = await ensurePackageBridge(projectPath);
  const liveRefresh = await refreshOpenEditor(requestId, flags, runtime);
  return maybeResolvePackage("init", flags, runtime, requestId, projectPath, {
    action: "init",
    status: "package_installed",
    bridge,
    liveRefresh,
    nextActions: liveRefresh.status === "triggered"
      ? ["Wait for Unity to finish compiling/importing, then retry /health and Bridge commands against the live endpoint."]
      : ["Open the Unity project or run unity_cli package resolve --project <path> --unity <Unity.exe> --wait to refresh packages."]
  });
}

async function refreshOpenEditor(requestId: string, flags: Record<string, string | boolean>, runtime: Runtime): Promise<Record<string, unknown>> {
  const endpoint = getString(flags, "bridge-endpoint") ?? runtime.bridgeEndpoint ?? process.env.ALPS_UNITY_BRIDGE_ENDPOINT ?? "http://127.0.0.1:47392";
  if (!runtime.bridgeExecute && !(await isBridgeReachable(endpoint))) {
    return { status: "skipped", reason: "bridge_endpoint_absent", endpoint };
  }
  const result = await executeBridgePayload(`${requestId}_refresh`, "editor.menu", { path: "Assets/Refresh", timeoutMs: 7000 }, { ...flags, "bridge-endpoint": endpoint }, runtime);
  if (result.ok) {
    return { status: "triggered", command: "editor.menu", path: "Assets/Refresh", endpoint };
  }
  return { status: "failed", command: "editor.menu", path: "Assets/Refresh", endpoint, code: result.error?.code, message: result.error?.message };
}

async function isBridgeReachable(endpoint: string): Promise<boolean> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 1500);
  try {
    const response = await fetch(new URL("/health", endpoint), { signal: controller.signal });
    return response.ok;
  } catch {
    return false;
  } finally {
    clearTimeout(timeout);
  }
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
    return info ? success(requestId, { projectPath, package: info }) : failure(requestId, "PACKAGE_NOT_FOUND", `Package not found: ${name}`, ["Run unity_cli package list to inspect installed packages."]);
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
    return success(requestId, { ...result, projectPath, status: result.status ?? "manifest_updated", resolve: "skipped", nextActions: result.nextActions ?? ["Pass --unity <path-to-Unity.exe> or set UNITY_EXE to run Unity PackageManager resolve as a job."] });
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
    return failure(requestId, "E_EXECUTION_TIMEOUT", `Job did not reach a terminal state within ${waitTimeoutMs}ms.`, [
      `Run unity_cli job status --id ${job.id}.`,
      `Run unity_cli job logs --id ${job.id}.`,
      `Run unity_cli job cancel --id ${job.id} if the Unity process should be stopped.`
    ], {
      jobId: job.id,
      timeoutMs: waitTimeoutMs,
      cancelAttempted: false,
      cancelled: false,
      processKilled: false,
      domainReloadObserved: false
    });
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

async function packageVersion(): Promise<string> {
  const packagePath = fileURLToPath(new URL("../package.json", import.meta.url));
  const packageJson = JSON.parse(await readFile(packagePath, "utf8")) as { version?: string };
  return packageJson.version ?? "0.0.0";
}

function help(): Record<string, unknown> {
  return {
    usage: "unity_cli <command> [options]",
    commands: [
      "job status --id <jobId>",
      "job logs --id <jobId> [--tail 100]",
      "job cancel --id <jobId>",
      "init [--project <path>] [--unity <Unity.exe>] [--wait]",
      "init --check [--project <path>]",
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
      "instances list",
      "instances cleanup",
      "doctor [--bridge-endpoint <url>] [--json]",
      "docs get <api> [--docs-dir <path>] [--unity-version <version>] [--format json|text]",
      "docs search <query> [--docs-dir <path>] [--page-size 20 --cursor 0] [--format json|text]",
      "reflect type <name> [--reflect-dir <path>] [--unity-version <version>] [--format json|text]",
      "reflect member <type.member> [--reflect-dir <path>] [--format json|text]",
      "reflect search <query> [--reflect-dir <path>] [--page-size 20 --cursor 0] [--format json|text]",
      "tools list [--group <group>] [--include-custom] [--format json|text]",
      "tools groups",
      "tools search <keyword> [--format json|text]",
      "tools describe <tool>",
      "capabilities [--json] [--bridge-endpoint <url>]",
      "docs-topic [topic] [--format json|text]",
      "code exec (--file <path>|--stdin|--code <text>) [--channel live|batchmode] [--refs <list>] [--define <symbol>] [--args <json>] [--timeout-ms <n>] [--job] [--artifact-dir <path>]",
      "custom-tool list [--project <path>] [--instance <id>] [--bridge-endpoint <url>]",
      "custom-tool run <name> [--project <path>] [--instance <id>] [--input <json>|--input-file <path>|--stdin] [--timeout-ms 30000] [--job] [--artifact-dir <path>] [--bridge-endpoint <url>]",
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
      "profiler status [--bridge-endpoint <url>]",
      "profiler start [--deep-profiling] [--bridge-endpoint <url>]",
      "profiler stop [--bridge-endpoint <url>]",
      "profiler pause [--paused true|false] [--bridge-endpoint <url>]",
      "profiler counter [--category <name> --counter <name>] [--bridge-endpoint <url>]",
      "profiler memory [--bridge-endpoint <url>]",
      "profiler frame [--module <name>] [--bridge-endpoint <url>]",
      "profiler capture <path> [--duration-ms 1000 --frames 1 --format json|raw --overwrite] [--job --wait] [--bridge-endpoint <url>]",
      "profiler export <path> [--format json|raw --overwrite] [--bridge-endpoint <url>]",
      "probuilder shape <cube|sphere|cylinder|plane|capsule> [--name <name> --parent <target>] [--job --wait] [--bridge-endpoint <url>]",
      "probuilder create --shape <shape> [--name <name>] [--job --wait] [--bridge-endpoint <url>]",
      "probuilder edit <target> --operation <name> [--value <value>] [--job --wait] [--bridge-endpoint <url>]",
      "probuilder csg <target> --source <source> --operation union|subtract|intersect [--job --wait] [--bridge-endpoint <url>]",
      "animation clip <Assets/Clip.anim> [--create --overwrite --frame-rate 60] [--bridge-endpoint <url>]",
      "animation state <target> [--layer 0] [--bridge-endpoint <url>]",
      "animation play <target> --state <name> [--layer 0 --normalized-time 0] [--bridge-endpoint <url>]",
      "animation stop <target> [--bridge-endpoint <url>]",
      "vfx graph <Assets/Effect.vfx> [--create --overwrite] [--bridge-endpoint <url>]",
      "vfx effect <target> [--graph <Assets/Effect.vfx> --add] [--bridge-endpoint <url>]",
      "ui canvas/create <name> [--bridge-endpoint <url>]",
      "ui text/create <name> --parent <target> [--text <text> --width 160 --height 40] [--bridge-endpoint <url>]",
      "ui button/create <name> --parent <target> [--label <text>] [--bridge-endpoint <url>]",
      "ui image/create <name> --parent <target> [--sprite <Assets/path.png>] [--bridge-endpoint <url>]",
      "ui document/info <target|Assets/path.uxml> [--uss <Assets/path.uss>] [--bridge-endpoint <url>]",
      "ui document/create --uxml <Assets/path.uxml> [--uss <Assets/path.uss> --panel-settings <Assets/path.asset> --overwrite] [--bridge-endpoint <url>]",
      "ui hierarchy [target] [--page-size 100 --cursor <n>] [--bridge-endpoint <url>]",
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
  const modulePath = resolve(fileURLToPath(metaUrl));
  const entryPath = resolve(argvPath);
  return modulePath === entryPath || safeRealpath(modulePath) === safeRealpath(entryPath);
}

function safeRealpath(path: string): string {
  try {
    return realpathSync(path);
  } catch {
    return path;
  }
}

if (isCliEntryPoint(import.meta.url, process.argv[1])) {
  execute(process.argv.slice(2)).then((result) => {
    printJson(result);
    process.exitCode = result.ok ? 0 : 1;
  });
}
