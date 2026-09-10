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
  { group: "animation-clip", command: "read", bridgeCommand: "catalog.editor.run", toolName: "animation-clip.read", buildArgs: animationArgs },
  { group: "animation-clip", command: "create", bridgeCommand: "catalog.editor.run", toolName: "animation-clip.create", buildArgs: animationArgs, destructiveHint: true, supportsDryRun: true },
  { group: "animation-clip", command: "update", bridgeCommand: "catalog.editor.run", toolName: "animation-clip.update", buildArgs: animationArgs, destructiveHint: true, supportsDryRun: true },
  { group: "animator", command: "get-state", bridgeCommand: "catalog.editor.run", toolName: "animator.get-state", buildArgs: animationArgs },
  { group: "animator", command: "play", bridgeCommand: "catalog.editor.run", toolName: "animator.play", buildArgs: animationArgs },
  { group: "animator", command: "stop", bridgeCommand: "catalog.editor.run", toolName: "animator.stop", buildArgs: animationArgs },
  { group: "animator", command: "get-info", bridgeCommand: "catalog.editor.run", toolName: "animator.get-info", buildArgs: animatorArgs },
  { group: "animator", command: "get-parameter", bridgeCommand: "catalog.editor.run", toolName: "animator.get-parameter", buildArgs: animatorArgs },
  { group: "animator", command: "set-parameter", bridgeCommand: "catalog.editor.run", toolName: "animator.set-parameter", buildArgs: animatorArgs, destructiveHint: true },
  { group: "animator", command: "crossfade", bridgeCommand: "catalog.editor.run", toolName: "animator.crossfade", buildArgs: animatorArgs },
  { group: "animator", command: "set-speed", bridgeCommand: "catalog.editor.run", toolName: "animator.set-speed", buildArgs: animatorArgs },
  { group: "animator", command: "set-enabled", bridgeCommand: "catalog.editor.run", toolName: "animator.set-enabled", buildArgs: animatorArgs },
  // ── controller.* AnimatorController editing tools ───
  { group: "animator-controller", command: "create", bridgeCommand: "catalog.editor.run", toolName: "animator-controller.create", buildArgs: controllerArgs, destructiveHint: true },
  { group: "animator-controller", command: "add-state", bridgeCommand: "catalog.editor.run", toolName: "animator-controller.add-state", buildArgs: controllerArgs, destructiveHint: true },
  { group: "animator-controller", command: "add-transition", bridgeCommand: "catalog.editor.run", toolName: "animator-controller.add-transition", buildArgs: controllerArgs, destructiveHint: true },
  { group: "animator-controller", command: "add-parameter", bridgeCommand: "catalog.editor.run", toolName: "animator-controller.add-parameter", buildArgs: controllerArgs, destructiveHint: true },
  { group: "animator-controller", command: "get-info", bridgeCommand: "catalog.editor.run", toolName: "animator-controller.get-info", buildArgs: controllerArgs },
  { group: "animator-controller", command: "assign", bridgeCommand: "catalog.editor.run", toolName: "animator-controller.assign", buildArgs: controllerArgs },
  { group: "animator-controller", command: "add-layer", bridgeCommand: "catalog.editor.run", toolName: "animator-controller.add-layer", buildArgs: controllerArgs, destructiveHint: true },
  { group: "animator-controller", command: "remove-layer", bridgeCommand: "catalog.editor.run", toolName: "animator-controller.remove-layer", buildArgs: controllerArgs, destructiveHint: true },
  { group: "animator-controller", command: "set-layer-weight", bridgeCommand: "catalog.editor.run", toolName: "animator-controller.set-layer-weight", buildArgs: controllerArgs, destructiveHint: true },
  { group: "animator-controller", command: "create-blend-tree-1d", bridgeCommand: "catalog.editor.run", toolName: "animator-controller.create-blend-tree-1d", buildArgs: controllerArgs, destructiveHint: true },
  { group: "animator-controller", command: "create-blend-tree-2d", bridgeCommand: "catalog.editor.run", toolName: "animator-controller.create-blend-tree-2d", buildArgs: controllerArgs, destructiveHint: true },
  { group: "animator-controller", command: "add-blend-tree-child", bridgeCommand: "catalog.editor.run", toolName: "animator-controller.add-blend-tree-child", buildArgs: controllerArgs, destructiveHint: true }
];

function animationArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  if (command === "animation-clip.read" || command === "animation-clip.create" || command === "animation-clip.update") {
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
  if (command === "animator-controller.create") {
    return compactArgs({
      controllerPath: positionalTarget ?? stringFlag(flags, "controller-path") ?? stringFlag(flags, "path")
    });
  }
  if (command === "animator-controller.add-state") {
    return compactArgs({
      controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path"),
      stateName: stringFlag(flags, "state-name") ?? stringFlag(flags, "name"),
      clipPath: stringFlag(flags, "clip-path") ?? stringFlag(flags, "clip"),
      layerIndex: numberFlag(flags, "layer-index") ?? 0,
      speed: floatFlag(flags, "speed"),
      isDefault: boolFlag(flags, "is-default")
    });
  }
  if (command === "animator-controller.add-transition") {
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
  if (command === "animator-controller.add-parameter") {
    return compactArgs({
      controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path"),
      parameterName: stringFlag(flags, "parameter-name") ?? stringFlag(flags, "name"),
      parameterType: stringFlag(flags, "parameter-type") ?? stringFlag(flags, "type") ?? "float",
      defaultValue: stringFlag(flags, "default-value")
    });
  }
  if (command === "animator-controller.get-info") {
    return compactArgs({
      controllerPath: positionalTarget ?? stringFlag(flags, "controller-path") ?? stringFlag(flags, "path")
    });
  }
  if (command === "animator-controller.assign") {
    return compactArgs({
      controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path"),
      target: stringFlag(flags, "target") ?? positionalTarget
    });
  }
  if (command === "animator-controller.add-layer") {
    return compactArgs({
      controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path"),
      layerName: stringFlag(flags, "layer-name") ?? stringFlag(flags, "name"),
      weight: floatFlag(flags, "weight"),
      blendingMode: stringFlag(flags, "blending-mode") ?? "override"
    });
  }
  if (command === "animator-controller.remove-layer") {
    return compactArgs({
      controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path"),
      layerIndex: numberFlag(flags, "layer-index"),
      layerName: stringFlag(flags, "layer-name")
    });
  }
  if (command === "animator-controller.set-layer-weight") {
    return compactArgs({
      controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path"),
      layerIndex: numberFlag(flags, "layer-index"),
      layerName: stringFlag(flags, "layer-name"),
      weight: floatFlag(flags, "weight")
    });
  }
  if (command === "animator-controller.create-blend-tree-1d") {
    return compactArgs({
      controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path"),
      stateName: stringFlag(flags, "state-name") ?? stringFlag(flags, "name"),
      blendParameter: stringFlag(flags, "blend-parameter") ?? stringFlag(flags, "parameter"),
      layerIndex: numberFlag(flags, "layer-index") ?? 0
    });
  }
  if (command === "animator-controller.create-blend-tree-2d") {
    return compactArgs({
      controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path"),
      stateName: stringFlag(flags, "state-name") ?? stringFlag(flags, "name"),
      blendParameterX: stringFlag(flags, "blend-parameter-x") ?? stringFlag(flags, "parameter-x"),
      blendParameterY: stringFlag(flags, "blend-parameter-y") ?? stringFlag(flags, "parameter-y"),
      layerIndex: numberFlag(flags, "layer-index") ?? 0,
      blendType: stringFlag(flags, "blend-type") ?? "simpledirectional2d"
    });
  }
  if (command === "animator-controller.add-blend-tree-child") {
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
  // 其余 controller 命令只需要 controllerPath。
  return compactArgs({
    controllerPath: stringFlag(flags, "controller-path") ?? stringFlag(flags, "path")
  });
}
