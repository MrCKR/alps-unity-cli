/**
 * Bridge domain specs for extension commands.
 *
 * extension.status is a read-only diagnostic command. install/remove are
 * handled by the CLI command layer and are not exposed as bridge commands.
 */

import type { BridgeCommandSpec } from "../types.js";

export const extensionBridgeSpecs: BridgeCommandSpec[] = [
  {
    group: "extension",
    command: "status",
    bridgeCommand: "project-tool.run",
    toolName: "extension.status",
    buildArgs: (ctx) => ({
      extensionId: ctx.flags["id"] ?? ctx.flags["name"] ?? ctx.positionalTarget
    }),
    destructiveHint: false,
    internal: true
  }
];
