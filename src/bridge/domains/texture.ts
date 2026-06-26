import { boolFlag, compactArgs, contentFlag, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const textureBridgeSpecs: BridgeCommandSpec[] = [
  { group: "texture", command: "info", bridgeCommand: "texture.info", buildArgs: textureArgs },
  { group: "texture", command: "create", bridgeCommand: "texture.create", buildArgs: textureArgs },
  { group: "texture", command: "sprite", bridgeCommand: "texture.sprite", buildArgs: textureArgs },
  { group: "texture", command: "import-settings", bridgeCommand: "texture.import-settings", buildArgs: textureArgs },
  { group: "texture", command: "generate", bridgeCommand: "texture.generate", buildArgs: textureGenerateArgs, supportsDryRun: true }
];

// 读取 tools run --input 中无法映射为简单 flag 的结构化字段（width/height/enum/bool 等）
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

function textureArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "texture") ?? stringField(flags, "path"),
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "path") ?? stringFlag(flags, "texture") ?? stringField(flags, "target") ?? stringField(flags, "path"),
    source: stringFlag(flags, "source") ?? stringField(flags, "source"),
    overwrite: boolFlag(flags, "overwrite") ?? boolField(flags, "overwrite"),
    width: numberFlag(flags, "width") ?? numberField(flags, "width"),
    height: numberFlag(flags, "height") ?? numberField(flags, "height"),
    color: stringFlag(flags, "color") ?? stringField(flags, "color"),
    textureType: stringFlag(flags, "texture-type") ?? stringFlag(flags, "type") ?? stringField(flags, "textureType") ?? stringField(flags, "type"),
    spriteMode: stringFlag(flags, "sprite-mode") ?? stringFlag(flags, "mode") ?? stringField(flags, "spriteMode") ?? stringField(flags, "mode"),
    pixelsPerUnit: numberFlag(flags, "pixels-per-unit") ?? numberFlag(flags, "ppu") ?? numberField(flags, "pixelsPerUnit"),
    pivotX: stringFlag(flags, "pivot-x") ?? stringField(flags, "pivotX"),
    pivotY: stringFlag(flags, "pivot-y") ?? stringField(flags, "pivotY"),
    readable: boolFlag(flags, "readable") ?? boolField(flags, "readable"),
    mipmap: boolFlag(flags, "mipmap") ?? boolField(flags, "mipmap"),
    sRGB: boolFlag(flags, "srgb") ?? boolField(flags, "sRGB"),
    alphaIsTransparency: boolFlag(flags, "alpha-is-transparency") ?? boolField(flags, "alphaIsTransparency"),
    wrapMode: stringFlag(flags, "wrap-mode") ?? stringField(flags, "wrapMode"),
    filterMode: stringFlag(flags, "filter-mode") ?? stringField(flags, "filterMode"),
    maxTextureSize: numberFlag(flags, "max-texture-size") ?? numberField(flags, "maxTextureSize"),
    compression: stringFlag(flags, "compression") ?? stringField(flags, "compression")
  });
}

function textureGenerateArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringField(flags, "path"),
    width: numberFlag(flags, "width") ?? numberField(flags, "width"),
    height: numberFlag(flags, "height") ?? numberField(flags, "height"),
    pattern: stringFlag(flags, "pattern") ?? stringField(flags, "pattern"),
    color: stringFlag(flags, "color") ?? stringField(flags, "color"),
    color2: stringFlag(flags, "color2") ?? stringField(flags, "color2"),
    patternSize: numberFlag(flags, "pattern-size") ?? numberField(flags, "patternSize"),
    dryRun: boolFlag(flags, "dry-run") ?? boolField(flags, "dryRun")
  });
}
