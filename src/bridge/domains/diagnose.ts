import { compactArgs, stringFlag, targetArgs } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const diagnoseBridgeSpecs: BridgeCommandSpec[] = [
  { group: "preflight", command: "", bridgeCommand: "diagnose.preflight", buildArgs: diagnoseArgs },
  { group: "preflight", command: "run", bridgeCommand: "diagnose.preflight", buildArgs: diagnoseArgs },
  { group: "diagnose", command: "", bridgeCommand: "diagnose.preflight", buildArgs: diagnoseArgs },
  { group: "diagnose", command: "run", bridgeCommand: "diagnose.preflight", buildArgs: diagnoseArgs }
];

function diagnoseArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ ...targetArgs(flags, positionalTarget), path: stringFlag(flags, "path") ?? positionalTarget, command: stringFlag(flags, "command") });
}
