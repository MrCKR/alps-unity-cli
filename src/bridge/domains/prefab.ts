import { boolFlag, compactArgs, floatFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const prefabBridgeSpecs: BridgeCommandSpec[] = [
  { group: "prefab", command: "info", bridgeCommand: "prefab.info", buildArgs: prefabArgs },
  { group: "prefab", command: "hierarchy", bridgeCommand: "prefab.hierarchy", buildArgs: prefabArgs },
  { group: "prefab", command: "create", bridgeCommand: "prefab.create", buildArgs: prefabArgs },
  { group: "prefab", command: "modify", bridgeCommand: "prefab.modify", buildArgs: prefabArgs },
  { group: "prefab", command: "stage-open", bridgeCommand: "prefab.stage-open", buildArgs: prefabArgs },
  { group: "prefab", command: "stage-save", bridgeCommand: "prefab.stage-save", buildArgs: prefabArgs },
  { group: "prefab", command: "stage-close", bridgeCommand: "prefab.stage-close", buildArgs: prefabArgs },
  { group: "prefab", command: "instantiate", bridgeCommand: "prefab.instantiate", buildArgs: prefabInstantiateArgs, supportsDryRun: true },
  { group: "prefab", command: "unpack", bridgeCommand: "prefab.unpack", buildArgs: prefabUnpackArgs, supportsDryRun: true }
];

function prefabArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  if (command === "prefab.create") {
    return compactArgs({
      source: positionalTarget ?? stringFlag(flags, "source") ?? stringFlag(flags, "target"),
      path: stringFlag(flags, "path") ?? stringFlag(flags, "destination") ?? stringFlag(flags, "to"),
      overwrite: boolFlag(flags, "overwrite")
    });
  }
  if (command === "prefab.modify") {
    return compactArgs({
      path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "prefab"),
      name: stringFlag(flags, "name"),
      addChild: stringFlag(flags, "add-child"),
      removeChild: stringFlag(flags, "remove-child"),
      overwrite: boolFlag(flags, "overwrite")
    });
  }
  if (command === "prefab.stage-save" || command === "prefab.stage-close") {
    return compactArgs({ path: positionalTarget ?? stringFlag(flags, "path"), save: boolFlag(flags, "save") });
  }
  return compactArgs({
    ...queryArgs(flags),
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "prefab")
  });
}

function prefabInstantiateArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path"),
    parent: stringFlag(flags, "parent"),
    name: stringFlag(flags, "name"),
    x: floatFlag(flags, "x"),
    y: floatFlag(flags, "y"),
    z: floatFlag(flags, "z"),
    dryRun: boolFlag(flags, "dry-run")
  });
}

function prefabUnpackArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    path: stringFlag(flags, "path"),
    dryRun: boolFlag(flags, "dry-run")
  });
}
