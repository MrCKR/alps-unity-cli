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
import { readInstances } from "../instances.js";
import { builtinToolGroups, builtinToolMetadata, extensionToolMetadata } from "../tools/metadata/index.js";
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
import { checkContextExtensionAvailability } from "../extension-availability.js";

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

// ── Shared context resolution ───────────────────────────────────────────

/** Resolve a context descriptor by name: built-in first, then custom via bridge. */
async function resolveContextDescriptor(
  contextName: string,
  flags: Record<string, string | boolean>,
  runtime: ContextRuntime,
  requestId: string
): Promise<{ found: true; descriptor: ContextDescriptor } | { found: false; includeCustom: boolean }> {
  const builtin = findBuiltinContextByName(contextName);
  if (builtin) {
    return { found: true, descriptor: builtin };
  }

  const includeCustom = shouldDiscoverProjectTools(flags, runtime) || getBoolean(flags, "include-custom");
  if (includeCustom) {
    const customDescs = await discoverCustomContexts(runtime, requestId);
    const custom = customDescs.find((c) => c.name === contextName);
    if (custom) {
      return { found: true, descriptor: customContextToDescriptor(custom) };
    }
  }

  return { found: false, includeCustom };
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

  const resolved = await resolveContextDescriptor(contextName, flags, runtime, requestId);
  if (resolved.found) {
    const availability = await checkContextExtensionAvailability(resolved.descriptor.name, flags);
    const data: Record<string, unknown> = {
      command: "context.describe",
      ...resolved.descriptor,
      supportStatusLabel: "已路由可执行",
    };
    if (!availability.available) {
      data.extensionAvailability = "unavailable";
      data.extensionId = availability.extensionId;
      data.missingPackageIds = availability.availabilityResult?.missingPackageIds ?? [];
      data.nextActions = availability.nextActions;
    } else if (availability.extensionId) {
      data.extensionAvailability = availability.availabilityResult?.status ?? "available";
      data.extensionId = availability.extensionId;
    }
    return success(requestId, data);
  }

  const nextActions = ["Run unity_cli context list to see all available contexts."];
  if (resolved.includeCustom) {
    nextActions.push("Run unity_cli context list --include-custom to discover project custom contexts.");
  } else {
    nextActions.push("Use --include-custom to discover project custom contexts.");
  }
  nextActions.push("Run unity_cli context list --category <category> to filter by category.");
  return failure(requestId, "E_CONTEXT_NOT_FOUND", `Context not found: ${contextName}.`, nextActions);
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
  const resolved = await resolveContextDescriptor(contextName, flags, runtime, requestId);
  if (!resolved.found) {
    const nextActions = ["Run unity_cli context list to see all available contexts."];
    if (resolved.includeCustom) {
      nextActions.push("Run unity_cli context list --include-custom to discover project custom contexts.");
    } else {
      nextActions.push("Use --include-custom to discover project custom contexts.");
    }
    return failure(requestId, "E_CONTEXT_NOT_FOUND", `Context not found: ${contextName}.`, nextActions);
  }

  const availability = await checkContextExtensionAvailability(resolved.descriptor.name, flags);
  if (!availability.available) {
    return failure(requestId, availability.errorCode, availability.message, availability.nextActions, {
      extensionId: availability.extensionId,
      missingPackageIds: availability.availabilityResult?.missingPackageIds ?? []
    });
  }

  const inputArgs = parseContextInput(flags);

  if (contextName === "instances.list") {
    const instances = await readInstances(runtime.dataDir ?? process.cwd());
    return success(requestId, {
      command: "context.read",
      context: contextName,
      instances,
      count: instances.length,
      readOnlyHint: true,
    });
  }

  if (contextName === "tools.groups") {
    const tools = [...builtinToolMetadata(), ...extensionToolMetadata()];
    const counts = new Map<string, number>();
    for (const tool of tools) {
      counts.set(tool.group, (counts.get(tool.group) ?? 0) + 1);
    }
    const groups = [...new Set([...builtinToolGroups(), ...extensionToolMetadata().map((tool) => tool.group)])]
      .sort()
      .map((group) => ({ name: group, toolCount: counts.get(group) ?? 0, cliCommand: `tools group list ${group}` }));
    return success(requestId, {
      command: "context.read",
      context: contextName,
      groups,
      count: groups.length,
      readOnlyHint: true,
    });
  }

  return executeBridgeCommand(
    requestId,
    "context.read",
    { ...flags, name: contextName, input: JSON.stringify(inputArgs), ...inputArgs },
    undefined,
    runtime
  );
}

/** 解析 context read 的 JSON 输入，非法输入按空对象处理，保持现有容错语义。 */
function parseContextInput(flags: Record<string, string | boolean>): Record<string, unknown> {
  const inputString = getString(flags, "input");
  if (!inputString) {
    return {};
  }
  try {
    const parsed = JSON.parse(inputString);
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed as Record<string, unknown> : {};
  } catch {
    return {};
  }
}
