import { boolFlag, compactArgs, contentFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const scriptBridgeSpecs: BridgeCommandSpec[] = [
  { group: "script", command: "create", bridgeCommand: "script.create", buildArgs: scriptArgs },
  { group: "script", command: "read", bridgeCommand: "script.read", buildArgs: scriptArgs },
  { group: "script", command: "edit", bridgeCommand: "script.edit", buildArgs: scriptArgs },
  { group: "script", command: "validate", bridgeCommand: "script.validate", buildArgs: scriptArgs }
];

async function scriptArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Promise<Record<string, unknown>> {
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path"),
    name: stringFlag(flags, "name"),
    content: await contentFlag(flags),
    patch: stringFlag(flags, "patch"),
    find: stringFlag(flags, "find"),
    replace: stringFlag(flags, "replace"),
    all: boolFlag(flags, "all")
  });
}
