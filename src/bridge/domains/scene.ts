import { boolFlag, compactArgs, numberFlag, queryArgs, screenshotArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const sceneBridgeSpecs: BridgeCommandSpec[] = [
  { group: "scene", command: "active", bridgeCommand: "scene.active", buildArgs: ({ flags }) => queryArgs(flags) },
  { group: "scene", command: "hierarchy", bridgeCommand: "scene.hierarchy", buildArgs: ({ flags }) => queryArgs(flags) },
  { group: "scene", command: "build-settings", bridgeCommand: "scene.build-settings", buildArgs: ({ flags }) => queryArgs(flags) },
  { group: "scene", command: "screenshot", bridgeCommand: "scene.screenshot", buildArgs: ({ flags, positionalTarget }) => screenshotArgs(flags, positionalTarget) },
  { group: "scene", command: "create", bridgeCommand: "scene.create", buildArgs: sceneFileArgs },
  { group: "scene", command: "load", bridgeCommand: "scene.load", buildArgs: sceneFileArgs },
  { group: "scene", command: "save", bridgeCommand: "scene.save", buildArgs: sceneFileArgs },
  { group: "scene", command: "unload", bridgeCommand: "scene.unload", buildArgs: sceneFileArgs, supportsDryRun: true },
  { group: "scene", command: "set-active", bridgeCommand: "scene.set-active", buildArgs: sceneFileArgs },
  { group: "scene", command: "list-opened", bridgeCommand: "scene.list-opened", buildArgs: ({ flags }) => queryArgs(flags) },
  { group: "scene", command: "data", bridgeCommand: "scene.data", buildArgs: sceneDataArgs },
  { group: "scene", command: "view-frame", bridgeCommand: "scene.view-frame", buildArgs: ({ flags }) => compactArgs({ target: stringFlag(flags, "target"), path: stringFlag(flags, "path"), frameAll: boolFlag(flags, "frame-all") }) },
  { group: "screenshot", command: "capture", bridgeCommand: "screenshot.capture", buildArgs: ({ flags, positionalTarget }) => screenshotArgs(flags, positionalTarget) }
];

function sceneFileArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ path: positionalTarget ?? stringFlag(flags, "path"), name: stringFlag(flags, "name"), dryRun: boolFlag(flags, "dry-run") ?? boolFlag(flags, "preflight") });
}

function sceneDataArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ maxDepth: numberFlag(flags, "max-depth"), maxResults: numberFlag(flags, "max-results"), nameFilter: stringFlag(flags, "name-filter"), includeComponents: boolFlag(flags, "include-components") });
}
