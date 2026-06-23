import { boolFlag, compactArgs, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const consoleBridgeSpecs: BridgeCommandSpec[] = [
  { group: "console", command: "get", bridgeCommand: "console.get", buildArgs: consoleGetArgs },
  { group: "console", command: "clear", bridgeCommand: "console.clear", buildArgs: ({ flags }) => compactArgs({ dryRun: boolFlag(flags, "dry-run") }), supportsDryRun: true }
];

function consoleGetArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    type: stringFlag(flags, "type") ?? stringFlag(flags, "level"),
    filter: stringFlag(flags, "filter") ?? stringFlag(flags, "search"),
    count: numberFlag(flags, "count")
  });
}
