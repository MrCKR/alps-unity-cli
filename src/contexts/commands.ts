import { getString } from "../args.js";
import { failure } from "../output.js";
import type { CliResult } from "../types.js";
import { dispatchContext, type ContextRuntime } from "./dispatcher.js";

export type { ContextRuntime } from "./dispatcher.js";

/** 仅解析 context CLI 形态，发现与读取均交给统一 dispatcher。 */
export async function handleContextCommand(
  command: string | undefined,
  positional: string[],
  flags: Record<string, string | boolean>,
  runtime: ContextRuntime,
  requestId: string
): Promise<CliResult> {
  const flagError = rejectUnknownContextFlags(command, flags, requestId);
  if (flagError) return flagError;
  if (!command || command === "list") return dispatchContext("list", undefined, flags, runtime, requestId);
  if (command === "search") return dispatchContext("search", positional[0] ?? getString(flags, "keyword") ?? getString(flags, "query"), flags, runtime, requestId);
  if (command === "describe" || command === "read") return dispatchContext(command, positional[0] ?? getString(flags, "name"), flags, runtime, requestId);
  return failure(requestId, "UNKNOWN_CONTEXT_COMMAND", "Expected context list, context search <keyword>, context describe <name>, or context read <name>.", [
    "Run unity_cli context list to see available contexts.",
    "Run unity_cli context describe <name> for full context metadata."
  ]);
}

/** Context 只接受结构化 input、selector 与 discovery 控制项。 */
function rejectUnknownContextFlags(
  command: string | undefined,
  flags: Record<string, string | boolean>,
  requestId: string
): CliResult | undefined {
  const common = ["instance", "raw"];
  const allowed = !command || command === "list"
    ? [...common, "category", "format"]
    : command === "search"
      ? [...common, "category", "format"]
      : command === "describe"
        ? common
        : command === "read"
          ? [...common, "input", "input-file", "stdin", "timeout-ms"]
          : common;
  const unknown = Object.keys(flags).find((flag) => !allowed.includes(flag));
  if (!unknown) return undefined;
  return failure(requestId, "E_UNKNOWN_OPTION", `Unknown option for context ${command ?? "list"}: --${unknown}.`, [
    command === "read" ? "Put Context fields inside --input <json>." : "Run unity_cli --help for the supported options."
  ], { option: `--${unknown}`, command: `context ${command ?? "list"}` });
}
