import { boolFlag, compactArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const packageBridgeSpecs: BridgeCommandSpec[] = [
  { group: "package", command: "embed", bridgeCommand: "package.embed", buildArgs: packageEmbedArgs, supportsDryRun: true }
];

function packageEmbedArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    package: positionalTarget ?? stringFlag(flags, "package") ?? stringFlag(flags, "package-id") ?? stringFlag(flags, "id"),
    dryRun: boolFlag(flags, "dry-run")
  });
}
