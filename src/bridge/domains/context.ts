/**
 * Bridge domain specs for context commands.
 *
 * Context commands are read-only and allowed in Play Mode.
 * Internal commands for bridge protocol only; the CLI routes
 * through the context.ts command handler, not directly through these.
 */

import { compactArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const contextBridgeSpecs: BridgeCommandSpec[] = [
  {
    group: "context",
    command: "list",
    bridgeCommand: "context.list",
    buildArgs: contextListArgs,
    destructiveHint: false,
    internal: true
  },
  {
    group: "context",
    command: "describe",
    bridgeCommand: "context.describe",
    buildArgs: contextDescribeArgs,
    destructiveHint: false,
    internal: true
  },
  {
    group: "context",
    command: "read",
    bridgeCommand: "context.read",
    buildArgs: contextReadArgs,
    destructiveHint: false,
    internal: true
  }
];

function contextListArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({});
}

function contextDescribeArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    name: positionalTarget ?? stringFlag(flags, "name"),
    fingerprintMode: stringFlag(flags, "fingerprint-mode")
  });
}

function contextReadArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  // The Bridge C# side uses RawJsonArg("input") which expects the input to be
  // a raw JSON value (object), not a stringified JSON string.
  let input: unknown;
  const inputString = stringFlag(flags, "input");
  if (inputString !== undefined) {
    try {
      input = JSON.parse(inputString);
    } catch {
      input = inputString;
    }
  }

  const inputObject = input && typeof input === "object" && !Array.isArray(input) ? input as Record<string, unknown> : {};
  return compactArgs({
    ...inputObject,
    name: positionalTarget ?? stringFlag(flags, "name"),
    input,
    fingerprintMode: stringFlag(flags, "fingerprint-mode"),
    projectPath: stringFlag(flags, "project-path")
  });
}
