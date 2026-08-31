import { getString } from "../args.js";
import { failure } from "../output.js";
import type { CliResult } from "../types.js";
import { dispatchTool, type ToolsRuntime } from "./dispatcher.js";

export type { ToolsRuntime } from "./dispatcher.js";

/** 解析 tools 子命令形态后统一交给 dispatcher，避免发现命令形成第二套路由。 */
export async function handleToolsCommand(
  command: string | undefined,
  positional: string[],
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  const flagError = rejectUnknownToolFlags(command, positional, flags, requestId);
  if (flagError) return flagError;
  if (command === "run") {
    const name = positional[0] ?? getString(flags, "name") ?? getString(flags, "tool");
    return dispatchTool(name, positional[1], flags, runtime, requestId);
  }
  if (command === "group") {
    if (positional[0] === "list") {
      return dispatchTool("tools.group-list", positional[1], flags, runtime, requestId, { group: positional[1], ...pageSeed(flags) });
    }
    return dispatchTool("tools.group", undefined, flags, runtime, requestId);
  }
  if (command === "search") {
    const keyword = positional[0] ?? getString(flags, "keyword") ?? getString(flags, "query");
    return dispatchTool("tools.search", undefined, flags, runtime, requestId, { keyword, ...pageSeed(flags) });
  }
  if (command === "describe") {
    const name = positional[0] ?? getString(flags, "name") ?? getString(flags, "tool");
    return dispatchTool("tools.describe", undefined, flags, runtime, requestId, { name });
  }
  return failure(requestId, "UNKNOWN_TOOLS_COMMAND", "Expected tools group, tools group list <group>, tools describe <tool>, tools run <tool>, or tools search <k1|k2|...>.", [
    "Run unitycli tools group to see tool categories.",
    "Run unitycli tools describe <tool> for full metadata."
  ]);
}

/** 公开 Tool 参数只允许 JSON input 与明确命令控制项，未知 flag 不进入业务 schema。 */
function rejectUnknownToolFlags(
  command: string | undefined,
  positional: string[],
  flags: Record<string, string | boolean>,
  requestId: string
): CliResult | undefined {
  const common = ["instance", "pretty"];
  const pagedDiscovery = command === "search" || command === "group" && positional[0] === "list";
  const allowed = command === "run"
    ? [...common, "input", "input-file", "stdin", "dry-run", "timeout-ms", "artifact-dir", "runtime"]
    : pagedDiscovery ? [...common, "page-size", "cursor"] : common;
  const unknown = Object.keys(flags).find((flag) => !allowed.includes(flag));
  if (!unknown) return undefined;
  return failure(requestId, "E_UNKNOWN_OPTION", `Unknown option for tools ${command ?? ""}: --${unknown}.`, [
    command === "run" ? "Put business fields inside --input <json>." : "Run unitycli --help for the supported options."
  ], { option: `--${unknown}`, command: `tools ${command ?? ""}`.trim(), positional });
}

function pageSeed(flags: Record<string, string | boolean>): Record<string, unknown> {
  return {
    ...(flags["page-size"] !== undefined ? { pageSize: Number(flags["page-size"]) } : {}),
    ...(flags.cursor !== undefined ? { cursor: Number(flags.cursor) } : {}),
  };
}
