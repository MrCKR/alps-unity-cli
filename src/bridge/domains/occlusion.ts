import type { BridgeCommandSpec } from "../types.js";

export const occlusionBridgeSpecs: BridgeCommandSpec[] = [
  { group: "occlusion", command: "bake-clear", bridgeCommand: "catalog.editor.run", toolName: "occlusion.bake-clear", buildArgs: () => ({}), destructiveHint: true, supportsDryRun: true },
];
