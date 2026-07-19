import { boolFlag, compactArgs, floatFlag, numberFlag, stringFlag, targetArgs } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const cinemachineBridgeSpecs: BridgeCommandSpec[] = markToolsOnly([
  { group: "cinemachine", command: "ping", bridgeCommand: "project-tool.run", toolName: "cinemachine.ping", buildArgs: cinemachinePingArgs },
  { group: "cinemachine", command: "create", bridgeCommand: "project-tool.run", toolName: "cinemachine.create", buildArgs: cinemachineCreateArgs, supportsJob: true },
  { group: "cinemachine", command: "target", bridgeCommand: "project-tool.run", toolName: "cinemachine.target", buildArgs: cinemachineTargetArgs },
  { group: "cinemachine", command: "lens", bridgeCommand: "project-tool.run", toolName: "cinemachine.lens", buildArgs: cinemachineLensArgs },
  { group: "cinemachine", command: "priority", bridgeCommand: "project-tool.run", toolName: "cinemachine.priority", buildArgs: cinemachinePriorityArgs },
  { group: "cinemachine", command: "blend", bridgeCommand: "project-tool.run", toolName: "cinemachine.blend", buildArgs: cinemachineBlendArgs },
  { group: "cinemachine", command: "body", bridgeCommand: "project-tool.run", toolName: "cinemachine.body", buildArgs: cinemachineBodyArgs },
  { group: "cinemachine", command: "aim", bridgeCommand: "project-tool.run", toolName: "cinemachine.aim", buildArgs: cinemachineAimArgs },
  { group: "cinemachine", command: "noise", bridgeCommand: "project-tool.run", toolName: "cinemachine.noise", buildArgs: cinemachineNoiseArgs },
  { group: "cinemachine", command: "brain.ensure", bridgeCommand: "project-tool.run", toolName: "cinemachine.brain.ensure", buildArgs: cinemachineTargetOnlyArgs },
  { group: "cinemachine", command: "brain.status", bridgeCommand: "project-tool.run", toolName: "cinemachine.brain.status", buildArgs: cinemachineTargetOnlyArgs },
  { group: "cinemachine", command: "extension.list", bridgeCommand: "project-tool.run", toolName: "cinemachine.extension.list", buildArgs: cinemachineTargetOnlyArgs },
  { group: "cinemachine", command: "extension.add", bridgeCommand: "project-tool.run", toolName: "cinemachine.extension.add", buildArgs: cinemachineExtensionTypeArgs },
  { group: "cinemachine", command: "extension.remove", bridgeCommand: "project-tool.run", toolName: "cinemachine.extension.remove", buildArgs: cinemachineExtensionTypeArgs }
]);

function markToolsOnly(specs: BridgeCommandSpec[]): BridgeCommandSpec[] {
  return specs.map((spec) => ({ ...spec, toolsOnly: true }));
}

function cinemachinePingArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({});
}

function cinemachineCreateArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    preset: positionalTarget ?? stringFlag(flags, "preset") ?? stringFlag(flags, "type"),
    name: stringFlag(flags, "name"),
    parent: stringFlag(flags, "parent"),
    follow: stringFlag(flags, "follow"),
    lookAt: stringFlag(flags, "look-at"),
    x: floatFlag(flags, "x"),
    y: floatFlag(flags, "y"),
    z: floatFlag(flags, "z"),
    priority: numberFlag(flags, "priority")
  });
}

function cinemachineTargetArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...targetArgs(flags, positionalTarget),
    follow: stringFlag(flags, "follow"),
    lookAt: stringFlag(flags, "look-at")
  });
}

function cinemachineLensArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...targetArgs(flags, positionalTarget),
    fieldOfView: floatFlag(flags, "field-of-view") ?? floatFlag(flags, "fov"),
    nearClipPlane: floatFlag(flags, "near-clip-plane") ?? floatFlag(flags, "near"),
    farClipPlane: floatFlag(flags, "far-clip-plane") ?? floatFlag(flags, "far"),
    orthographicSize: floatFlag(flags, "orthographic-size"),
    dutch: floatFlag(flags, "dutch")
  });
}

function cinemachinePriorityArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...targetArgs(flags, positionalTarget),
    priority: numberFlag(flags, "priority"),
    enabled: boolFlag(flags, "enabled")
  });
}

function cinemachineBlendArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    style: stringFlag(flags, "style"),
    duration: floatFlag(flags, "duration"),
    target: stringFlag(flags, "target")
  });
}

function cinemachineBodyArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...targetArgs(flags, positionalTarget),
    body: stringFlag(flags, "body") ?? stringFlag(flags, "type"),
    property: stringFlag(flags, "property"),
    value: stringFlag(flags, "value")
  });
}

function cinemachineAimArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...targetArgs(flags, positionalTarget),
    aim: stringFlag(flags, "aim") ?? stringFlag(flags, "type"),
    property: stringFlag(flags, "property"),
    value: stringFlag(flags, "value")
  });
}

function cinemachineNoiseArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...targetArgs(flags, positionalTarget),
    noiseProfile: stringFlag(flags, "noise-profile"),
    amplitude: floatFlag(flags, "amplitude"),
    frequency: floatFlag(flags, "frequency"),
    remove: boolFlag(flags, "remove")
  });
}

function cinemachineTargetOnlyArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs(targetArgs(flags, positionalTarget));
}

function cinemachineExtensionTypeArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...targetArgs(flags, positionalTarget),
    extensionType: stringFlag(flags, "extension-type") ?? stringFlag(flags, "type")
  });
}
