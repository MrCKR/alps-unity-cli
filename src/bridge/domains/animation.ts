import { boolFlag, compactArgs, floatFlag, numberFlag, stringFlag, targetArgs } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const animationBridgeSpecs: BridgeCommandSpec[] = [
  { group: "animation", command: "clip", bridgeCommand: "animation.clip", buildArgs: animationArgs },
  { group: "animation", command: "state", bridgeCommand: "animation.state", buildArgs: animationArgs },
  { group: "animation", command: "play", bridgeCommand: "animation.play", buildArgs: animationArgs },
  { group: "animation", command: "stop", bridgeCommand: "animation.stop", buildArgs: animationArgs }
];

function animationArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  if (command === "animation.clip") {
    return compactArgs({
      action: stringFlag(flags, "action") ?? stringFlag(flags, "op") ?? (flags.create ? "create" : "info"),
      path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "clip"),
      overwrite: boolFlag(flags, "overwrite"),
      frameRate: floatFlag(flags, "frame-rate"),
      legacy: boolFlag(flags, "legacy")
    });
  }
  return compactArgs({
    ...targetArgs(flags, positionalTarget),
    state: stringFlag(flags, "state") ?? stringFlag(flags, "name"),
    layer: numberFlag(flags, "layer"),
    normalizedTime: floatFlag(flags, "normalized-time")
  });
}
