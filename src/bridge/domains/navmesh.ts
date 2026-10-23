import { boolFlag, compactArgs, floatFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const navMeshBridgeSpecs: BridgeCommandSpec[] = [
  { group: "navmesh", command: "bake-clear", bridgeCommand: "catalog.editor.run", toolName: "navmesh.bake-clear", buildArgs: () => ({}), destructiveHint: true, supportsDryRun: true },
  { group: "navmesh", command: "settings", bridgeCommand: "catalog.editor.run", toolName: "navmesh.settings", buildArgs: () => ({}), destructiveHint: false, supportsDryRun: false },
  { group: "navmesh", command: "settings-set", bridgeCommand: "catalog.editor.run", toolName: "navmesh.settings-set", buildArgs: settingsSetArgs, destructiveHint: true, supportsDryRun: true }
];

// Settings mutation 保留 typed nullable fields；未知键仍由公开 schema 在 Bridge 前拒绝。
function settingsSetArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const input = structuredInput(flags);
  return compactArgs({
    agentRadius: floatFlag(flags, "agent-radius") ?? numberValue(input.agentRadius),
    agentHeight: floatFlag(flags, "agent-height") ?? numberValue(input.agentHeight),
    agentSlope: floatFlag(flags, "agent-slope") ?? numberValue(input.agentSlope),
    agentClimb: floatFlag(flags, "agent-climb") ?? numberValue(input.agentClimb),
    minRegionArea: floatFlag(flags, "min-region-area") ?? numberValue(input.minRegionArea),
    voxelSize: floatFlag(flags, "voxel-size") ?? numberValue(input.voxelSize),
    manualVoxelSize: boolFlag(flags, "manual-voxel-size") ?? boolValue(input.manualVoxelSize)
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
function numberValue(value: unknown): number | undefined { return typeof value === "number" && Number.isFinite(value) ? value : undefined; }
function boolValue(value: unknown): boolean | undefined { return typeof value === "boolean" ? value : undefined; }
