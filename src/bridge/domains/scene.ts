import { boolFlag, compactArgs, numberFlag, queryArgs, screenshotArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const sceneBridgeSpecs: BridgeCommandSpec[] = [
  { group: "scene", command: "active", bridgeCommand: "scene.active", buildArgs: ({ flags }) => queryArgs(flags) },
  { group: "scene", command: "hierarchy", bridgeCommand: "scene.hierarchy", buildArgs: sceneHierarchyArgs },
  { group: "scene", command: "build-settings", bridgeCommand: "scene.build-settings", buildArgs: ({ flags }) => queryArgs(flags) },
  { group: "scene", command: "screenshot", bridgeCommand: "scene.screenshot", buildArgs: ({ flags, positionalTarget }) => screenshotArgs(flags, positionalTarget) },
  { group: "scene", command: "create", bridgeCommand: "scene.create", buildArgs: sceneFileArgs },
  { group: "scene", command: "load", bridgeCommand: "scene.load", buildArgs: sceneFileArgs },
  { group: "scene", command: "save", bridgeCommand: "scene.save", buildArgs: sceneFileArgs },
  { group: "scene", command: "unload", bridgeCommand: "scene.unload", buildArgs: sceneFileArgs },
  { group: "scene", command: "set-active", bridgeCommand: "scene.set-active", buildArgs: sceneFileArgs },
  { group: "scene", command: "list-opened", bridgeCommand: "scene.list-opened", buildArgs: ({ flags }) => queryArgs(flags) },
  { group: "scene", command: "data", bridgeCommand: "scene.data", buildArgs: sceneDataArgs },
  { group: "scene", command: "move-object", bridgeCommand: "scene.move-object", buildArgs: sceneMoveObjectArgs },
  { group: "scene", command: "validate", bridgeCommand: "scene.validate", buildArgs: sceneValidateArgs },
  { group: "scene", command: "view-frame", bridgeCommand: "scene.view-frame", buildArgs: ({ flags }) => { const s = structuredInput(flags); return compactArgs({ target: stringFlag(flags, "target") ?? stringValue(s.target), path: stringFlag(flags, "path") ?? stringValue(s.path), frameAll: boolFlag(flags, "frame-all") ?? boolValue(s.frameAll) }); } }
];

function sceneFileArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({ path: positionalTarget ?? stringFlag(flags, "path") ?? stringValue(structured.path), name: stringFlag(flags, "name") ?? stringValue(structured.name), dryRun: boolFlag(flags, "dry-run") ?? boolFlag(flags, "preflight") ?? boolValue(structured.dryRun) });
}

function sceneDataArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({ maxDepth: numberFlag(flags, "max-depth") ?? numberValue(structured.maxDepth), maxResults: numberFlag(flags, "max-results") ?? numberValue(structured.maxResults), nameFilter: stringFlag(flags, "name-filter") ?? stringValue(structured.nameFilter), includeComponents: boolFlag(flags, "include-components") ?? boolValue(structured.includeComponents) });
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
    nameFilter: stringFlag(flags, "name-filter") ?? stringFlag(flags, "filter") ?? stringValue(structured.nameFilter),
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
