import { boolFlag, compactArgs, contentFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const scriptBridgeSpecs: BridgeCommandSpec[] = [
  { group: "script", command: "create", bridgeCommand: "script.create", buildArgs: scriptArgs },
  { group: "script", command: "read", bridgeCommand: "script.read", buildArgs: scriptArgs },
  { group: "script", command: "hash", bridgeCommand: "script.hash", buildArgs: scriptArgs },
  { group: "script", command: "edit", bridgeCommand: "script.edit", buildArgs: scriptArgs },
  { group: "script", command: "apply-edits", bridgeCommand: "script.apply-edits", buildArgs: scriptArgs },
  { group: "script", command: "validate", bridgeCommand: "script.validate", buildArgs: scriptArgs },
  { group: "script", command: "update-or-create", bridgeCommand: "script.update-or-create", buildArgs: scriptArgs, supportsDryRun: true },
  { group: "script", command: "find-in-file", bridgeCommand: "script.find-in-file", buildArgs: scriptFindArgs },
  { group: "script", command: "capabilities", bridgeCommand: "script.capabilities", buildArgs: ({ flags }) => compactArgs({}) }
];

async function scriptArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Promise<Record<string, unknown>> {
  const structured = structuredInput(flags);
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path"),
    name: stringFlag(flags, "name"),
    content: await contentFlag(flags),
    patch: stringFlag(flags, "patch"),
    find: stringFlag(flags, "find"),
    replace: stringFlag(flags, "replace"),
    all: boolFlag(flags, "all"),
    preconditionSha256: stringFlag(flags, "precondition-sha256") ?? stringFlag(flags, "sha256"),
    edits: structured.edits ?? parseJsonFlag(stringFlag(flags, "edits"))
  });
}

/** 读取 tools run --input/--input-file 注入的结构化对象，保留数组而不是字符串化。 */
function structuredInput(flags: Record<string, string | boolean>): Record<string, unknown> {
  const raw = stringFlag(flags, "__structuredInput");
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw);
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed as Record<string, unknown> : {};
  } catch {
    return {};
  }
}

function parseJsonFlag(raw: string | undefined): unknown {
  if (raw === undefined) return undefined;
  try {
    return JSON.parse(raw);
  } catch {
    return raw;
  }
}

function scriptFindArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "target"),
    query: stringFlag(flags, "query") ?? stringFlag(flags, "search") ?? stringFlag(flags, "pattern"),
    regex: boolFlag(flags, "regex"),
    ignoreCase: boolFlag(flags, "ignore-case")
  });
}
