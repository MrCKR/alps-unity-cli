#!/usr/bin/env node
import { realpathSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs, getBoolean, getString, type CliArgsSpec } from "./args.js";
import { registerBuiltinExtensionContributions } from "./extensions/index.js";
import { handleCommand } from "./cli-commands/command.js";
import { handleExtensionsCommand } from "./cli-commands/extensions.js";
import { handleInstances } from "./cli-commands/instances.js";
import { handleInit } from "./cli-commands/package.js";
import type { CliCommandModule, Runtime } from "./cli-commands/types.js";
import { createRequestId, failure, serializeResult, success } from "./output.js";
import { defaultDataDir, ensureWindows } from "./platform.js";
import { handleToolsCommand } from "./tools/commands.js";
import { handleDoctorCommand } from "./doctor.js";
import { handleTargetCommand } from "./instances.js";
import type { CliResult } from "./types.js";

/**
 * 用户可见能力入口只有tools与command，二者复用唯一Tool catalog和dispatcher。
 * Unity能力域不得恢复context/scene/asset/package等顶层入口。
 */
const CLI_COMMANDS = new Map<string, CliCommandModule>([
  ["tools", { run: ({ command, args, flags, runtime, requestId }) => handleToolsCommand(command, args, flags, runtime, requestId) }],
  ["command", { run: ({ args, flags, runtime, requestId }) => handleCommand(args, flags, runtime, requestId) }],
  ["extensions", { run: ({ command, args, flags, runtime, requestId }) => handleExtensionsCommand(command, args, flags, runtime, requestId) }],
  ["init", { run: ({ command, args, flags, runtime, requestId }) => handleInit(command, args, flags, runtime, requestId) }],
  ["target", { run: ({ command, args, flags, runtime, requestId }) => handleTopLevelTarget(command, args, flags, runtime, requestId) }],
  ["instances", { run: ({ command, flags, runtime, requestId }) => handleInstances(command, flags, runtime, requestId) }],
  ["doctor", { run: ({ flags, runtime, requestId }) => handleDoctorCommand(flags, runtime, requestId) }]
]);

/** CLI option 的唯一 arity 表；命令 handler 继续负责校验该 option 是否适用于具体命令。 */
export const CLI_ARGS_SPEC: CliArgsSpec = {
  commandNames: new Set(CLI_COMMANDS.keys()),
  passthroughAfterCommand: "command",
  options: {
    help: "boolean",
    version: "boolean",
    pretty: "boolean",
    instance: "value",
    project: "value",
    input: "value",
    "input-file": "value",
    stdin: "boolean",
    "dry-run": "boolean",
    "timeout-ms": "value",
    "artifact-dir": "value",
    "page-size": "value",
    cursor: "value",
    runtime: "boolean",
    agent: "value",
    scope: "value",
    workspace: "value",
    check: "boolean",
    force: "boolean",
    all: "boolean",
    job: "boolean",
    wait: "boolean",
    "wait-timeout-ms": "value",
    "bridge-endpoint": "value",
    retry: "value",
    target: "value",
  },
};

export async function execute(argv: string[], runtime = createRuntime()): Promise<CliResult> {
  // Register built-in extension contributions before any tool discovery or bridge command routing.
  registerBuiltinExtensionContributions();

  const requestId = createRequestId();
  const parsed = parseArgs(argv, CLI_ARGS_SPEC);
  const [group, command] = parsed.positional;

  try {
    if (parsed.issue) {
      const option = `--${parsed.issue.option}`;
      if (parsed.issue.code === "E_UNKNOWN_OPTION") {
        return failure(requestId, parsed.issue.code, `Unknown option: ${option}.`, ["Run unitycli --help for the supported options."], { option });
      }
      if (parsed.issue.code === "E_OPTION_VALUE_REQUIRED") {
        return failure(requestId, parsed.issue.code, `Option ${option} requires a value.`, [`Pass ${option}=<value> or ${option} <value>.`], { option, expected: "value", ...(parsed.issue.actual ? { actual: parsed.issue.actual } : {}) });
      }
      return failure(requestId, parsed.issue.code, `Boolean option ${option} accepts only true or false when using an attached value.`, [`Use ${option}, ${option}=true, or ${option}=false.`], { option, expected: "boolean", actual: parsed.issue.actual });
    }
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

    const allowedOptions = commandOptionNames(group, command);
    if (allowedOptions) {
      const unknownOption = Object.keys(parsed.flags).find((name) => !allowedOptions.has(name));
      if (unknownOption) {
        return failure(requestId, "E_UNKNOWN_OPTION", `Unknown option for ${[group, command].filter(Boolean).join(" ")}: --${unknownOption}.`, [
          "Run unitycli --help for the supported options.",
        ], { option: `--${unknownOption}`, command: [group, command].filter(Boolean).join(" ") });
      }
    }

    ensureWindows();

    const cliModule = CLI_COMMANDS.get(group);
    const cliResult = await cliModule?.run({
      command,
      args: parsed.passthrough ?? parsed.positional.slice(2),
      flags: parsed.flags,
      runtime,
      requestId
    });
    if (cliResult) {
      return cliResult;
    }

    // Bridge registry 是内部能力路由，公开 Unity 能力统一从 tools run 进入。
    return failure(requestId, "UNKNOWN_COMMAND", `Unknown command: ${argv.join(" ")}`, [
      "Top-level commands are: tools, command, extensions, init, target, instances, doctor.",
      "Run unitycli tools group or unitycli command to discover Unity tools.",
      "Run unitycli instances list, then pass --instance <id|projectPath|alias> or use a Unity project CWD.",
      "Run unitycli --help."
    ]);
  } catch (error) {
    return failure(requestId, "COMMAND_FAILED", error instanceof Error ? error.message : String(error), [
      "Run unitycli --help for available commands.",
      "Run unitycli doctor to diagnose the environment."
    ]);
  }
}

function createRuntime(dataDir = defaultDataDir()): Runtime { return { dataDir }; }

/** 返回当前 route 可用的 option 集；undefined 表示顶层命令本身未知，应由 UNKNOWN_COMMAND 处理。 */
function commandOptionNames(group: string | undefined, command: string | undefined): ReadonlySet<string> | undefined {
  if (!group || !CLI_COMMANDS.has(group)) return undefined;
  const global = ["help", "version", "pretty"];
  if (group === "tools") {
    const common = [...global, "instance"];
    if (command === "run") return new Set([...common, "input", "input-file", "stdin", "dry-run", "timeout-ms", "artifact-dir", "runtime"]);
    if (command === "search" || command === "group") return new Set([...common, "page-size", "cursor"]);
    return new Set(common);
  }
  if (group === "command") return new Set([...global, "instance", "timeout-ms", "artifact-dir", "dry-run", "runtime", "page-size", "cursor"]);
  if (group === "extensions") {
    const common = [...global, "project"];
    return new Set(command === "install" || command === "remove" ? [...common, "timeout-ms"] : common);
  }
  if (group === "init") {
    if (command === "help" || command === "discover") return new Set(global);
    if (command === "package") return new Set([...global, "project", "check", "timeout-ms"]);
    if (command === "skill") return new Set([...global, "agent", "scope", "workspace", "check", "timeout-ms"]);
    return new Set(global);
  }
  if (group === "target") return new Set([...global, "force"]);
  if (group === "instances") return new Set(command === "prune" ? [...global, "instance", "timeout-ms"] : [...global, "all"]);
  return new Set([...global, "instance", "project"]);
}

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
    "Run unitycli target alias list.",
    "Run unitycli target alias set <projectPath|instanceId> <alias>."
  ]);
}

/** 业务 Tool input 可拥有 target 字段，但控制面和 discovery 不得把 --target 当 instance selector。 */
function usesLegacyTargetSelector(group: string, command: string | undefined): boolean {
  return group === "doctor"
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
    usage: "unitycli [options] <command> [options]",
    commands: [
      "tools group [--instance <id|projectPath|alias>]",
      "tools group list <group> [--page-size <n>] [--cursor <n>] [--instance <id|projectPath|alias>]",
      "tools describe <tool> [--instance <id|projectPath|alias>]",
      "tools run <name> [--input <json>] [--input-file <path>] [--timeout-ms <n>] [--artifact-dir <path>] [--runtime] [--instance <id|projectPath|alias>]",
      "tools search <k1|k2|...> [--page-size <n>] [--cursor <n>] [--instance <id|projectPath|alias>]",
      "command [--project-path <path>] [--page-size <n>] [--cursor <n>] [<name> [--field <value>|<positional>...]]",
      "extensions list [--project <path>]",
      "extensions install <id|name> [--project <path>]",
      "extensions remove <id|name> [--project <path>]",
      "extensions doctor [--project <path>]",
      "init help",
      "init discover",
      "init package --project <UnityProject> [--check] [--timeout-ms <n>]",
      "init skill --agent <.agents|claude> --scope workspace --workspace <path> [--check] [--timeout-ms <n>]",
      "init skill --agent <.agents|claude> --scope user [--check] [--timeout-ms <n>]",
      "target alias list",
      "target alias set <projectPath|instanceId> <alias> [--force]",
      "target alias unset <alias>",
      "instances list [--all]",
      "instances prune",
      "doctor [--instance <id|projectPath|alias>] [--project <path>]"
    ],
    options: [
      "--pretty  Indent the JSON envelope; default stdout is compact single-line JSON."
    ],
    note: "Top-level commands are: tools, command, extensions, init, target, instances, doctor. Unity capabilities share one Tool catalog: use `tools run <tool> --input <json>` or `command <name> <business-args>`. Live target resolution accepts only `--instance <full-id|canonical-project-path|alias>`, `command --project-path <path>`, or the nearest Unity project root from CWD. When CWD is outside the selected Unity project, every `tools` discovery call requires `--instance <projectPath>`. `target` manages aliases only; endpoint, PID, active-target and single-instance fallback selection do not exist. Tool names returned by group list/search are exact; groups are filters and are never prepended. Discovery lists default to 20 items, allow at most 100, and continue with `pageInfo.nextCursor`. Descriptor `examples[].input` is business JSON; optional `examples[].execution` carries dryRun and timeoutMs controls. Run `init help` for the self-contained AI installation protocol; Bridge and Skill installation are separate explicit effects. stdout is compact JSON unless `--pretty` is explicit. In PowerShell, wrap inline JSON and pipe-separated tools search keywords in single quotes."
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

export const CLI_OUTPUT_FLUSH_GRACE_MS = 1_000;

export interface CliExitRuntime {
  write(text: string, complete: () => void): void;
  schedule(complete: () => void, delayMs: number): unknown;
  cancel(timer: unknown): void;
  exit(code: number): void;
}

/** 输出完成后结束CLI；残留socket、timer或child handle不得延长公开调用生命周期。 */
export function finishCliProcess(
  result: CliResult,
  pretty: boolean,
  runtime: CliExitRuntime = {
    write: (text, complete) => { process.stdout.write(text, complete); },
    schedule: (complete, delayMs) => setTimeout(complete, delayMs),
    cancel: (timer) => clearTimeout(timer as ReturnType<typeof setTimeout>),
    exit: (code) => process.exit(code),
  },
): void {
  const exitCode = result.ok ? 0 : 1;
  let finished = false;
  let timer: unknown;
  const finish = (): void => {
    if (finished) return;
    finished = true;
    if (timer !== undefined) runtime.cancel(timer);
    runtime.exit(exitCode);
  };
  timer = runtime.schedule(finish, CLI_OUTPUT_FLUSH_GRACE_MS);
  runtime.write(serializeResult(result, { pretty }), finish);
}

if (isCliEntryPoint(import.meta.url, process.argv[1])) {
  const argv = process.argv.slice(2);
  const prettyOutput = readPrettyOutputFlag(argv);
  void execute(stripOutputOnlyFlags(argv)).then(
    (result) => finishCliProcess(result, prettyOutput),
    (error) => finishCliProcess(failure(createRequestId(), "COMMAND_FAILED", error instanceof Error ? error.message : String(error), ["Run unitycli --help for available commands."]), prettyOutput),
  );
}

/** 读取只影响 stdout 空白的 pretty 开关，支持放在命令前后。 */
function readPrettyOutputFlag(argv: string[]): boolean {
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (token === "--pretty") {
      const next = argv[index + 1];
      return next === "false" ? false : true;
    }
    if (token === "--pretty=false") return false;
    if (token === "--pretty=true") return true;
  }
  return false;
}

/** 移除只影响 stdout 序列化的全局开关，避免泄漏到业务 handler。 */
function stripOutputOnlyFlags(argv: string[]): string[] {
  const stripped: string[] = [];
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (token === "--pretty") {
      const next = argv[index + 1];
      if (next === "true" || next === "false") index += 1;
      continue;
    }
    if (token === "--pretty=true" || token === "--pretty=false") continue;
    stripped.push(token);
  }
  return stripped;
}
