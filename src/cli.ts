#!/usr/bin/env node
import { realpathSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs, getBoolean, getString } from "./args.js";
import { registerBuiltinExtensionContributions } from "./extensions/index.js";
import { handleExtensionsCommand } from "./cli-commands/extensions.js";
import { handleContextCommand } from "./context/commands.js";
import { handleInit } from "./cli-commands/package.js";
import { handleBatch } from "./cli-commands/batch.js";
import { handleJob } from "./cli-commands/job.js";
import type { CliCommandModule, Runtime } from "./cli-commands/types.js";
import { summarizeJob } from "./cli-commands/unity-job.js";
import { JobRunner } from "./job-runner.js";
import { JobStore } from "./job-store.js";
import { createRequestId, failure, printJson, success } from "./output.js";
import { defaultDataDir, ensureWindows } from "./platform.js";
import { handleToolsCommand } from "./tools.js";
import { handleDoctorCommand } from "./doctor.js";
import { handleInstancesCommand, handleTargetCommand } from "./instances.js";
import { confirmDestructiveExecution } from "./security.js";
import type { CliResult } from "./types.js";

/**
 * 用户可见顶层命令分为能力入口(tools/context)与 CLI 控制面。
 * Unity 能力域不得恢复 scene/asset/package 等顶层入口。
 */
const CLI_COMMANDS = new Map<string, CliCommandModule>([
  ["tools", { run: ({ command, args, flags, runtime, requestId }) => handleToolsCommand(command, args, flags, runtime, requestId) }],
  ["context", { run: ({ command, args, flags, runtime, requestId }) => handleContextCommand(command, args, flags, runtime, requestId) }],
  ["extensions", { run: ({ command, args, flags, runtime, requestId }) => handleExtensionsCommand(command, args, flags, runtime, requestId) }],
  ["init", { run: ({ flags, runtime, requestId }) => handleInit(flags, runtime, requestId) }],
  ["target", { run: ({ command, args, flags, runtime, requestId }) => handleTopLevelTarget(command, args, flags, runtime, requestId) }],
  ["instances", { run: ({ command, flags, runtime, requestId }) => handleTopLevelInstances(command, flags, runtime, requestId) }],
  ["jobs", { run: ({ command, args, flags, runtime, requestId }) => handleTopLevelJobs(command, args, flags, runtime, requestId) }],
  ["batch", { run: ({ command, args, flags, runtime, requestId }) => handleTopLevelBatch(command, args, flags, runtime, requestId) }],
  ["doctor", { run: ({ flags, runtime, requestId }) => handleDoctorCommand(flags, runtime, requestId) }]
]);

export async function execute(argv: string[], runtime = createRuntime()): Promise<CliResult> {
  // Register built-in extension contributions before any tool discovery or bridge command routing.
  registerBuiltinExtensionContributions();

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

    // Bridge registry 仅是内部路由，不再作为顶层执行入口(README Project Baseline)。
    // 旧顶层入口(scene active、go find、camera list、package list、build run 等)已迁移为 tools run。
    return failure(requestId, "UNKNOWN_COMMAND", `Unknown command: ${argv.join(" ")}`, [
      "Top-level commands are: tools, context, extensions, init, target, instances, jobs, batch, doctor.",
      "Run unity_cli tools group to discover Unity tools, then unity_cli tools run <tool> --input <json>.",
      "Run unity_cli instances list and unity_cli target set <id|alias> to choose an Editor target.",
      "Run unity_cli --help."
    ]);
  } catch (error) {
    return failure(requestId, "COMMAND_FAILED", error instanceof Error ? error.message : String(error), [
      "Run unity_cli --help for available commands.",
      "Run unity_cli doctor to diagnose the environment."
    ]);
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
    return success(requestId, await summarizeJob(job));
  } catch (error) {
    return failure(requestId, "WORKER_FAILED", error instanceof Error ? error.message : String(error));
  }
}

/** 顶层 target 控制面：管理默认 Unity 实例与 alias。 */
async function handleTopLevelTarget(
  command: string | undefined,
  args: string[],
  flags: Record<string, string | boolean>,
  runtime: Runtime,
  requestId: string
): Promise<CliResult> {
  if (!command || command === "current") {
    return handleTargetCommand("current", [], flags, runtime.dataDir, requestId);
  }
  if (command === "set") {
    return handleTargetCommand("set", args, flags, runtime.dataDir, requestId);
  }
  if (command === "clear") {
    return handleTargetCommand("clear", [], flags, runtime.dataDir, requestId);
  }
  if (command === "alias") {
    return handleTargetCommand("alias", args, flags, runtime.dataDir, requestId);
  }
  return failure(requestId, "UNKNOWN_TARGET_COMMAND", "Expected target current, target set <id|alias>, target clear, or target alias set|unset|list.", [
    "Run unity_cli instances list to inspect registered targets.",
    "Run unity_cli target alias set <instanceId> <alias> for a stable alias."
  ]);
}

/** 顶层 instances 控制面：读取和清理 Unity 实例注册表。 */
async function handleTopLevelInstances(
  command: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: Runtime,
  requestId: string
): Promise<CliResult> {
  const subCommand = command ?? "list";
  if (subCommand === "prune") {
    const confirmation = await confirmDestructiveExecution(flags, {
      command: "instances.prune",
      title: "Prune Unity Instances",
      destructiveHint: true,
      supportsDryRun: false,
      requestId
    });
    if (confirmation) return confirmation;
  }
  return handleInstancesCommand(subCommand, flags, runtime.dataDir, requestId);
}

/** 顶层 jobs 控制面：管理 CLI job runner 队列与日志。 */
async function handleTopLevelJobs(
  command: string | undefined,
  args: string[],
  flags: Record<string, string | boolean>,
  runtime: Runtime,
  requestId: string
): Promise<CliResult> {
  const mergedFlags = positionalIdFlags(args, flags);
  if (command === "cancel") {
    const confirmation = await confirmDestructiveExecution(mergedFlags, {
      command: "jobs.cancel",
      title: "Cancel Job",
      destructiveHint: true,
      supportsDryRun: false,
      requestId
    });
    if (confirmation) return confirmation;
  }
  return handleJob(command, mergedFlags, runtime, requestId);
}

/** 顶层 batch 控制面：执行 CLI batch 文件或输出 batch schema。 */
async function handleTopLevelBatch(
  command: string | undefined,
  args: string[],
  flags: Record<string, string | boolean>,
  runtime: Runtime,
  requestId: string
): Promise<CliResult> {
  return handleBatch(command, args[0], flags, runtime, requestId);
}

/** 将 `jobs status <id>` 形式转换为现有 handler 使用的 `--id`。 */
function positionalIdFlags(args: string[], flags: Record<string, string | boolean>): Record<string, string | boolean> {
  if (flags.id !== undefined || flags.job !== undefined || !args[0]) {
    return flags;
  }
  return { ...flags, id: args[0] };
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
      "tools group [--include-custom] [--format json|text]",
      "tools group list <group> [--include-custom] [--format json|text]",
      "tools describe <tool>",
      "tools run <name> [--input <json>] [--input-file <path>] [--timeout-ms <n>] [--fingerprint-mode warn|block] [--artifact-dir <path>] [--runtime] [--target <instance>]",
      "tools search <keyword> [--format json|text]",
      "context list [--category <cat>] [--include-custom] [--format json|text]",
      "context describe <name>",
      "context read <name> [--input <json>] [--target <instance>] [--bridge-endpoint <url>]",
      "extensions list [--project <path>] [--format json|text]",
      "extensions install <id|name> [--project <path>]",
      "extensions remove <id|name> [--project <path>]",
      "extensions doctor [--project <path>] [--format json|text]",
      "init [--project <path>] [--agent <pi|claude|.agent>] [--unity <Unity.exe>] [--wait]  # auto-installs Skill when .pi/.claude/.agent exists",
      "init --pkg-only [--project <path>] [--unity <Unity.exe>] [--wait]",
      "init --skill-only --agent <pi|claude|.agent,...> [--project <path>]",
      "init --check [--project <path>] [--agent <pi|claude|.agent,...>]",
      "target current",
      "target set <id|alias|projectHash|projectPathPrefix>",
      "target clear",
      "target alias list|set|unset",
      "instances list",
      "instances prune --yes",
      "jobs status|logs|describe|cancel <jobId>",
      "batch schema",
      "batch run <file> [--preflight] [--parallel <n>] [--fail-fast true|false] --yes",
      "doctor [--project <path>] [--bridge-endpoint <url>]"
    ],
    note: "Top-level commands are: tools, context, extensions, init, target, instances, jobs, batch, doctor. Unity capabilities (scene, asset, package, build, test, docs, reflect, editor, physics, profiler, ...) are exposed via `tools run <tool> --input <json>` and `context read <name>`. CLI control-plane state uses `target`, `instances`, `jobs`, `batch`, and `doctor`."
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
