import { boolFlag, compactArgs, numberFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const materialBridgeSpecs: BridgeCommandSpec[] = [
  { group: "material", command: "info", bridgeCommand: "material.info", buildArgs: materialArgs },
  { group: "material", command: "create", bridgeCommand: "material.create", buildArgs: materialArgs },
  { group: "material", command: "assign", bridgeCommand: "material.assign", buildArgs: materialArgs },
  { group: "material", command: "set", bridgeCommand: "material.set", buildArgs: materialArgs }
];

function materialArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  if (command === "material.create") {
    return compactArgs({
      path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "material"),
      shader: stringFlag(flags, "shader"),
      overwrite: boolFlag(flags, "overwrite")
    });
  }
  if (command === "material.assign") {
    return compactArgs({
      target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name"),
      material: stringFlag(flags, "material") ?? stringFlag(flags, "path"),
      slot: numberFlag(flags, "slot") ?? numberFlag(flags, "material-index")
    });
  }
  if (command === "material.set") {
    return compactArgs({
      path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "material"),
      property: stringFlag(flags, "property"),
      value: stringFlag(flags, "value"),
      type: stringFlag(flags, "type")
    });
  }
  return compactArgs({
    ...queryArgs(flags),
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "material")
  });
}
