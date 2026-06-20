/**
 * Bridge domain specs for extension commands.
 *
 * P1 baseline: extension.status is a read-only diagnostic command.
 * install/remove are reserved for P5 and not exposed as bridge commands.
 */

import type { BridgeCommandSpec } from "../types.js";

export const extensionBridgeSpecs: BridgeCommandSpec[] = [
  {
    group: "extension",
    command: "status",
    bridgeCommand: "extension.status",
    buildArgs: (ctx) => ({
      extensionId: ctx.flags["id"] ?? ctx.flags["name"] ?? ctx.positionalTarget
    }),
    destructiveHint: false,
    internal: true
  }
];
