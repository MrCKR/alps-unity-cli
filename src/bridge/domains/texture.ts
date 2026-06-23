import { boolFlag, compactArgs, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const textureBridgeSpecs: BridgeCommandSpec[] = [
  { group: "texture", command: "info", bridgeCommand: "texture.info", buildArgs: textureArgs },
  { group: "texture", command: "create", bridgeCommand: "texture.create", buildArgs: textureArgs },
  { group: "texture", command: "sprite", bridgeCommand: "texture.sprite", buildArgs: textureArgs },
  { group: "texture", command: "import-settings", bridgeCommand: "texture.import-settings", buildArgs: textureArgs },
  { group: "texture", command: "generate", bridgeCommand: "texture.generate", buildArgs: textureGenerateArgs, supportsDryRun: true }
];

function textureArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "texture"),
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "path") ?? stringFlag(flags, "texture"),
    source: stringFlag(flags, "source") ?? stringFlag(flags, "input"),
    overwrite: boolFlag(flags, "overwrite"),
    width: numberFlag(flags, "width"),
    height: numberFlag(flags, "height"),
    color: stringFlag(flags, "color"),
    textureType: stringFlag(flags, "texture-type") ?? stringFlag(flags, "type"),
    spriteMode: stringFlag(flags, "sprite-mode") ?? stringFlag(flags, "mode"),
    pixelsPerUnit: numberFlag(flags, "pixels-per-unit") ?? numberFlag(flags, "ppu"),
    pivotX: stringFlag(flags, "pivot-x"),
    pivotY: stringFlag(flags, "pivot-y"),
    readable: boolFlag(flags, "readable"),
    mipmap: boolFlag(flags, "mipmap"),
    sRGB: boolFlag(flags, "srgb"),
    alphaIsTransparency: boolFlag(flags, "alpha-is-transparency"),
    wrapMode: stringFlag(flags, "wrap-mode"),
    filterMode: stringFlag(flags, "filter-mode"),
    maxTextureSize: numberFlag(flags, "max-texture-size"),
    compression: stringFlag(flags, "compression")
  });
}

function textureGenerateArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path"),
    width: numberFlag(flags, "width"),
    height: numberFlag(flags, "height"),
    pattern: stringFlag(flags, "pattern"),
    color: stringFlag(flags, "color"),
    color2: stringFlag(flags, "color2"),
    patternSize: numberFlag(flags, "pattern-size"),
    dryRun: boolFlag(flags, "dry-run")
  });
}
