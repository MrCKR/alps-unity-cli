import { boolFlag, compactArgs, queryArgs, screenshotArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const sceneBridgeSpecs: BridgeCommandSpec[] = [
  { group: "scene", command: "active", bridgeCommand: "scene.active", buildArgs: ({ flags }) => queryArgs(flags) },
  { group: "scene", command: "hierarchy", bridgeCommand: "scene.hierarchy", buildArgs: ({ flags }) => queryArgs(flags) },
  { group: "scene", command: "build-settings", bridgeCommand: "scene.build-settings", buildArgs: ({ flags }) => queryArgs(flags) },
  { group: "scene", command: "screenshot", bridgeCommand: "scene.screenshot", buildArgs: ({ flags, positionalTarget }) => screenshotArgs(flags, positionalTarget) },
  { group: "scene", command: "create", bridgeCommand: "scene.create", buildArgs: sceneFileArgs },
  { group: "scene", command: "load", bridgeCommand: "scene.load", buildArgs: sceneFileArgs },
  { group: "scene", command: "save", bridgeCommand: "scene.save", buildArgs: sceneFileArgs },
  { group: "screenshot", command: "capture", bridgeCommand: "screenshot.capture", buildArgs: ({ flags, positionalTarget }) => screenshotArgs(flags, positionalTarget) }
];

function sceneFileArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ path: positionalTarget ?? stringFlag(flags, "path"), name: stringFlag(flags, "name"), dryRun: boolFlag(flags, "dry-run") ?? boolFlag(flags, "preflight") });
}
