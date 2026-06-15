import { customToolArgs } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const customToolBridgeSpecs: BridgeCommandSpec[] = [
  { group: "custom-tool", command: "list", bridgeCommand: "custom-tool.list", buildArgs: ({ command, flags, positionalTarget }) => customToolArgs(command, flags, positionalTarget) },
  { group: "custom-tool", command: "run", bridgeCommand: "custom-tool.run", buildArgs: ({ command, flags, positionalTarget }) => customToolArgs(command, flags, positionalTarget), supportsJob: true }
];
