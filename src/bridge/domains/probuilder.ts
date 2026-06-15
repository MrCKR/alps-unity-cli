import { compactArgs, floatFlag, numberFlag, queryArgs, stringFlag, targetArgs } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const probuilderBridgeSpecs: BridgeCommandSpec[] = [
  { group: "probuilder", command: "shape", bridgeCommand: "probuilder.shape", buildArgs: probuilderArgs, supportsJob: true },
  { group: "probuilder", command: "create", bridgeCommand: "probuilder.create", buildArgs: probuilderArgs, supportsJob: true },
  { group: "probuilder", command: "csg", bridgeCommand: "probuilder.csg", buildArgs: probuilderArgs, supportsJob: true },
  { group: "probuilder", command: "edit", bridgeCommand: "probuilder.edit", buildArgs: probuilderArgs, supportsJob: true }
];

function probuilderArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...(command === "probuilder.create" ? queryArgs(flags) : targetArgs(flags, positionalTarget)),
    shape: stringFlag(flags, "shape") ?? stringFlag(flags, "type") ?? (command === "probuilder.shape" ? positionalTarget : undefined),
    operation: stringFlag(flags, "operation") ?? stringFlag(flags, "op"),
    name: stringFlag(flags, "name"),
    parent: stringFlag(flags, "parent"),
    source: stringFlag(flags, "source"),
    x: floatFlag(flags, "x"),
    y: floatFlag(flags, "y"),
    z: floatFlag(flags, "z"),
    width: floatFlag(flags, "width"),
    height: floatFlag(flags, "height"),
    depth: floatFlag(flags, "depth"),
    radius: floatFlag(flags, "radius"),
    segments: numberFlag(flags, "segments"),
    sides: numberFlag(flags, "sides"),
    material: stringFlag(flags, "material"),
    value: stringFlag(flags, "value")
  });
}
