import { boolFlag, compactArgs, floatFlag, numberFlag, stringFlag, targetArgs } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

/** 从 --input 保留下来的结构化 JSON 中读取数组/对象参数。 */
function structuredInput(flags: BridgeFlags): Record<string, unknown> {
  const raw = typeof flags.__structuredInput === "string" ? flags.__structuredInput : undefined;
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw);
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed : {};
  } catch {
    return {};
  }
}

export const animationBridgeSpecs: BridgeCommandSpec[] = [
  { group: "animation", command: "clip", bridgeCommand: "animation.clip", buildArgs: animationArgs },
  { group: "animation", command: "clip.create", bridgeCommand: "animation.clip.create", buildArgs: animationArgs },
  { group: "animation", command: "clip.modify", bridgeCommand: "animation.clip.modify", buildArgs: animationArgs },
  { group: "animation", command: "state", bridgeCommand: "animation.state", buildArgs: animationArgs },
  { group: "animation", command: "play", bridgeCommand: "animation.play", buildArgs: animationArgs },
  { group: "animation", command: "stop", bridgeCommand: "animation.stop", buildArgs: animationArgs },
  { group: "animator", command: "get-info", bridgeCommand: "animator.get-info", buildArgs: animatorArgs },
  { group: "animator", command: "get-parameter", bridgeCommand: "animator.get-parameter", buildArgs: animatorArgs },
  { group: "animator", command: "set-parameter", bridgeCommand: "animator.set-parameter", buildArgs: animatorArgs },
  { group: "animator", command: "crossfade", bridgeCommand: "animator.crossfade", buildArgs: animatorArgs },
  { group: "animator", command: "set-speed", bridgeCommand: "animator.set-speed", buildArgs: animatorArgs },
  { group: "animator", command: "set-enabled", bridgeCommand: "animator.set-enabled", buildArgs: animatorArgs },
  // ── controller.* AnimatorController editing tools ───
  { group: "controller", command: "create", bridgeCommand: "controller.create", buildArgs: controllerArgs },
  { group: "controller", command: "add-state", bridgeCommand: "controller.add-state", buildArgs: controllerArgs },
  { group: "controller", command: "add-transition", bridgeCommand: "controller.add-transition", buildArgs: controllerArgs },
  { group: "controller", command: "add-parameter", bridgeCommand: "controller.add-parameter", buildArgs: controllerArgs },
  { group: "controller", command: "get-info", bridgeCommand: "controller.get-info", buildArgs: controllerArgs },
  { group: "controller", command: "assign", bridgeCommand: "controller.assign", buildArgs: controllerArgs },
  { group: "controller", command: "add-layer", bridgeCommand: "controller.add-layer", buildArgs: controllerArgs },
  { group: "controller", command: "remove-layer", bridgeCommand: "controller.remove-layer", buildArgs: controllerArgs },
  { group: "controller", command: "set-layer-weight", bridgeCommand: "controller.set-layer-weight", buildArgs: controllerArgs },
  { group: "controller", command: "create-blend-tree-1d", bridgeCommand: "controller.create-blend-tree-1d", buildArgs: controllerArgs },
  { group: "controller", command: "create-blend-tree-2d", bridgeCommand: "controller.create-blend-tree-2d", buildArgs: controllerArgs },
  { group: "controller", command: "add-blend-tree-child", bridgeCommand: "controller.add-blend-tree-child", buildArgs: controllerArgs }
];

function animationArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  if (command === "animation.clip" || command === "animation.clip.create" || command === "animation.clip.modify") {
    return compactArgs({
      path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "clip"),
      overwrite: boolFlag(flags, "overwrite"),
      frameRate: floatFlag(flags, "frame-rate"),
      legacy: boolFlag(flags, "legacy"),
      wrapMode: stringFlag(flags, "wrap-mode"),
      loopTime: boolFlag(flags, "loop-time"),
      startTime: floatFlag(flags, "start-time"),
      stopTime: floatFlag(flags, "stop-time"),
      length: floatFlag(flags, "length"),
      maxCurveKeys: numberFlag(flags, "max-curve-keys")
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

function controllerArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  if (command === "controller.create") {
    return compactArgs({
      controllerPath: positionalTarget ?? stringFlag(flags, "controller-path") ?? stringFlag(flags, "path")
    });
  }
  if (command === "controller.add-state") {
    return compactArgs({
      controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path"),
      stateName: stringFlag(flags, "state-name") ?? stringFlag(flags, "name"),
      clipPath: stringFlag(flags, "clip-path") ?? stringFlag(flags, "clip"),
      layerIndex: numberFlag(flags, "layer-index") ?? 0,
      speed: floatFlag(flags, "speed"),
      isDefault: boolFlag(flags, "is-default")
    });
  }
  if (command === "controller.add-transition") {
    const structured = structuredInput(flags);
    return compactArgs({
      controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path"),
      fromState: stringFlag(flags, "from-state") ?? stringFlag(flags, "from"),
      toState: stringFlag(flags, "to-state") ?? stringFlag(flags, "to"),
      layerIndex: numberFlag(flags, "layer-index") ?? 0,
      hasExitTime: boolFlag(flags, "has-exit-time"),
      duration: floatFlag(flags, "duration"),
      exitTime: floatFlag(flags, "exit-time"),
      conditions: stringFlag(flags, "conditions") ?? structured.conditions
    });
  }
  if (command === "controller.add-parameter") {
    return compactArgs({
      controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path"),
      parameterName: stringFlag(flags, "parameter-name") ?? stringFlag(flags, "name"),
      parameterType: stringFlag(flags, "parameter-type") ?? stringFlag(flags, "type") ?? "float",
      defaultValue: stringFlag(flags, "default-value")
    });
  }
  if (command === "controller.get-info") {
    return compactArgs({
      controllerPath: positionalTarget ?? stringFlag(flags, "controller-path") ?? stringFlag(flags, "path")
    });
  }
  if (command === "controller.assign") {
    return compactArgs({
      controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path"),
      target: stringFlag(flags, "target") ?? positionalTarget
    });
  }
  if (command === "controller.add-layer") {
    return compactArgs({
      controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path"),
      layerName: stringFlag(flags, "layer-name") ?? stringFlag(flags, "name"),
      weight: floatFlag(flags, "weight"),
      blendingMode: stringFlag(flags, "blending-mode") ?? "override"
    });
  }
  if (command === "controller.remove-layer") {
    return compactArgs({
      controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path"),
      layerIndex: numberFlag(flags, "layer-index"),
      layerName: stringFlag(flags, "layer-name")
    });
  }
  if (command === "controller.set-layer-weight") {
    return compactArgs({
      controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path"),
      layerIndex: numberFlag(flags, "layer-index"),
      layerName: stringFlag(flags, "layer-name"),
      weight: floatFlag(flags, "weight")
    });
  }
  if (command === "controller.create-blend-tree-1d") {
    return compactArgs({
      controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path"),
      stateName: stringFlag(flags, "state-name") ?? stringFlag(flags, "name"),
      blendParameter: stringFlag(flags, "blend-parameter") ?? stringFlag(flags, "parameter"),
      layerIndex: numberFlag(flags, "layer-index") ?? 0
    });
  }
  if (command === "controller.create-blend-tree-2d") {
    return compactArgs({
      controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path"),
      stateName: stringFlag(flags, "state-name") ?? stringFlag(flags, "name"),
      blendParameterX: stringFlag(flags, "blend-parameter-x") ?? stringFlag(flags, "parameter-x"),
      blendParameterY: stringFlag(flags, "blend-parameter-y") ?? stringFlag(flags, "parameter-y"),
      layerIndex: numberFlag(flags, "layer-index") ?? 0,
      blendType: stringFlag(flags, "blend-type") ?? "simpledirectional2d"
    });
  }
  if (command === "controller.add-blend-tree-child") {
    return compactArgs({
      controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path"),
      stateName: stringFlag(flags, "state-name") ?? stringFlag(flags, "name"),
      clipPath: stringFlag(flags, "clip-path") ?? stringFlag(flags, "clip"),
      layerIndex: numberFlag(flags, "layer-index") ?? 0,
      threshold: floatFlag(flags, "threshold"),
      positionX: floatFlag(flags, "position-x"),
      positionY: floatFlag(flags, "position-y")
    });
  }
  // fallback
  return compactArgs({
    controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path")
  });
}
