/**
 * CLI sub-commands: context list, context describe, context read.
 *
 * These commands expose built-in and project custom context descriptors
 * so that AI agents and users can discover, describe and read contexts.
 *
 * Key invariants:
 * - Custom contexts are always source="project", kind="context".
 * - Built-in contexts are always source="built-in".
 * - Project contexts cannot silently override built-in contexts with the same name.
 * - Contexts never enter the `tools` execution path.
 */

import { getBoolean, getString } from "../args.js";
import {
  shouldDiscoverContexts,
  discoverProjectContexts,
  rawDescriptorToContextDescriptor,
  type ContextDescriptor,
  type ProjectContextRuntime
} from "../project-context-discovery.js";
import { executeBridgeCommand } from "../bridge/index.js";
import { failure, success } from "../output.js";
import type { CliResult } from "../types.js";

export type ContextRuntime = ProjectContextRuntime;

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
    "Run unity_cli context list to see available contexts.",
    "Run unity_cli context describe <name> for full context metadata.",
    "Run unity_cli context read <name> to read a context value."
  ]);
}

async function handleContextList(
  flags: Record<string, string | boolean>,
  runtime: ContextRuntime,
  requestId: string
): Promise<CliResult> {
  const explicitIncludeCustom = getBoolean(flags, "include-custom");
  const includeCustom = explicitIncludeCustom || shouldDiscoverContexts(flags, runtime);

  let contexts: ContextDescriptor[] = [];
  let mode = "offline" as string;
  let builtInCount = 0;
  let projectCustomCount = 0;

  if (includeCustom) {
    const discovery = await discoverProjectContexts(flags, runtime, requestId, { requireBridge: explicitIncludeCustom });
    if (discovery.ok === false) return discovery;
    contexts = discovery.contexts;
    mode = discovery.mode;
    builtInCount = discovery.builtInCount;
    projectCustomCount = discovery.projectCustomCount;
  }

  const summaries = contexts.map((c) => ({
    name: c.name,
    title: c.title,
    description: c.description,
    category: c.category,
    kind: c.kind,
    source: c.source,
    requiresUnity: c.requiresUnity,
    target: c.target,
    availabilityScope: c.availabilityScope,
    playModeSupport: c.playModeSupport,
    equivalentUnityMcp: c.equivalentUnityMcp,
    equivalentUnityMcpNote: c.equivalentUnityMcpNote,
    cliCommand: c.cliCommand,
    fingerprint: c.fingerprint ? { value: c.fingerprint.value, status: c.fingerprint.status } : undefined
  }));

  return success(requestId, {
    command: "context.list",
    mode,
    count: summaries.length,
    builtInCount,
    projectCustomCount,
    contexts: summaries
  });
}

async function handleContextDescribe(
  contextName: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: ContextRuntime,
  requestId: string
): Promise<CliResult> {
  if (!contextName) {
    return failure(requestId, "E_CONTEXT_NAME_REQUIRED", "Pass a context name: unity_cli context describe <name>.", [
      "Run unity_cli context list to see available contexts."
    ]);
  }

  // Try discovering contexts to find the one by name
  const includeCustom = shouldDiscoverContexts(flags, runtime) || getBoolean(flags, "include-custom");
  let context: ContextDescriptor | undefined;

  if (includeCustom) {
    const discovery = await discoverProjectContexts(flags, runtime, requestId);
    if (discovery.ok === false) return discovery;
    context = discovery.contexts.find((c) => c.name === contextName);
  }

  if (!context) {
    // Fall back to bridge context.describe for direct lookup
    const bridgeResult = await executeBridgeCommand(requestId, "context.describe", flags, contextName, runtime);
    if (bridgeResult.ok) {
      // Enrich the bridge response with TS-side metadata
      const data = bridgeResult.data as Record<string, unknown>;
      const descriptor = rawDescriptorToContextDescriptor(data);
      if (descriptor) {
        const { requiresUnity: _, ...rest } = descriptor;
        return success(requestId, {
          command: "context.describe",
          mode: descriptor.source === "project" ? "bridge" : "offline",
          requiresUnity: descriptor.requiresUnity,
          ...rest
        });
      }
    }
    return bridgeResult;
  }

  const { requiresUnity: _, ...contextRest } = context;
  return success(requestId, {
    command: "context.describe",
    mode: context.source === "project" ? "bridge" : "offline",
    requiresUnity: context.requiresUnity,
    ...contextRest
  });
}

async function handleContextRead(
  contextName: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: ContextRuntime,
  requestId: string
): Promise<CliResult> {
  if (!contextName) {
    return failure(requestId, "E_CONTEXT_NAME_REQUIRED", "Pass a context name: unity_cli context read <name>.", [
      "Run unity_cli context list to see available contexts."
    ]);
  }

  // Delegate to bridge context.read
  return executeBridgeCommand(requestId, "context.read", flags, contextName, runtime);
}
