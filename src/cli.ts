#!/usr/bin/env node
import { realpathSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs, getBoolean, getString } from "./args.js";
import { bridgeCommandRegistry, executeBridgeCommand, type BridgeRuntime } from "./bridge/index.js";
import { handleBatch } from "./cli-commands/batch.js";
import { handleBuild } from "./cli-commands/build.js";
import { handleEditor } from "./cli-commands/editor.js";
import { handleJob } from "./cli-commands/job.js";
import { handleInit, handlePackage } from "./cli-commands/package.js";
import { handleTest } from "./cli-commands/test.js";
import type { CliCommandModule, Runtime } from "./cli-commands/types.js";
import { startUnityJob, summarizeJob } from "./cli-commands/unity-job.js";
import { handleCapabilitiesCommand } from "./capabilities.js";
import { handleDoctorCommand } from "./doctor.js";
import { handleInstancesCommand, handleTargetCommand } from "./instances.js";
import { handleDocsCommand, handleReflectCommand } from "./docs-reflect.js";
import { JobRunner } from "./job-runner.js";
import { handleCode } from "./runtime-code.js";
import { JobStore } from "./job-store.js";
import { createRequestId, failure, printJson, success } from "./output.js";
import { defaultDataDir, ensureWindows } from "./platform.js";
import { handleToolsCommand } from "./tools.js";
import { confirmDestructiveExecution } from "./security.js";
import { securityPolicyForCommand } from "./security-policy.js";
import type { CliResult } from "./types.js";

const CLI_COMMANDS = new Map<string, CliCommandModule>([
  ["job", { run: ({ command, flags, runtime, requestId }) => handleJob(command, flags, runtime, requestId) }],
  ["init", { run: ({ flags, runtime, requestId }) => handleInit(flags, runtime, requestId) }],
  ["package", { run: ({ command, args, flags, runtime, requestId }) => handlePackage(command, args, flags, runtime, requestId) }],
  ["batch", { run: ({ command, args, flags, runtime, requestId }) => handleBatch(command, args[0], flags, runtime, requestId) }],
  ["doctor", { run: ({ flags, runtime, requestId }) => handleDoctorCommand(flags, runtime, requestId) }],
  ["instances", { run: ({ command, flags, runtime, requestId }) => handleInstancesCommand(command, flags, runtime.dataDir, requestId) }],
  ["target", { run: ({ command, args, flags, runtime, requestId }) => handleTargetCommand(command, args, flags, runtime.dataDir, requestId) }],
  ["code", { run: ({ command, flags, runtime, requestId }) => handleCode(command, flags, runtime, requestId) }],
  ["tools", { run: ({ command, args, flags, runtime, requestId }) => handleToolsCommand(command, args, flags, runtime, requestId) }],
  ["capabilities", { run: ({ flags, runtime, requestId }) => handleCapabilitiesCommand(flags, runtime, requestId) }],
  ["docs", { run: ({ command, args, flags, runtime, requestId }) => handleDocsCommand(command, args, flags, runtime.dataDir, requestId, runtime) }],
  ["reflect", { run: ({ command, args, flags, runtime, requestId }) => handleReflectCommand(command, args, flags, runtime.dataDir, requestId) }],
  ["editor", { run: ({ command, flags, runtime, requestId }) => handleEditor(command, flags, runtime, requestId) }],
  ["test", { run: ({ command, flags, runtime, requestId }) => handleTest(command, flags, runtime, requestId) }],
  ["build", { run: ({ command, flags, runtime, requestId }) => handleBuild(command, flags, runtime, requestId) }]
]);

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

    const cliModule = CLI_COMMANDS.get(group);
    const cliResult = await cliModule?.run({
      command,
      args: parsed.positional.slice(2),
      flags: parsed.flags,
      runtime,
      requestId
    });
    if (cliResult) {
      return cliResult;
    }

    const bridgeSpec = bridgeCommandRegistry.lookup(group, command);
    if (bridgeSpec) {
      if (shouldRunBridgeAsJob(group, command, parsed.flags)) {
        const confirmation = await confirmDestructiveExecution(parsed.flags, {
          command: bridgeSpec.bridgeCommand,
          title: bridgeSpec.bridgeCommand,
          destructiveHint: bridgeSpec.destructiveHint,
          supportsDryRun: bridgeSpec.supportsDryRun,
          requestId
        });
        if (confirmation) {
          return confirmation;
        }
        const args = await bridgeJobArgs(bridgeSpec.bridgeCommand, parsed.flags, parsed.positional[2]);
        const jobArgs = ["-executeMethod", "UnityCli.UnityCliBatch.RunBridgeCommand", "-unityCliBridgeCommand", bridgeSpec.bridgeCommand, "-unityCliBridgeRequestId", requestId, "-unityCliBridgeArgs", JSON.stringify(args)];
        return startUnityJob(group === "profiler" ? "profiler" : "probuilder", parsed.flags, runtime, requestId, jobArgs, {
          action: bridgeSpec.bridgeCommand,
          bridgeCommand: bridgeSpec.bridgeCommand,
          args
        });
      }
      return executeBridgeCommand(requestId, bridgeSpec.bridgeCommand, parsed.flags, parsed.positional[2], runtime);
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

async function bridgeJobArgs(command: string, flags: Record<string, string | boolean>, positionalTarget: string | undefined): Promise<Record<string, unknown>> {
  const runtime: BridgeRuntime = { bridgeExecute: async (payload) => ({ ok: true, data: { bridgeArgs: payload.args, dryRun: payload.dryRun, security: payload.security } }) };
  const result = await executeBridgeCommand("dry_run", command, flags, positionalTarget, runtime);
  return ((result.data as { bridgeArgs?: Record<string, unknown> } | undefined)?.bridgeArgs ?? {}) as Record<string, unknown>;
}

function shouldRunBridgeAsJob(group: string | undefined, command: string | undefined, flags: Record<string, string | boolean>): boolean {
  return getBoolean(flags, "job") && ((group === "profiler" && command === "capture") || group === "probuilder");
}

export function isDestructiveCliCommand(group: string | undefined, command: string | undefined): boolean {
  const spec = bridgeCommandRegistry.lookup(group, command);
  return spec?.destructiveHint === true || securityPolicyForCommand(spec?.bridgeCommand)?.destructiveHint === true;
}

function isPackageManagerError(error: unknown): error is Error & { code: string; nextActions: string[] } {
  return error instanceof Error && typeof (error as { code?: unknown }).code === "string" && Array.isArray((error as { nextActions?: unknown }).nextActions);
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
      "job describe --id <jobId>",
      "job cancel --id <jobId>",
      "init [--project <path>] [--unity <Unity.exe>] [--wait]",
      "init --check [--project <path>]",
      "package [--format json|text]",
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
      "instances prune",
      "target get",
      "target set <id|alias>",
      "target clear",
      "target alias set <instanceId> <alias> [--force]",
      "target alias unset <alias>",
      "target alias list",
      "doctor [--bridge-endpoint <url>] [--json]",
      "docs [--format json|text]",
      "docs topic <topic> [--format json|text]",
      "docs get <api> [--docs-dir <path>] [--unity-version <version>] [--format json|text]",
      "docs search <query> [--docs-dir <path>] [--page-size 20 --cursor 0] [--format json|text]",
      "reflect type <name> [--reflect-dir <path>] [--unity-version <version>] [--format json|text]",
      "reflect member <type.member> [--reflect-dir <path>] [--format json|text]",
      "reflect search <query> [--reflect-dir <path>] [--page-size 20 --cursor 0] [--format json|text]",
      "tools list [--group <group>] [--include-custom] [--format json|text]",
      "tools groups",
      "tools search <keyword> [--format json|text]",
      "tools describe <tool>",
      "tools run <name> [--input <json>] [--input-file <path>] [--timeout-ms <n>] [--fingerprint-mode warn|block] [--artifact-dir <path>] [--runtime] [--target <instance>]",
      "capabilities [--json] [--bridge-endpoint <url>]",
      "code exec (--file <path>|--stdin|--code <text>) [--channel live|batchmode] [--refs <list>] [--define <symbol>] [--args <json>] [--timeout-ms <n>] [--job] [--artifact-dir <path>]",
      "scene active [规划中]",
      "scene hierarchy [--page-size 100] [--bridge-endpoint <url>]",
      "scene build-settings [规划中]",
      "scene screenshot <path> [--width 1280 --height 720 --format png --overwrite] [--bridge-endpoint <url>]",
      "scene create <path> [规划中]",
      "scene load <path|build-index> [规划中]",
      "scene save [--path <path>] [规划中]",
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
      "graphics pipeline [--bridge-endpoint <url>]",
      "graphics volume [target] [--bridge-endpoint <url>]",
      "graphics screenshot <path> [--include-diagnostics] [--bridge-endpoint <url>]",
      "physics settings [--mode 3d|2d] [--gravity-y <n>] [--bridge-endpoint <url>]",
      "physics raycast [--origin-x 0 --origin-y 0 --origin-z 0 --direction-z 1 --distance 100] [--bridge-endpoint <url>]",
      "physics simulation [--steps 1 --delta-time 0.02 --simulate] [--bridge-endpoint <url>]",
      "profiler status [--bridge-endpoint <url>]",
      "profiler start [--deep-profiling] [--bridge-endpoint <url>]",
      "profiler stop [--bridge-endpoint <url>]",
      "profiler pause [--paused true|false] [--bridge-endpoint <url>]",
      "profiler counter [--category <name>] [--counter <name>] [--bridge-endpoint <url>]",
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
      "go inspect <target> [规划中]",
      "go create <name> [规划中]",
      "go modify <target> [规划中]",
      "go delete <target> [规划中]",
      "component add <target> --component <type> [规划中]",
      "component remove <target> --component <type> [规划中]",
      "component set <target> --component <type> --property <name> --value <value> [规划中]",
      "script create <path> --content-file <file> [规划中]",
      "script read <path> [规划中]",
      "script edit <path> [规划中]",
      "script validate <path> [规划中]",
      "screenshot capture <path>",
      "editor refresh --compile [--wait] [--project <path>] [--unity <Unity.exe>]",
      "test run [--platform EditMode|PlayMode] [--wait]",
      "test poll|status --id <jobId>",
      "test logs --id <jobId> [--tail 100]",
      "test cancel --id <jobId>",
      "build run [--target StandaloneWindows64] [--output <path>] [--wait]",
      "build status --id <jobId>",
      "build logs --id <jobId> [--tail 100]",
      "build cancel --id <jobId>"
    ],
    supportLegend: {
      "已路由可执行": "通过 live Unity Editor Bridge /bridge/execute 执行。",
      "已路由可执行 + 可 batch/job": "同时支持 live Unity Editor Bridge /bridge/execute 和 batchmode/job runner 两种执行通道。",
      "仅 batch/job": "通过 Unity batchmode/job runner 执行，不等同于 live Bridge 路由。",
      "仅 CLI 离线": "不需要 Unity 连接，在本地 CLI 完成。",
      "规划中": "CLI 契约已声明，作为后续 bundled Unity Bridge handler 覆盖范围保留。"
    }
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
