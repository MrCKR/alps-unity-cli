import { boolFlag, compactArgs, floatFlag, numberFlag, queryArgs, screenshotArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const cameraBridgeSpecs: BridgeCommandSpec[] = [
  { group: "camera", command: "list", bridgeCommand: "camera.list", buildArgs: ({ flags }) => queryArgs(flags) },
  { group: "camera", command: "info", bridgeCommand: "camera.info", buildArgs: ({ flags, positionalTarget }) => compactArgs({ target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") }) },
  { group: "camera", command: "screenshot", bridgeCommand: "camera.screenshot", buildArgs: ({ flags, positionalTarget }) => compactArgs({ ...screenshotArgs(flags, undefined), target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") }) },
  { group: "camera", command: "create", bridgeCommand: "camera.create", buildArgs: cameraCreateArgs },
  { group: "camera", command: "target", bridgeCommand: "camera.target", buildArgs: cameraTargetArgs },
  { group: "camera", command: "lens", bridgeCommand: "camera.lens", buildArgs: cameraLensArgs },
  { group: "camera", command: "multiview", bridgeCommand: "camera.multiview", buildArgs: cameraMultiviewArgs }
];

function cameraCreateArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    name: stringFlag(flags, "name") ?? positionalTarget,
    parent: stringFlag(flags, "parent"),
    position: stringFlag(flags, "position"),
    rotation: stringFlag(flags, "rotation"),
    fieldOfView: floatFlag(flags, "field-of-view") ?? floatFlag(flags, "fov"),
    orthographic: boolFlag(flags, "orthographic"),
    orthographicSize: floatFlag(flags, "orthographic-size"),
    clearFlags: stringFlag(flags, "clear-flags"),
    depth: floatFlag(flags, "depth"),
    tag: stringFlag(flags, "tag")
  });
}

function cameraTargetArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name"),
    lookAt: stringFlag(flags, "look-at"),
    position: stringFlag(flags, "position"),
    rotation: stringFlag(flags, "rotation")
  });
}

function cameraLensArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name"),
    fieldOfView: floatFlag(flags, "field-of-view") ?? floatFlag(flags, "fov"),
    orthographic: boolFlag(flags, "orthographic"),
    orthographicSize: floatFlag(flags, "orthographic-size"),
    nearClipPlane: floatFlag(flags, "near-clip-plane"),
    farClipPlane: floatFlag(flags, "far-clip-plane"),
    clearFlags: stringFlag(flags, "clear-flags"),
    depth: floatFlag(flags, "depth")
  });
}

function cameraMultiviewArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    targets: stringFlag(flags, "targets"),
    output: stringFlag(flags, "output"),
    width: numberFlag(flags, "width"),
    height: numberFlag(flags, "height"),
    format: stringFlag(flags, "format")
  });
}
