import { compactArgs, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const runtimeToolBridgeSpecs: BridgeCommandSpec[] = [
  { group: "runtime-tool", command: "list", bridgeCommand: "runtime-tool.list", buildArgs: runtimeToolListArgs },
  { group: "runtime-tool", command: "run", bridgeCommand: "runtime-tool.run", buildArgs: runtimeToolRunArgs, destructiveHint: true }
];

function runtimeToolListArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({});
}

function runtimeToolRunArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    name: positionalTarget ?? stringFlag(flags, "name"),
    input: stringFlag(flags, "input"),
    fingerprintMode: stringFlag(flags, "fingerprint-mode"),
    playModeSessionId: stringFlag(flags, "play-mode-session-id"),
    timeoutMs: numberFlag(flags, "timeout-ms"),
    artifactDir: stringFlag(flags, "artifact-dir")
  });
}