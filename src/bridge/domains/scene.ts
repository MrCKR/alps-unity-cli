import { boolFlag, compactArgs, numberFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const sceneBridgeSpecs: BridgeCommandSpec[] = [
  { group: "scene", command: "active", bridgeCommand: "project-tool.run", toolName: "scene.active", buildArgs: ({ flags }) => queryArgs(flags) },
  { group: "scene", command: "hierarchy", bridgeCommand: "project-tool.run", toolName: "scene.hierarchy", buildArgs: sceneHierarchyArgs },
  { group: "scene", command: "create", bridgeCommand: "project-tool.run", toolName: "scene.create", buildArgs: sceneFileArgs, destructiveHint: true },
  { group: "scene", command: "load", bridgeCommand: "project-tool.run", toolName: "scene.load", buildArgs: sceneFileArgs },
  { group: "scene", command: "save", bridgeCommand: "project-tool.run", toolName: "scene.save", buildArgs: sceneSaveArgs, destructiveHint: true, supportsDryRun: true },
  { group: "scene", command: "unload", bridgeCommand: "project-tool.run", toolName: "scene.unload", buildArgs: sceneFileArgs, destructiveHint: true, supportsDryRun: true },
  { group: "scene", command: "set-active", bridgeCommand: "project-tool.run", toolName: "scene.set-active", buildArgs: sceneFileArgs },
  { group: "scene", command: "list-opened", bridgeCommand: "project-tool.run", toolName: "scene.list-opened", buildArgs: ({ flags }) => queryArgs(flags) },
  { group: "scene", command: "gameobject", bridgeCommand: "project-tool.run", toolName: "scene.gameobject", buildArgs: sceneGameObjectReadArgs },
  { group: "scene", command: "gameobject-component", bridgeCommand: "project-tool.run", toolName: "scene.gameobject.component", buildArgs: sceneGameObjectReadArgs },
  { group: "scene", command: "gameobject-components", bridgeCommand: "project-tool.run", toolName: "scene.gameobject.components", buildArgs: sceneGameObjectReadArgs },
  { group: "scene", command: "move-object", bridgeCommand: "project-tool.run", toolName: "scene.move-object", buildArgs: sceneMoveObjectArgs, supportsDryRun: true },
  { group: "scene", command: "validate", bridgeCommand: "project-tool.run", toolName: "scene.validate", buildArgs: sceneValidateArgs },
  { group: "scene", command: "view-frame", bridgeCommand: "project-tool.run", toolName: "scene.view-frame", buildArgs: ({ flags }) => { const s = structuredInput(flags); return compactArgs({ target: stringFlag(flags, "target") ?? stringValue(s.target), path: stringFlag(flags, "path") ?? stringValue(s.path), frameAll: boolFlag(flags, "frame-all") ?? boolValue(s.frameAll) }); } }
];

function sceneFileArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({ path: positionalTarget ?? stringFlag(flags, "path") ?? stringValue(structured.path), name: stringFlag(flags, "name") ?? stringValue(structured.name), dryRun: boolFlag(flags, "dry-run") ?? boolFlag(flags, "preflight") ?? boolValue(structured.dryRun) });
}

// scene.save 的 all 是显式业务模式，不能泄漏到其他 scene file Tool。
function sceneSaveArgs(context: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(context.flags);
  return compactArgs({
    ...sceneFileArgs(context),
    all: boolFlag(context.flags, "all") ?? boolValue(structured.all)
  });
}

/** 统一投影三个 GameObject 读取 Tool 的可选 selector 与读取深度字段。 */
function sceneGameObjectReadArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringValue(structured.target),
    id: numberFlag(flags, "id") ?? numberValue(structured.id),
    path: stringFlag(flags, "path") ?? stringValue(structured.path),
    component: stringFlag(flags, "component") ?? stringValue(structured.component),
    includeComponents: boolFlag(flags, "include-components") ?? boolValue(structured.includeComponents),
    includeProperties: boolFlag(flags, "include-properties") ?? boolValue(structured.includeProperties)
  });
}

// scene.move-object: target GameObject + destination scene path
function sceneMoveObjectArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringValue(structured.target),
    path: stringFlag(flags, "path") ?? stringValue(structured.path),
    instanceId: numberFlag(flags, "instance-id") ?? numberValue(structured.instanceId),
    scene: stringFlag(flags, "scene") ?? stringValue(structured.scene),
    dryRun: boolFlag(flags, "dry-run") ?? boolValue(structured.dryRun)
  });
}

// scene.validate: optional scene path
function sceneValidateArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({ path: stringFlag(flags, "path") ?? stringValue(structured.path) });
}

/** scene.hierarchy 的参数构建：对齐 Coplay get_hierarchy 的 parent/depth/filter/组件预览控制。 */
function sceneHierarchyArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    pageSize: numberFlag(flags, "page-size") ?? numberFlag(flags, "limit") ?? numberValue(structured.pageSize),
    cursor: numberFlag(flags, "cursor") ?? numberValue(structured.cursor),
    parent: stringFlag(flags, "parent") ?? stringValue(structured.parent),
    maxDepth: numberFlag(flags, "max-depth") ?? numberValue(structured.maxDepth),
    nameFilter: stringFlag(flags, "name-filter") ?? stringFlag(flags, "filter") ?? stringFlag(flags, "query") ?? stringValue(structured.nameFilter) ?? stringValue(structured.query),
    query: stringFlag(flags, "query") ?? stringValue(structured.query),
    componentType: stringFlag(flags, "component-type") ?? stringValue(structured.componentType),
    includeComponents: boolFlag(flags, "include-components") ?? boolValue(structured.includeComponents),
    includeInactive: boolFlag(flags, "include-inactive") ?? boolValue(structured.includeInactive)
  });
}

function structuredInput(flags: Record<string, unknown>): Record<string, unknown> {
  const raw = typeof flags.__structuredInput === "string" ? flags.__structuredInput : undefined;
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw);
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed as Record<string, unknown> : {};
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
