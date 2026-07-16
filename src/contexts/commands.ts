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
  if (!command || command === "list") return dispatchContext("list", undefined, flags, runtime, requestId);
  if (command === "search") return dispatchContext("search", positional[0] ?? getString(flags, "keyword") ?? getString(flags, "query"), flags, runtime, requestId);
  if (command === "describe" || command === "read") return dispatchContext(command, positional[0] ?? getString(flags, "name"), flags, runtime, requestId);
  return failure(requestId, "UNKNOWN_CONTEXT_COMMAND", "Expected context list, context search <keyword>, context describe <name>, or context read <name>.", [
    "Run unity_cli context list to see available contexts.",
    "Run unity_cli context describe <name> for full context metadata."
  ]);
}
