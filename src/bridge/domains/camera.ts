import { boolFlag, compactArgs, floatFlag, numberFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const cameraBridgeSpecs: BridgeCommandSpec[] = [
  { group: "camera", command: "list", bridgeCommand: "catalog.editor.run", toolName: "camera.list", buildArgs: ({ flags }) => queryArgs(flags) },
  { group: "camera", command: "info", bridgeCommand: "catalog.editor.run", toolName: "camera.info", buildArgs: ({ flags, positionalTarget }) => compactArgs({ target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") }) },
  { group: "screenshot", command: "from-camera", bridgeCommand: "catalog.editor.run", toolName: "screenshot.from-camera", buildArgs: cameraScreenshotArgs, destructiveHint: true },
  { group: "camera", command: "create", bridgeCommand: "catalog.editor.run", toolName: "camera.create", buildArgs: cameraCreateArgs },
  { group: "camera", command: "target", bridgeCommand: "catalog.editor.run", toolName: "camera.target", buildArgs: cameraTargetArgs },
  { group: "camera", command: "lens", bridgeCommand: "catalog.editor.run", toolName: "camera.lens", buildArgs: cameraLensArgs },
  { group: "camera", command: "multiview", bridgeCommand: "catalog.editor.run", toolName: "camera.multiview", buildArgs: cameraMultiviewArgs, destructiveHint: true }
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

function cameraScreenshotArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? structured.target,
    path: stringFlag(flags, "path") ?? stringFlag(flags, "output") ?? structured.path ?? structured.output,
    output: structured.output,
    width: numberFlag(flags, "width") ?? structured.width,
    height: numberFlag(flags, "height") ?? structured.height,
    format: stringFlag(flags, "format") ?? structured.format,
    transparent: boolFlag(flags, "transparent") ?? structured.transparent,
    quality: numberFlag(flags, "quality") ?? structured.quality,
    overwrite: boolFlag(flags, "overwrite") ?? structured.overwrite,
    inlineBase64: boolFlag(flags, "inline-base64") ?? structured.inlineBase64,
    limits: structured.limits,
    budget: structured.budget
  });
}

function cameraMultiviewArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    targets: stringFlag(flags, "targets") ?? structured.targets,
    output: stringFlag(flags, "output") ?? structured.output,
    width: numberFlag(flags, "width") ?? structured.width,
    height: numberFlag(flags, "height") ?? structured.height,
    format: stringFlag(flags, "format") ?? structured.format,
    limits: structured.limits,
    budget: structured.budget
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
