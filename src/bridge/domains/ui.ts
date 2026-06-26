import { boolFlag, compactArgs, numberFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const uiBridgeSpecs: BridgeCommandSpec[] = [
  { group: "ui", command: "canvas/create", bridgeCommand: "ui.canvas.create", buildArgs: uiArgs },
  { group: "ui", command: "text/create", bridgeCommand: "ui.text.create", buildArgs: uiArgs },
  { group: "ui", command: "button/create", bridgeCommand: "ui.button.create", buildArgs: uiArgs },
  { group: "ui", command: "image/create", bridgeCommand: "ui.image.create", buildArgs: uiArgs },
  { group: "ui", command: "document/info", bridgeCommand: "ui.document.info", buildArgs: uiArgs },
  { group: "ui", command: "document/create", bridgeCommand: "ui.document.create", buildArgs: uiArgs },
  { group: "ui", command: "hierarchy", bridgeCommand: "ui.hierarchy", buildArgs: uiArgs },
  { group: "ui", command: "attach-document", bridgeCommand: "ui.attach-document", buildArgs: uiAttachArgs },
  { group: "ui", command: "create-panel-settings", bridgeCommand: "ui.create-panel-settings", buildArgs: uiPanelArgs },
  { group: "ui", command: "get-visual-tree", bridgeCommand: "ui.get-visual-tree", buildArgs: uiVisualTreeArgs }
];

// 读取 tools run --input 中无法映射为简单 flag 的结构化字段
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

function stringField(flags: BridgeFlags, key: string): string | undefined {
  const value = structuredInput(flags)[key];
  return typeof value === "string" ? value : undefined;
}

function numberField(flags: BridgeFlags, key: string): number | undefined {
  const value = structuredInput(flags)[key];
  return typeof value === "number" ? value : undefined;
}

function boolField(flags: BridgeFlags, key: string): boolean | undefined {
  const value = structuredInput(flags)[key];
  return typeof value === "boolean" ? value : undefined;
}

function uiArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  if (command === "ui.hierarchy") {
    return compactArgs({ ...queryArgs(flags), target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? stringField(flags, "target"), pageSize: numberFlag(flags, "page-size") ?? numberField(flags, "pageSize"), cursor: stringFlag(flags, "cursor") ?? stringField(flags, "cursor") });
  }
  if (command === "ui.document.info") {
    const documentTarget = positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? stringField(flags, "target");
    return compactArgs({
      target: documentTarget,
      path: stringFlag(flags, "path") ?? stringFlag(flags, "uxml") ?? stringField(flags, "path") ?? stringField(flags, "uxml") ?? (looksLikeUiAssetPath(documentTarget) ? documentTarget : undefined),
      uss: stringFlag(flags, "uss") ?? stringFlag(flags, "style") ?? stringField(flags, "uss")
    });
  }
  if (command === "ui.document.create") {
    return compactArgs({
      target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
      name: stringFlag(flags, "name") ?? stringField(flags, "name"),
      uxml: stringFlag(flags, "uxml") ?? stringFlag(flags, "path") ?? stringField(flags, "uxml"),
      uss: stringFlag(flags, "uss") ?? stringFlag(flags, "style") ?? stringField(flags, "uss"),
      panelSettings: stringFlag(flags, "panel-settings") ?? stringField(flags, "panelSettings"),
      overwrite: boolFlag(flags, "overwrite") ?? boolField(flags, "overwrite")
    });
  }
  return compactArgs({
    name: positionalTarget ?? stringFlag(flags, "name") ?? stringField(flags, "name"),
    parent: stringFlag(flags, "parent") ?? stringFlag(flags, "canvas") ?? stringField(flags, "parent") ?? stringField(flags, "canvas"),
    text: stringFlag(flags, "text") ?? stringFlag(flags, "label") ?? stringField(flags, "text"),
    sprite: stringFlag(flags, "sprite") ?? stringFlag(flags, "path") ?? stringField(flags, "sprite"),
    color: stringFlag(flags, "color") ?? stringField(flags, "color"),
    width: numberFlag(flags, "width") ?? numberField(flags, "width"),
    height: numberFlag(flags, "height") ?? numberField(flags, "height"),
    x: numberFlag(flags, "x") ?? numberField(flags, "x"),
    y: numberFlag(flags, "y") ?? numberField(flags, "y"),
    fontSize: numberFlag(flags, "font-size") ?? numberField(flags, "fontSize")
  });
}

function looksLikeUiAssetPath(value: string | undefined): boolean {
  return typeof value === "string" && /\.(uxml|uss)$/i.test(value);
}

function uiAttachArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    sourceAsset: stringFlag(flags, "source-asset") ?? stringFlag(flags, "uxml") ?? stringField(flags, "sourceAsset") ?? stringField(flags, "uxml"),
    panelSettings: stringFlag(flags, "panel-settings") ?? stringField(flags, "panelSettings"),
    sortOrder: numberFlag(flags, "sort-order") ?? numberField(flags, "sortOrder")
  });
}

function uiPanelArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringField(flags, "path"),
    scaleMode: stringFlag(flags, "scale-mode") ?? stringField(flags, "scaleMode"),
    referenceWidth: numberFlag(flags, "reference-width") ?? numberField(flags, "referenceWidth"),
    referenceHeight: numberFlag(flags, "reference-height") ?? numberField(flags, "referenceHeight")
  });
}

function uiVisualTreeArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    maxDepth: numberFlag(flags, "max-depth") ?? numberField(flags, "maxDepth")
  });
}
