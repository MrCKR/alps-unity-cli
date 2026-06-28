import { boolFlag, compactArgs, numberFlag, stringFlag, targetArgs } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const gameObjectBridgeSpecs: BridgeCommandSpec[] = [
  { group: "go", command: "find", bridgeCommand: "go.find", buildArgs: findArgs },
  { group: "go", command: "inspect", bridgeCommand: "go.inspect", buildArgs: inspectArgs },
  { group: "go", command: "create", bridgeCommand: "go.create", buildArgs: gameObjectArgs },
  { group: "go", command: "modify", bridgeCommand: "go.modify", buildArgs: gameObjectArgs },
  { group: "go", command: "duplicate", bridgeCommand: "go.duplicate", buildArgs: gameObjectArgs },
  { group: "go", command: "move-relative", bridgeCommand: "go.move-relative", buildArgs: gameObjectArgs },
  { group: "go", command: "look-at", bridgeCommand: "go.look-at", buildArgs: gameObjectArgs },
  { group: "go", command: "delete", bridgeCommand: "go.delete", buildArgs: gameObjectArgs },
  { group: "go", command: "set-parent", bridgeCommand: "go.set-parent", buildArgs: gameObjectArgs },
  { group: "component", command: "add", bridgeCommand: "component.add", buildArgs: componentArgs },
  { group: "component", command: "remove", bridgeCommand: "component.remove", buildArgs: componentArgs },
  { group: "component", command: "set", bridgeCommand: "component.set", buildArgs: componentArgs },
  { group: "component", command: "get", bridgeCommand: "component.get", buildArgs: componentDeepArgs },
  { group: "component", command: "modify", bridgeCommand: "component.modify", buildArgs: componentDeepArgs },
  { group: "component", command: "list-types", bridgeCommand: "component.list-types", buildArgs: componentListTypesArgs }
];

/** go.find 的搜索参数解析：支持 describe 中公开的 searchTerm/name/target 等字段，并避免实例 --target 泄漏。 */
function findArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    searchTerm: stringFlag(flags, "search-term")
      ?? stringFlag(flags, "query")
      ?? stringFlag(flags, "name")
      ?? stringValue(structured.searchTerm)
      ?? stringValue(structured.query)
      ?? stringValue(structured.name)
      ?? stringValue(structured.target)
      ?? positionalTarget,
    target: stringValue(structured.target) ?? positionalTarget,
    name: stringFlag(flags, "name") ?? stringValue(structured.name),
    tag: stringFlag(flags, "tag") ?? stringValue(structured.tag),
    layer: stringFlag(flags, "layer") ?? stringValue(structured.layer),
    component: stringFlag(flags, "component") ?? stringValue(structured.component),
    path: stringFlag(flags, "path") ?? stringValue(structured.path),
    id: stringFlag(flags, "id") ?? stringValue(structured.id),
    searchMethod: stringFlag(flags, "search-method") ?? stringValue(structured.searchMethod),
    includeInactive: boolFlag(flags, "include-inactive") ?? boolValue(structured.includeInactive),
    pageSize: numberFlag(flags, "page-size") ?? numberValue(structured.pageSize),
    cursor: numberFlag(flags, "cursor") ?? numberValue(structured.cursor)
  });
}

function gameObjectArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  const position = vectorValue(structured.position);
  const rotation = vectorValue(structured.rotation);
  const scale = vectorValue(structured.scale);
  const offset = vectorValue(structured.offset);
  const baseTargetArgs = command === "go.create"
    ? compactArgs({ target: positionalTarget ?? stringFlag(flags, "target"), name: stringFlag(flags, "name") })
    : targetArgs(flags, positionalTarget);

  return compactArgs({
    ...baseTargetArgs,
    searchTerm: stringFlag(flags, "search-term") ?? stringFlag(flags, "query") ?? stringValue(structured.searchTerm) ?? stringValue(structured.query),
    searchMethod: stringFlag(flags, "search-method") ?? stringValue(structured.searchMethod),
    includeInactive: boolFlag(flags, "include-inactive") ?? boolValue(structured.includeInactive),
    pageSize: numberFlag(flags, "page-size") ?? numberValue(structured.pageSize),
    cursor: numberFlag(flags, "cursor") ?? numberValue(structured.cursor),
    newName: stringFlag(flags, "new-name") ?? stringFlag(flags, "set-name") ?? stringValue(structured.newName),
    parent: stringFlag(flags, "parent") ?? stringValue(structured.parent),
    destination: stringFlag(flags, "destination") ?? stringFlag(flags, "to") ?? stringValue(structured.destination),
    lookAt: stringFlag(flags, "look-at") ?? stringFlag(flags, "lookAt") ?? stringValue(structured.lookAt),
    active: boolFlag(flags, "active") ?? boolValue(structured.active),
    layerValue: stringFlag(flags, "layer-value") ?? stringFlag(flags, "set-layer") ?? stringValue(structured.layerValue),
    tagValue: stringFlag(flags, "tag-value") ?? stringFlag(flags, "set-tag") ?? stringValue(structured.tagValue),
    primitive: stringFlag(flags, "primitive") ?? stringValue(structured.primitive),
    x: stringFlag(flags, "x") ?? numberValue(position?.x),
    y: stringFlag(flags, "y") ?? numberValue(position?.y),
    z: stringFlag(flags, "z") ?? numberValue(position?.z),
    offsetX: stringFlag(flags, "offset-x") ?? stringFlag(flags, "dx") ?? numberValue(offset?.x),
    offsetY: stringFlag(flags, "offset-y") ?? stringFlag(flags, "dy") ?? numberValue(offset?.y),
    offsetZ: stringFlag(flags, "offset-z") ?? stringFlag(flags, "dz") ?? numberValue(offset?.z),
    rotationX: stringFlag(flags, "rotation-x") ?? numberValue(rotation?.x),
    rotationY: stringFlag(flags, "rotation-y") ?? numberValue(rotation?.y),
    rotationZ: stringFlag(flags, "rotation-z") ?? numberValue(rotation?.z),
    scaleX: stringFlag(flags, "scale-x") ?? numberValue(scale?.x),
    scaleY: stringFlag(flags, "scale-y") ?? numberValue(scale?.y),
    scaleZ: stringFlag(flags, "scale-z") ?? numberValue(scale?.z),
    worldPositionStays: boolFlag(flags, "world-position-stays") ?? boolValue(structured.worldPositionStays),
    dryRun: boolFlag(flags, "dry-run") ?? boolFlag(flags, "preflight") ?? boolValue(structured.dryRun)
  });
}

/** go.inspect 的目标解析：优先使用 --input.target，避免与 tools run --target 实例选择冲突。 */
function inspectArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: stringValue(structured.target) ?? positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name"),
    name: stringFlag(flags, "name") ?? stringValue(structured.name),
    path: stringFlag(flags, "path") ?? stringValue(structured.path),
    id: stringFlag(flags, "id") ?? stringValue(structured.id)
  });
}

function componentArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  const component = stringFlag(flags, "component")
    ?? stringFlag(flags, "type")
    ?? stringFlag(flags, "component-type")
    ?? stringValue(structured.component)
    ?? stringValue(structured.componentType);

  return compactArgs({
    ...targetArgs(flags, positionalTarget),
    component,
    components: structured.components,
    property: stringFlag(flags, "property") ?? stringValue(structured.property),
    value: flags.value !== undefined ? flags.value : structured.value,
    properties: structured.properties ?? stringFlag(flags, "properties"),
    componentIndex: numberFlag(flags, "component-index") ?? numberValue(structured.componentIndex),
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

function stringValue(value: unknown): string | undefined {
  return typeof value === "string" ? value : undefined;
}

function numberValue(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function boolValue(value: unknown): boolean | undefined {
  return typeof value === "boolean" ? value : undefined;
}

function vectorValue(value: unknown): { x?: unknown; y?: unknown; z?: unknown } | undefined {
  return value && typeof value === "object" && !Array.isArray(value) ? value as { x?: unknown; y?: unknown; z?: unknown } : undefined;
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
    viewQuery: structured.viewQuery,
    namePattern: stringFlag(flags, "name-pattern") ?? structured.namePattern,
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
