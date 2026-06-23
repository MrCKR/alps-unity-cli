import { boolFlag, compactArgs, numberFlag, stringFlag, targetArgs } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const gameObjectBridgeSpecs: BridgeCommandSpec[] = [
  { group: "go", command: "find", bridgeCommand: "go.find", buildArgs: ({ flags, positionalTarget }) => targetArgs(flags, positionalTarget) },
  { group: "go", command: "inspect", bridgeCommand: "go.inspect", buildArgs: ({ flags, positionalTarget }) => targetArgs(flags, positionalTarget) },
  { group: "go", command: "create", bridgeCommand: "go.create", buildArgs: gameObjectArgs },
  { group: "go", command: "modify", bridgeCommand: "go.modify", buildArgs: gameObjectArgs },
  { group: "go", command: "duplicate", bridgeCommand: "go.duplicate", buildArgs: gameObjectArgs },
  { group: "go", command: "move-relative", bridgeCommand: "go.move-relative", buildArgs: gameObjectArgs },
  { group: "go", command: "look-at", bridgeCommand: "go.look-at", buildArgs: gameObjectArgs },
  { group: "go", command: "delete", bridgeCommand: "go.delete", buildArgs: gameObjectArgs },
  { group: "go", command: "set-parent", bridgeCommand: "go.set-parent", buildArgs: gameObjectArgs, supportsDryRun: true },
  { group: "component", command: "add", bridgeCommand: "component.add", buildArgs: componentArgs },
  { group: "component", command: "remove", bridgeCommand: "component.remove", buildArgs: componentArgs },
  { group: "component", command: "set", bridgeCommand: "component.set", buildArgs: componentArgs },
  { group: "component", command: "get", bridgeCommand: "component.get", buildArgs: componentDeepArgs },
  { group: "component", command: "modify", bridgeCommand: "component.modify", buildArgs: componentDeepArgs, supportsDryRun: true },
  { group: "component", command: "list-types", bridgeCommand: "component.list-types", buildArgs: componentListTypesArgs }
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

/** 从 __structuredInput 提取复杂字段（pathPatches/jsonPatch 等数组/对象）。 */
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

/** component.get / component.modify 的参数构建：支持 ObjectRef + 深序列化选项 + patch。 */
function componentDeepArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    instanceId: numberFlag(flags, "instance-id") ?? structured.instanceId,
    path: stringFlag(flags, "path") ?? stringFlag(flags, "target") ?? structured.path,
    name: stringFlag(flags, "name") ?? structured.name,
    componentInstanceId: numberFlag(flags, "component-instance-id") ?? structured.componentInstanceId,
    componentIndex: numberFlag(flags, "component-index") ?? structured.componentIndex,
    component: stringFlag(flags, "component") ?? stringFlag(flags, "type") ?? structured.component,
    componentType: stringFlag(flags, "component-type") ?? structured.componentType,
    maxDepth: numberFlag(flags, "max-depth") ?? structured.maxDepth,
    includeFields: boolFlag(flags, "include-fields") ?? structured.includeFields,
    includeProperties: boolFlag(flags, "include-properties") ?? structured.includeProperties,
    deepSerialization: boolFlag(flags, "deep-serialization") ?? structured.deepSerialization,
    paths: structured.paths,
    pathPatches: structured.pathPatches,
    jsonPatch: structured.jsonPatch,
    componentDiff: structured.componentDiff,
    dryRun: boolFlag(flags, "dry-run")
  });
}

/** component.list-types 的参数构建。 */
function componentListTypesArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    keyword: stringFlag(flags, "keyword") ?? stringFlag(flags, "search") ?? structured.keyword,
    pageSize: numberFlag(flags, "page-size") ?? structured.pageSize,
    cursor: numberFlag(flags, "cursor") ?? structured.cursor
  });
}
