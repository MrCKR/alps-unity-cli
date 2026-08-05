#!/usr/bin/env node
import { realpathSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs, getBoolean, getString } from "./args.js";
import { registerBuiltinExtensionContributions } from "./extensions/index.js";
import { handleExtensionsCommand } from "./cli-commands/extensions.js";
import { handleContextCommand } from "./contexts/commands.js";
import { handleInit } from "./cli-commands/package.js";
import type { CliCommandModule, Runtime } from "./cli-commands/types.js";
import { createRequestId, failure, printResult, success } from "./output.js";
import { defaultDataDir, ensureWindows } from "./platform.js";
import { handleToolsCommand } from "./tools/commands.js";
import { handleDoctorCommand } from "./doctor.js";
import { handleInstancesCommand, handleTargetCommand } from "./instances.js";
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
  ["doctor", { run: ({ flags, runtime, requestId }) => handleDoctorCommand(flags, runtime, requestId) }]
]);

export async function execute(argv: string[], runtime = createRuntime()): Promise<CliResult> {
  // Register built-in extension contributions before any tool discovery or bridge command routing.
  registerBuiltinExtensionContributions();

  const requestId = createRequestId();
  const parsed = parseArgs(argv);
  const [group, command] = parsed.positional;

  try {
    if (getBoolean(parsed.flags, "version")) {
      return success(requestId, { version: await packageVersion() });
    }

    if (!group || getBoolean(parsed.flags, "help")) {
      return success(requestId, help());
    }

    const removedExecutionOption = ["job", "wait", "wait-timeout-ms"].find((option) => parsed.flags[option] !== undefined);
    if (removedExecutionOption) {
      return failure(requestId, "E_OBSOLETE_OPTION", `--${removedExecutionOption} has been removed; every Tool call is direct and terminal.`, [
        `Remove --${removedExecutionOption} from the command.`,
        "Use the same tools run invocation and inspect its final success or error envelope."
      ]);
    }

    if (parsed.flags["bridge-endpoint"] !== undefined || process.env.UNITY_CLI_BRIDGE_ENDPOINT !== undefined) {
      return failure(requestId, "E_OBSOLETE_OPTION", "Public Bridge endpoint selection has been removed.", [
        "Pass --instance <instanceId|canonicalProjectPath|alias> or run from the target Unity project directory.",
        "Remove UNITY_CLI_BRIDGE_ENDPOINT from the process environment.",
      ]);
    }
    if (parsed.flags.retry !== undefined) {
      return failure(requestId, "E_OBSOLETE_OPTION", "Public transport retry counts have been removed; the session supervisor owns recoverable Unity lifecycle transitions.", [
        "Remove --retry and use one absolute --timeout-ms budget for the logical request.",
      ]);
    }
    if (parsed.flags.target !== undefined && usesLegacyTargetSelector(group, command)) {
      return failure(requestId, "E_OBSOLETE_OPTION", "--target is not an instance selector. Use --instance or Unity project CWD.", [
        "Pass --instance <instanceId|canonicalProjectPath|alias>.",
      ]);
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

    // Bridge registry 是内部能力路由，公开 Unity 能力统一从 tools run 进入。
    return failure(requestId, "UNKNOWN_COMMAND", `Unknown command: ${argv.join(" ")}`, [
      "Top-level commands are: tools, context, extensions, init, target, instances, doctor.",
      "Run unity_cli tools group to discover Unity tools, then unity_cli tools run <tool> --input <json>.",
      "Run unity_cli instances list, then pass --instance <id|projectPath|alias> or use a Unity project CWD.",
      "Run unity_cli --help."
    ]);
  } catch (error) {
    return failure(requestId, "COMMAND_FAILED", error instanceof Error ? error.message : String(error), [
      "Run unity_cli --help for available commands.",
      "Run unity_cli doctor to diagnose the environment."
    ]);
  }
}

function createRuntime(dataDir = defaultDataDir()): Runtime { return { dataDir }; }

/** 顶层 target 控制面只管理 canonical project identity alias。 */
async function handleTopLevelTarget(
  command: string | undefined,
  args: string[],
  flags: Record<string, string | boolean>,
  runtime: Runtime,
  requestId: string
): Promise<CliResult> {
  if (command === "alias") {
    return handleTargetCommand("alias", args, flags, runtime.dataDir, requestId);
  }
  return failure(requestId, "UNKNOWN_TARGET_COMMAND", "Expected target alias list|set|unset.", [
    "Run unity_cli target alias list.",
    "Run unity_cli target alias set <projectPath|instanceId> <alias>."
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
  return handleInstancesCommand(subCommand, flags, runtime.dataDir, requestId);
}

/** 业务 Tool input 可拥有 target 字段，但控制面和 discovery 不得把 --target 当 instance selector。 */
function usesLegacyTargetSelector(group: string, command: string | undefined): boolean {
  return group === "context"
    || group === "doctor"
    || group === "instances"
    || group === "target"
    || (group === "tools" && command !== "run");
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
      "tools group [--include-custom] [--instance <id|projectPath|alias>] [--format json|text]",
      "tools group list <group> [--include-custom] [--instance <id|projectPath|alias>] [--format json|text]",
      "tools describe <tool> [--instance <id|projectPath|alias>]",
      "tools run <name> [--input <json>] [--input-file <path>] [--timeout-ms <n>] [--artifact-dir <path>] [--runtime] [--instance <id|projectPath|alias>]",
      "tools search <keyword> [--instance <id|projectPath|alias>] [--format json|text]",
      "context list [--category <cat>] [--include-custom] [--instance <id|projectPath|alias>] [--format json|text]",
      "context search <keyword> [--category <cat>] [--include-custom] [--instance <id|projectPath|alias>] [--format json|text]",
      "context describe <name> [--instance <id|projectPath|alias>]",
      "context read <name> [--input <json>] [--input-file <path>] [--instance <id|projectPath|alias>]",
      "extensions list [--project <path>] [--format json|text]",
      "extensions install <id|name> [--project <path>]",
      "extensions remove <id|name> [--project <path>]",
      "extensions doctor [--project <path>] [--format json|text]",
      "init [--project <path>] [--agent <pi|claude|.agents>]  # auto-installs Skill when .pi/.claude/.agents exists",
      "init --pkg-only [--project <path>]",
      "init --skill-only --agent <pi|claude|.agents,...> [--project <path>]",
      "init --check [--project <path>] [--agent <pi|claude|.agents,...>]",
      "target alias list",
      "target alias set <projectPath|instanceId> <alias> [--force]",
      "target alias unset <alias>",
      "instances list",
      "instances prune",
      "doctor [--instance <id|projectPath|alias>] [--project <path>]"
    ],
    options: [
      "--raw  Print the same JSON envelope as compact single-line JSON for pipes and scripts."
    ],
    note: "Top-level commands are: tools, context, extensions, init, target, instances, doctor. Unity capabilities are exposed through `tools run <tool> --input <json>` and `context read <name>`. Live target resolution accepts only `--instance <full-id|canonical-project-path|alias>` or the nearest Unity project root from CWD. `target` manages aliases only; endpoint, PID, active-target and single-instance fallback selection do not exist. Descriptor `examples[].argv` is a JSON token array. In PowerShell, wrap inline `--input` JSON in single quotes, or use `--input-file` for complex input."
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
  const argv = process.argv.slice(2);
  const rawOutput = readRawOutputFlag(argv);
  execute(stripOutputOnlyFlags(argv)).then((result) => {
    printResult(result, { raw: rawOutput });
    process.exitCode = result.ok ? 0 : 1;
  });
}

/** 读取只影响 stdout 序列化的全局 raw 开关，支持放在命令前后。 */
function readRawOutputFlag(argv: string[]): boolean {
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (token === "--raw") {
      const next = argv[index + 1];
      return next === "false" ? false : true;
    }
    if (token === "--raw=false") return false;
    if (token === "--raw=true") return true;
  }
  return false;
}

/** 移除只影响 stdout 序列化的全局开关，避免泄漏到业务 handler。 */
function stripOutputOnlyFlags(argv: string[]): string[] {
  const stripped: string[] = [];
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (token === "--raw") {
      const next = argv[index + 1];
      if (next === "true" || next === "false") index += 1;
      continue;
    }
    if (token.startsWith("--raw=")) {
      continue;
    }
    stripped.push(token);
  }
  return stripped;
}
