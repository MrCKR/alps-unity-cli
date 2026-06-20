/**
 * Bridge domain for context commands.
 *
 * Registers two internal bridge commands:
 *   - context.list:  Returns custom context descriptors from Unity (internal)
 *   - context.read:  Reads a context value by name (external, CLI-accessible)
 *
 * The context.list command is internal — only used by the CLI to discover
 * custom contexts at runtime. It is not exposed as a CLI sub-command.
 *
 * The context.read command is the external entry point for `context read <name>`.
 * It dispatches to the Unity Bridge handler which routes to the appropriate
 * C# context reader based on the context name.
 */

import { compactArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const contextBridgeSpecs: BridgeCommandSpec[] = [
  {
    group: "context",
    command: "read",
    bridgeCommand: "context.read",
    buildArgs: contextReadArgs,
  },
  {
    group: "context",
    command: "list",
    bridgeCommand: "context.list",
    buildArgs: contextListArgs,
    internal: true,
  },
];

function contextReadArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    name: stringFlag(flags, "name") ?? positionalTarget,
    input: stringFlag(flags, "input"),
  });
}

function contextListArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({});
}
