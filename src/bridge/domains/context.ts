/**
 * Bridge domain specs for context commands.
 *
 * Context commands are read-only and allowed in Play Mode.
 * Internal commands for bridge protocol only; the CLI routes
 * through the context.ts command handler, not directly through these.
 */

import type { BridgeCommandSpec } from "../types.js";

export const contextBridgeSpecs: BridgeCommandSpec[] = [
  {
    group: "context",
    command: "list",
    bridgeCommand: "context.list",
    buildArgs: () => ({}),
    destructiveHint: false,
    internal: true
  },
  {
    group: "context",
    command: "describe",
    bridgeCommand: "context.describe",
    buildArgs: (ctx) => ({ contextName: ctx.flags["name"] ?? ctx.positionalTarget }),
    destructiveHint: false,
    internal: true
  },
  {
    group: "context",
    command: "read",
    bridgeCommand: "context.read",
    buildArgs: (ctx) => {
      const input: Record<string, unknown> = { contextName: ctx.flags["name"] ?? ctx.positionalTarget };
      const inputJson = typeof ctx.flags["input"] === "string" ? ctx.flags["input"] : undefined;
      if (inputJson) {
        try {
          const parsed = JSON.parse(inputJson);
          if (typeof parsed === "object" && parsed !== null) {
            Object.assign(input, parsed);
          }
        } catch { /* ignore invalid JSON */ }
      }
      return input;
    },
    destructiveHint: false,
    internal: true
  }
];
