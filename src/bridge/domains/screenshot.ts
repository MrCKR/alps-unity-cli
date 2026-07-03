import { boolFlag, compactArgs, floatFlag, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const screenshotBridgeSpecs: BridgeCommandSpec[] = [
  { group: "screenshot", command: "capture", bridgeCommand: "project-tool.run", toolName: "screenshot.capture", buildArgs: screenshotArgs },
  { group: "screenshot", command: "game-view", bridgeCommand: "screenshot.game-view", buildArgs: screenshotArgs },
  { group: "screenshot", command: "scene-view", bridgeCommand: "screenshot.scene-view", buildArgs: screenshotArgs },
  { group: "screenshot", command: "isolated-object", bridgeCommand: "screenshot.isolated-object", buildArgs: isolatedArgs },
  { group: "screenshot", command: "orbit", bridgeCommand: "screenshot.orbit", buildArgs: orbitArgs },
  { group: "screenshot", command: "surround", bridgeCommand: "screenshot.surround", buildArgs: surroundArgs }
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
    overwrite: boolFlag(flags, "overwrite") ?? boolValue(structured, "overwrite"),
    includeDiagnostics: boolFlag(flags, "include-diagnostics") ?? boolValue(structured, "includeDiagnostics"),
    inlineBase64: boolFlag(flags, "inline-base64") ?? boolValue(structured, "inlineBase64")
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
    inlineBase64: boolFlag(flags, "inline-base64") ?? boolValue(structured, "inlineBase64")
  });
}

function orbitArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringValue(structured, "target"),
    "output-dir": stringFlag(flags, "output-dir") ?? stringFlag(flags, "dir") ?? stringValue(structured, "output-dir") ?? stringValue(structured, "outputDir") ?? stringValue(structured, "dir"),
    count: numberFlag(flags, "count") ?? numberValue(structured, "count"),
    radius: floatFlag(flags, "radius") ?? numberValue(structured, "radius"),
    width: numberFlag(flags, "width") ?? numberValue(structured, "width"),
    height: numberFlag(flags, "height") ?? numberValue(structured, "height")
  });
}

function surroundArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringValue(structured, "target"),
    "output-dir": stringFlag(flags, "output-dir") ?? stringFlag(flags, "dir") ?? stringValue(structured, "output-dir") ?? stringValue(structured, "outputDir") ?? stringValue(structured, "dir"),
    contactSheetPath: stringFlag(flags, "contact-sheet") ?? stringFlag(flags, "contact-sheet-path") ?? stringValue(structured, "contactSheetPath") ?? stringValue(structured, "contact-sheet"),
    width: numberFlag(flags, "width") ?? numberValue(structured, "width"),
    height: numberFlag(flags, "height") ?? numberValue(structured, "height"),
    radius: floatFlag(flags, "radius") ?? numberValue(structured, "radius"),
    fov: floatFlag(flags, "fov") ?? numberValue(structured, "fov"),
    overwrite: boolFlag(flags, "overwrite") ?? boolValue(structured, "overwrite"),
    inlineBase64: boolFlag(flags, "inline-base64") ?? boolValue(structured, "inlineBase64")
  });
}
