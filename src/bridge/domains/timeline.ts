import { boolFlag, compactArgs, floatFlag, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const timelineBridgeSpecs: BridgeCommandSpec[] = [
  { group: "timeline", command: "create", bridgeCommand: "catalog.editor.run", toolName: "timeline.create", buildArgs: timelineCreateArgs, destructiveHint: true, supportsDryRun: true },
  { group: "timeline", command: "info", bridgeCommand: "catalog.editor.run", toolName: "timeline.info", buildArgs: timelineInfoArgs, destructiveHint: false, supportsDryRun: false },
  { group: "timeline", command: "track.add", bridgeCommand: "catalog.editor.run", toolName: "timeline.track.add", buildArgs: timelineTrackAddArgs, destructiveHint: true, supportsDryRun: true },
  { group: "timeline", command: "clip.add", bridgeCommand: "catalog.editor.run", toolName: "timeline.clip.add", buildArgs: timelineClipAddArgs, destructiveHint: true, supportsDryRun: true }
];

// Timeline create 保留 typed business fields；package availability 由 live descriptor 统一裁决。
function timelineCreateArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringValue(structured.path),
    frameRate: floatFlag(flags, "frame-rate") ?? numberValue(structured.frameRate),
    overwrite: boolFlag(flags, "overwrite") ?? boolValue(structured.overwrite)
  });
}

// Timeline info 只转发 bounded read controls，不在 Bridge 层扩展 ObjectRef 或 scene binding 语义。
function timelineInfoArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    timeline: positionalTarget ?? stringFlag(flags, "timeline") ?? stringValue(structured.timeline),
    cursor: numberFlag(flags, "cursor") ?? intValue(structured.cursor),
    pageSize: numberFlag(flags, "page-size") ?? intValue(structured.pageSize),
    maxDepth: numberFlag(flags, "max-depth") ?? intValue(structured.maxDepth),
    maxClipsPerTrack: numberFlag(flags, "max-clips-per-track") ?? intValue(structured.maxClipsPerTrack)
  });
}

// Track mutation 保留 stable localId selector，避免 Bridge 退化为名称猜测。
function timelineTrackAddArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    timeline: positionalTarget ?? stringFlag(flags, "timeline") ?? stringValue(structured.timeline),
    trackType: stringFlag(flags, "track-type") ?? stringValue(structured.trackType),
    name: stringFlag(flags, "name") ?? stringValue(structured.name),
    parentTrack: stringFlag(flags, "parent-track") ?? stringValue(structured.parentTrack),
    parentLocalId: stringFlag(flags, "parent-local-id") ?? stringValue(structured.parentLocalId)
  });
}

// Clip mutation 只转发 timing、source asset 和唯一 track selector。
function timelineClipAddArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    timeline: positionalTarget ?? stringFlag(flags, "timeline") ?? stringValue(structured.timeline),
    track: stringFlag(flags, "track") ?? stringValue(structured.track),
    trackLocalId: stringFlag(flags, "track-local-id") ?? stringValue(structured.trackLocalId),
    start: floatFlag(flags, "start") ?? numberValue(structured.start),
    duration: floatFlag(flags, "duration") ?? numberValue(structured.duration),
    asset: stringFlag(flags, "asset") ?? stringValue(structured.asset)
  });
}

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

function stringValue(value: unknown): string | undefined {
  return typeof value === "string" ? value : undefined;
}

function numberValue(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function intValue(value: unknown): number | undefined {
  return typeof value === "number" && Number.isInteger(value) ? value : undefined;
}

function boolValue(value: unknown): boolean | undefined {
  return typeof value === "boolean" ? value : undefined;
}
