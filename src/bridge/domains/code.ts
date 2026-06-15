import { boolFlag, compactArgs, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const codeBridgeSpecs: BridgeCommandSpec[] = [
  { group: "code", command: "exec", bridgeCommand: "code.exec", buildArgs: codeArgs, supportsJob: true }
];

function codeArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    sourcePath: stringFlag(flags, "file"),
    sourceText: stringFlag(flags, "code"),
    sourceKind: stringFlag(flags, "file") ? "file" : stringFlag(flags, "code") ? "inline" : boolFlag(flags, "stdin") ? "stdin" : undefined,
    channel: stringFlag(flags, "channel"),
    timeoutMs: numberFlag(flags, "timeout-ms"),
    artifactDir: stringFlag(flags, "artifact-dir"),
    argsJson: stringFlag(flags, "args"),
    refs: stringFlag(flags, "refs"),
    defines: stringFlag(flags, "define")
  });
}
