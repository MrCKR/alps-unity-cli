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
import type { CliCommandModule, Runtime } from "./cli-commands/types.js";
import { summarizeJob } from "./cli-commands/unity-job.js";
import { JobRunner } from "./job-runner.js";
import { JobStore } from "./job-store.js";
import { createRequestId, failure, printJson, success } from "./output.js";
import { defaultDataDir, ensureWindows } from "./platform.js";
import { handleToolsCommand } from "./tools.js";
import type { CliResult } from "./types.js";

/**
 * 用户可见顶层命令只允许 tools/context/extensions/init(README Project Baseline)。
 * 其余能力迁移为 Tool(tools run)或 Context(context read);job worker 为内部路由。
 */
const CLI_COMMANDS = new Map<string, CliCommandModule>([
  ["tools", { run: ({ command, args, flags, runtime, requestId }) => handleToolsCommand(command, args, flags, runtime, requestId) }],
  ["context", { run: ({ command, args, flags, runtime, requestId }) => handleContextCommand(command, args, flags, runtime, requestId) }],
  ["extensions", { run: ({ command, args, flags, runtime, requestId }) => handleExtensionsCommand(command, args, flags, runtime, requestId) }],
  ["init", { run: ({ flags, runtime, requestId }) => handleInit(flags, runtime, requestId) }]
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
      "Top-level commands are: tools, context, extensions, init.",
      "Run unity_cli tools group to discover tools, then unity_cli tools run <tool> --input <json>.",
      "Run unity_cli --help."
    ]);
  } catch (error) {
    return failure(requestId, "COMMAND_FAILED", error instanceof Error ? error.message : String(error), [
      "Run unity_cli --help for available commands.",
      "Run unity_cli tools run diagnose.doctor to diagnose the environment."
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
      "init [--project <path>] [--agent <pi|claude|.agent>] [--unity <Unity.exe>] [--wait]",
      "init --pkg-only [--project <path>] [--unity <Unity.exe>] [--wait]",
      "init --skill-only --agent <pi|claude|.agent,...> [--project <path>]",
      "init --check [--project <path>] [--agent <pi|claude|.agent,...>]"
    ],
    note: "Top-level commands are: tools, context, extensions, init. All Unity capabilities (scene, asset, package, build, test, docs, reflect, editor, physics, profiler, ...) are exposed via `tools run <tool> --input <json>` and `context read <name>`. Run `unity_cli tools group` to discover tools."
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
