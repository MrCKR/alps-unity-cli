import { boolFlag, compactArgs, floatFlag, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const screenshotBridgeSpecs: BridgeCommandSpec[] = [
  { group: "screenshot", command: "game-view", bridgeCommand: "project-tool.run", toolName: "screenshot.game-view", buildArgs: screenshotArgs, destructiveHint: true },
  { group: "screenshot", command: "scene-view", bridgeCommand: "project-tool.run", toolName: "screenshot.scene-view", buildArgs: screenshotArgs, destructiveHint: true },
  { group: "screenshot", command: "isolated-object", bridgeCommand: "project-tool.run", toolName: "screenshot.isolated-object", buildArgs: isolatedArgs, destructiveHint: true },
  { group: "screenshot", command: "orbit", bridgeCommand: "project-tool.run", toolName: "screenshot.orbit", buildArgs: orbitArgs, destructiveHint: true },
  { group: "screenshot", command: "surround", bridgeCommand: "project-tool.run", toolName: "screenshot.surround", buildArgs: surroundArgs, destructiveHint: true, supportsDryRun: true },
  { group: "screenshot", command: "editor-element", bridgeCommand: "project-tool.run", toolName: "screenshot.editor-element", buildArgs: editorElementArgs, supportsDryRun: true }
];

// 读取 tools run --input 中无法映射为简单 flag 的结构化字段（path/width/height/target 等）
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

function stringValue(structured: Record<string, unknown>, key: string): string | undefined {
  const value = structured[key];
  return typeof value === "string" ? value : undefined;
}

function numberValue(structured: Record<string, unknown>, key: string): number | undefined {
  const value = structured[key];
  return typeof value === "number" ? value : undefined;
}

function boolValue(structured: Record<string, unknown>, key: string): boolean | undefined {
  const value = structured[key];
  return typeof value === "boolean" ? value : undefined;
}

function screenshotArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "output") ?? stringValue(structured, "path") ?? stringValue(structured, "output"),
    width: numberFlag(flags, "width") ?? numberValue(structured, "width"),
    height: numberFlag(flags, "height") ?? numberValue(structured, "height"),
    format: stringFlag(flags, "format") ?? stringValue(structured, "format"),
    transparent: boolFlag(flags, "transparent") ?? boolValue(structured, "transparent"),
    quality: numberFlag(flags, "quality") ?? numberValue(structured, "quality"),
    superSize: numberFlag(flags, "super-size") ?? numberValue(structured, "superSize"),
    overwrite: boolFlag(flags, "overwrite") ?? boolValue(structured, "overwrite"),
    includeDiagnostics: boolFlag(flags, "include-diagnostics") ?? boolValue(structured, "includeDiagnostics"),
    inlineBase64: boolFlag(flags, "inline-base64") ?? boolValue(structured, "inlineBase64"),
    limits: structured.limits,
    budget: structured.budget
  });
}

function isolatedArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringValue(structured, "target"),
    path: stringFlag(flags, "path") ?? stringFlag(flags, "output") ?? stringValue(structured, "path") ?? stringValue(structured, "output"),
    width: numberFlag(flags, "width") ?? numberValue(structured, "width"),
    height: numberFlag(flags, "height") ?? numberValue(structured, "height"),
    orthographic: boolFlag(flags, "orthographic") ?? boolValue(structured, "orthographic"),
    size: floatFlag(flags, "size") ?? numberValue(structured, "size"),
    overwrite: boolFlag(flags, "overwrite") ?? boolValue(structured, "overwrite"),
    inlineBase64: boolFlag(flags, "inline-base64") ?? boolValue(structured, "inlineBase64"),
    limits: structured.limits,
    budget: structured.budget
  });
}

function orbitArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringValue(structured, "target"),
    outputDir: stringFlag(flags, "output-dir") ?? stringFlag(flags, "dir") ?? stringValue(structured, "output-dir") ?? stringValue(structured, "outputDir") ?? stringValue(structured, "dir"),
    count: numberFlag(flags, "count") ?? numberValue(structured, "count"),
    radius: floatFlag(flags, "radius") ?? numberValue(structured, "radius"),
    width: numberFlag(flags, "width") ?? numberValue(structured, "width"),
    height: numberFlag(flags, "height") ?? numberValue(structured, "height"),
    overwrite: boolFlag(flags, "overwrite") ?? boolValue(structured, "overwrite"),
    limits: structured.limits,
    budget: structured.budget
  });
}

function surroundArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringValue(structured, "target"),
    outputDir: stringFlag(flags, "output-dir") ?? stringFlag(flags, "dir") ?? stringValue(structured, "output-dir") ?? stringValue(structured, "outputDir") ?? stringValue(structured, "dir"),
    contactSheetPath: stringFlag(flags, "contact-sheet") ?? stringFlag(flags, "contact-sheet-path") ?? stringValue(structured, "contactSheetPath") ?? stringValue(structured, "contact-sheet"),
    width: numberFlag(flags, "width") ?? numberValue(structured, "width"),
    height: numberFlag(flags, "height") ?? numberValue(structured, "height"),
    radius: floatFlag(flags, "radius") ?? numberValue(structured, "radius"),
    fov: floatFlag(flags, "fov") ?? numberValue(structured, "fov"),
    overwrite: boolFlag(flags, "overwrite") ?? boolValue(structured, "overwrite"),
    inlineBase64: boolFlag(flags, "inline-base64") ?? boolValue(structured, "inlineBase64"),
    limits: structured.limits,
    budget: structured.budget,
    dryRun: boolFlag(flags, "dry-run") ?? boolValue(structured, "dryRun")
  });
}

function editorElementArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    window: stringFlag(flags, "window") ?? stringValue(structured, "window"),
    selector: stringFlag(flags, "selector") ?? stringValue(structured, "selector"),
    maxWidth: numberFlag(flags, "max-width") ?? numberValue(structured, "maxWidth"),
    maxHeight: numberFlag(flags, "max-height") ?? numberValue(structured, "maxHeight"),
    maxBytes: numberFlag(flags, "max-bytes") ?? numberValue(structured, "maxBytes"),
    budget: structured.budget
  });
}
