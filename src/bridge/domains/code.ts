import { boolFlag, compactArgs, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const codeBridgeSpecs: BridgeCommandSpec[] = [
  { group: "code", command: "execute", bridgeCommand: "code.execute", buildArgs: codeArgs, destructiveHint: true, supportsDryRun: true }
];

function codeArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    sourcePath: stringFlag(flags, "file"),
    sourceText: stringFlag(flags, "code"),
    sourceKind: stringFlag(flags, "file") ? "file" : stringFlag(flags, "code") ? "inline" : boolFlag(flags, "stdin") ? "stdin" : undefined,
    timeoutMs: numberFlag(flags, "timeout-ms"),
    artifactDir: stringFlag(flags, "artifact-dir"),
    argsJson: stringFlag(flags, "args"),
    refs: stringFlag(flags, "refs"),
    defines: stringFlag(flags, "define")
  });
}
