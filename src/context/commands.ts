/**
 * CLI sub-commands: context list, context describe, context read.
 *
 * These commands expose read-only context data so that agents and users can
 * discover Unity Editor / project state without side effects.
 *
 * Context is the CLI-first equivalent of MCP Resources:
 *   - context list:   discover available contexts
 *   - context describe <name>: inspect a context descriptor
 *   - context read <name>:     read context data via the Unity Bridge
 */

import { getString, getBoolean } from "../args.js";
import { executeBridgeCommand, type BridgeRuntime } from "../bridge/index.js";
import {
  builtinContextDescriptors,
  builtinContextCategories,
  findBuiltinContextByName,
  searchContexts,
  allContextDescriptors,
  discoverCustomContexts,
  customContextToDescriptor,
  toListEntry,
  type ContextBridgeRuntime,
} from "../context/registry.js";
import type { ContextDescriptor } from "../context/types.js";
import { failure, success } from "../output.js";
import { shouldDiscoverProjectTools, type ProjectToolRuntime } from "../project-tool-discovery.js";
import type { CliResult } from "../types.js";

export type ContextRuntime = BridgeRuntime & ProjectToolRuntime;

export async function handleContextCommand(
  command: string | undefined,
  positional: string[],
  flags: Record<string, string | boolean>,
  runtime: ContextRuntime,
  requestId: string
): Promise<CliResult> {
  if (!command || command === "list") {
    return handleContextList(flags, runtime, requestId);
  }

  if (command === "describe") {
    const contextName = positional[0] ?? getString(flags, "name");
    return handleContextDescribe(contextName, flags, runtime, requestId);
  }

  if (command === "read") {
    const contextName = positional[0] ?? getString(flags, "name");
    return handleContextRead(contextName, flags, runtime, requestId);
  }

  return failure(requestId, "UNKNOWN_CONTEXT_COMMAND", "Expected context list, context describe <name>, or context read <name>.", [
    "Run unity_cli context list to see all available contexts.",
    "Run unity_cli context describe <name> for full context metadata.",
    "Run unity_cli context read <name> to read a context value."
  ]);
}

// ── context list ──────────────────────────────────────────────────────────

async function handleContextList(
  flags: Record<string, string | boolean>,
  runtime: ContextRuntime,
  requestId: string
): Promise<CliResult> {
  const includeCustom = getBoolean(flags, "include-custom") || shouldDiscoverProjectTools(flags, runtime);
  const categoryFilter = getString(flags, "category");
  const format = getString(flags, "format");

  const contexts = await allContextDescriptors(flags, runtime, requestId, { includeCustom });
  const filtered = categoryFilter
    ? contexts.filter((c) => c.category === categoryFilter)
    : contexts;

  const entries = filtered.map(toListEntry);

  const data: Record<string, unknown> = {
    command: "context.list",
    count: entries.length,
    categories: [...new Set(contexts.map((c) => c.category))].sort(),
    contexts: entries,
  };

  if (format === "text") {
    const lines = entries.map((e) =>
      `${e.name} [${e.category}] ${e.source} ${e.target} — ${e.title}`
    );
    data.text = lines.join("\n");
  }

  return success(requestId, data);
}

// ── context describe ──────────────────────────────────────────────────────

async function handleContextDescribe(
  contextName: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: ContextRuntime,
  requestId: string
): Promise<CliResult> {
  if (!contextName) {
    return failure(requestId, "CONTEXT_DESCRIBE_NAME_REQUIRED", "Pass a context name: unity_cli context describe <name>.", [
      "Run unity_cli context list to see all available contexts."
    ]);
  }

  // Look up built-in first, then try custom contexts from bridge
  const builtin = findBuiltinContextByName(contextName);
  if (builtin) {
    return success(requestId, {
      command: "context.describe",
      ...builtin,
      supportStatusLabel: "已路由可执行",
    });
  }

  // Try custom context discovery
  const includeCustom = shouldDiscoverProjectTools(flags, runtime) || getBoolean(flags, "include-custom");
  if (includeCustom) {
    const customDescs = await discoverCustomContexts(runtime, requestId);
    const custom = customDescs.find((c) => c.name === contextName);
    if (custom) {
      const descriptor = customContextToDescriptor(custom);
      return success(requestId, {
        command: "context.describe",
        ...descriptor,
        supportStatusLabel: "已路由可执行",
      });
    }
  }

  return failure(requestId, "E_CONTEXT_NOT_FOUND", `Context not found: ${contextName}.`, [
    "Run unity_cli context list to see all available contexts.",
    "Run unity_cli context list --include-custom to discover project custom contexts.",
    "Run unity_cli context list --category <category> to filter by category."
  ]);
}

// ── context read ──────────────────────────────────────────────────────────

async function handleContextRead(
  contextName: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: ContextRuntime,
  requestId: string
): Promise<CliResult> {
  if (!contextName) {
    return failure(requestId, "E_CONTEXT_NAME_REQUIRED", "Pass a context name: unity_cli context read <name>.", [
      "Run unity_cli context list to see all available contexts.",
      "Run unity_cli context describe <name> for context metadata."
    ]);
  }

  // Verify the context exists (built-in or custom)
  const builtin = findBuiltinContextByName(contextName);
  if (!builtin) {
    const includeCustom = shouldDiscoverProjectTools(flags, runtime) || getBoolean(flags, "include-custom");
    if (includeCustom) {
      const customDescs = await discoverCustomContexts(runtime, requestId);
      const custom = customDescs.find((c) => c.name === contextName);
      if (!custom) {
        return failure(requestId, "E_CONTEXT_NOT_FOUND", `Context not found: ${contextName}.`, [
          "Run unity_cli context list to see all available contexts."
        ]);
      }
    } else {
      return failure(requestId, "E_CONTEXT_NOT_FOUND", `Context not found: ${contextName}.`, [
        "Run unity_cli context list to see all available contexts.",
        "Use --include-custom to discover project custom contexts."
      ]);
    }
  }

  // Route through the bridge: context.read with the context name as argument
  // The Unity Bridge handler dispatches to the appropriate C# context reader
  const inputString = getString(flags, "input");
  let inputArgs: Record<string, unknown> = {};
  if (inputString) {
    try {
      const parsed = JSON.parse(inputString);
      if (typeof parsed === "object" && parsed !== null && !Array.isArray(parsed)) {
        inputArgs = parsed as Record<string, unknown>;
      }
    } catch {
      // Invalid JSON input — proceed with empty args
    }
  }

  return executeBridgeCommand(
    requestId,
    "context.read",
    { ...flags, name: contextName, ...inputArgs },
    undefined,
    runtime
  );
}
