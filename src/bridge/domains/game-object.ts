import { boolFlag, compactArgs, stringFlag, targetArgs } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const gameObjectBridgeSpecs: BridgeCommandSpec[] = [
  { group: "go", command: "find", bridgeCommand: "go.find", buildArgs: ({ flags, positionalTarget }) => targetArgs(flags, positionalTarget) },
  { group: "go", command: "inspect", bridgeCommand: "go.inspect", buildArgs: ({ flags, positionalTarget }) => targetArgs(flags, positionalTarget) },
  { group: "go", command: "create", bridgeCommand: "go.create", buildArgs: gameObjectArgs },
  { group: "go", command: "modify", bridgeCommand: "go.modify", buildArgs: gameObjectArgs },
  { group: "go", command: "duplicate", bridgeCommand: "go.duplicate", buildArgs: gameObjectArgs },
  { group: "go", command: "move-relative", bridgeCommand: "go.move-relative", buildArgs: gameObjectArgs },
  { group: "go", command: "look-at", bridgeCommand: "go.look-at", buildArgs: gameObjectArgs },
  { group: "go", command: "delete", bridgeCommand: "go.delete", buildArgs: gameObjectArgs },
  { group: "component", command: "add", bridgeCommand: "component.add", buildArgs: componentArgs },
  { group: "component", command: "remove", bridgeCommand: "component.remove", buildArgs: componentArgs },
  { group: "component", command: "set", bridgeCommand: "component.set", buildArgs: componentArgs }
];

function gameObjectArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...targetArgs(flags, positionalTarget),
    newName: stringFlag(flags, "new-name") ?? stringFlag(flags, "set-name"),
    parent: stringFlag(flags, "parent"),
    destination: stringFlag(flags, "destination") ?? stringFlag(flags, "to"),
    lookAt: stringFlag(flags, "look-at") ?? stringFlag(flags, "lookAt"),
    active: boolFlag(flags, "active"),
    layerValue: stringFlag(flags, "layer-value") ?? stringFlag(flags, "set-layer"),
    tagValue: stringFlag(flags, "tag-value") ?? stringFlag(flags, "set-tag"),
    primitive: stringFlag(flags, "primitive"),
    x: stringFlag(flags, "x"),
    y: stringFlag(flags, "y"),
    z: stringFlag(flags, "z"),
    offsetX: stringFlag(flags, "offset-x") ?? stringFlag(flags, "dx"),
    offsetY: stringFlag(flags, "offset-y") ?? stringFlag(flags, "dy"),
    offsetZ: stringFlag(flags, "offset-z") ?? stringFlag(flags, "dz"),
    rotationX: stringFlag(flags, "rotation-x"),
    rotationY: stringFlag(flags, "rotation-y"),
    rotationZ: stringFlag(flags, "rotation-z"),
    scaleX: stringFlag(flags, "scale-x"),
    scaleY: stringFlag(flags, "scale-y"),
    scaleZ: stringFlag(flags, "scale-z"),
    dryRun: boolFlag(flags, "dry-run") ?? boolFlag(flags, "preflight")
  });
}

function componentArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...targetArgs(flags, positionalTarget),
    component: stringFlag(flags, "component") ?? stringFlag(flags, "type"),
    property: stringFlag(flags, "property"),
    value: stringFlag(flags, "value"),
    dryRun: boolFlag(flags, "dry-run") ?? boolFlag(flags, "preflight")
  });
}
