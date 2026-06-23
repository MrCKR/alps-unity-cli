import { boolFlag, compactArgs, floatFlag, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const screenshotBridgeSpecs: BridgeCommandSpec[] = [
  { group: "screenshot", command: "game-view", bridgeCommand: "screenshot.game-view", buildArgs: screenshotArgs },
  { group: "screenshot", command: "scene-view", bridgeCommand: "screenshot.scene-view", buildArgs: screenshotArgs },
  { group: "screenshot", command: "isolated-object", bridgeCommand: "screenshot.isolated-object", buildArgs: isolatedArgs },
  { group: "screenshot", command: "orbit", bridgeCommand: "screenshot.orbit", buildArgs: orbitArgs }
];

function screenshotArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "output"),
    width: numberFlag(flags, "width"),
    height: numberFlag(flags, "height"),
    format: stringFlag(flags, "format"),
    overwrite: boolFlag(flags, "overwrite"),
    includeDiagnostics: boolFlag(flags, "include-diagnostics")
  });
}

function isolatedArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    path: stringFlag(flags, "path") ?? stringFlag(flags, "output"),
    width: numberFlag(flags, "width"),
    height: numberFlag(flags, "height"),
    orthographic: boolFlag(flags, "orthographic"),
    size: floatFlag(flags, "size"),
    overwrite: boolFlag(flags, "overwrite")
  });
}

function orbitArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target"),
    "output-dir": stringFlag(flags, "output-dir") ?? stringFlag(flags, "dir"),
    count: numberFlag(flags, "count"),
    radius: floatFlag(flags, "radius"),
    width: numberFlag(flags, "width"),
    height: numberFlag(flags, "height")
  });
}
