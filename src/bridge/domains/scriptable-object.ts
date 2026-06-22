import { boolFlag, compactArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const scriptableObjectBridgeSpecs: BridgeCommandSpec[] = [
  { group: "scriptable-object", command: "create", bridgeCommand: "scriptable-object.create", buildArgs: scriptableObjectArgs },
  { group: "scriptable-object", command: "info", bridgeCommand: "scriptable-object.info", buildArgs: scriptableObjectArgs },
  { group: "scriptable-object", command: "modify", bridgeCommand: "scriptable-object.modify", buildArgs: scriptableObjectArgs }
];

function scriptableObjectArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path"),
    type: stringFlag(flags, "type"),
    property: stringFlag(flags, "property"),
    value: stringFlag(flags, "value"),
    overwrite: boolFlag(flags, "overwrite")
  });
}
