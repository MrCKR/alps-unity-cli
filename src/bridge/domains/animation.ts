import { boolFlag, compactArgs, floatFlag, numberFlag, stringFlag, targetArgs } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const animationBridgeSpecs: BridgeCommandSpec[] = [
  { group: "animation", command: "clip", bridgeCommand: "animation.clip", buildArgs: animationArgs },
  { group: "animation", command: "state", bridgeCommand: "animation.state", buildArgs: animationArgs },
  { group: "animation", command: "play", bridgeCommand: "animation.play", buildArgs: animationArgs },
  { group: "animation", command: "stop", bridgeCommand: "animation.stop", buildArgs: animationArgs },
  { group: "animator", command: "get-info", bridgeCommand: "animator.get-info", buildArgs: animatorArgs },
  { group: "animator", command: "get-parameter", bridgeCommand: "animator.get-parameter", buildArgs: animatorArgs },
  { group: "animator", command: "set-parameter", bridgeCommand: "animator.set-parameter", buildArgs: animatorArgs },
  { group: "animator", command: "crossfade", bridgeCommand: "animator.crossfade", buildArgs: animatorArgs },
  { group: "animator", command: "set-speed", bridgeCommand: "animator.set-speed", buildArgs: animatorArgs },
  { group: "animator", command: "set-enabled", bridgeCommand: "animator.set-enabled", buildArgs: animatorArgs }
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

function animatorArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const base = compactArgs({
    ...targetArgs(flags, positionalTarget),
    parameterName: stringFlag(flags, "parameter-name") ?? stringFlag(flags, "name"),
    parameterType: stringFlag(flags, "parameter-type") ?? stringFlag(flags, "type"),
    value: stringFlag(flags, "value") ?? stringFlag(flags, "val")
  });
  if (command === "animator.crossfade") {
    return compactArgs({
      ...base,
      state: stringFlag(flags, "state") ?? stringFlag(flags, "name"),
      duration: floatFlag(flags, "duration"),
      layer: numberFlag(flags, "layer")
    });
  }
  if (command === "animator.set-speed") {
    return compactArgs({
      ...targetArgs(flags, positionalTarget),
      speed: floatFlag(flags, "speed")
    });
  }
  if (command === "animator.set-enabled") {
    return compactArgs({
      ...targetArgs(flags, positionalTarget),
      value: boolFlag(flags, "value") ?? boolFlag(flags, "enabled") ?? true
    });
  }
  // get-info, get-parameter, set-parameter
  return base;
}
