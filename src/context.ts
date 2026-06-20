/**
 * CLI command: context list / context describe / context read
 *
 * Context provides read-only observation of Unity project and editor state.
 * Context never modifies Unity state; if a capability has side effects
 * it MUST be a Tool, not a Context.
 *
 * New P1 command tree:
 *   context list [--category <cat>] [--format json|text]
 *   context describe <name>
 *   context read <name> [--input <json>]
 */

import { getBoolean, getString } from "./args.js";
import { builtinContextMetadata, builtinContextCategories, findContextByName, filterContextsByCategory, searchContexts, type ContextMetadata } from "./context/metadata/index.js";
import { executeBridgePayload, type BridgeRuntime } from "./bridge/index.js";
import { discoverProjectContexts, shouldDiscoverContexts, type ContextDiscoveryRuntime } from "./context-discovery.js";
import { failure, success } from "./output.js";
import { instanceSelectionFailure, selectInstanceForFlags, targetSummary } from "./instances.js";
import type { CliResult } from "./types.js";

export type ContextRuntime = ContextDiscoveryRuntime;

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
    return handleContextRead(contextName, positional, flags, runtime, requestId);
  }

  return failure(requestId, "UNKNOWN_CONTEXT_COMMAND", "Expected context list, context describe <name>, or context read <name>.", [
    "Run unity_cli context list to see all available contexts.",
    "Run unity_cli context describe <name> for full metadata.",
    "Run unity_cli context read <name> to read a context value."
  ]);
}

async function handleContextList(
  flags: Record<string, string | boolean>,
  runtime: ContextRuntime,
  requestId: string
): Promise<CliResult> {
  const categoryFilter = getString(flags, "category");
  const includeCustom = shouldDiscoverContexts(flags, runtime);

  let contexts: ContextMetadata[] = categoryFilter ? filterContextsByCategory(categoryFilter) : builtinContextMetadata();

  if (includeCustom) {
    const customContexts = await discoverProjectContexts(flags, runtime, requestId);
    if (customContexts.ok === false) return customContexts;
    contexts = [...contexts, ...(categoryFilter ? customContexts.contexts.filter((c: ContextMetadata) => c.category === categoryFilter) : customContexts.contexts)];
  }

  const summaries = contexts.map((c) => ({
    name: c.name,
    title: c.title,
    description: c.description,
    category: c.category,
    source: c.source,
    readOnlyHint: c.readOnlyHint,
    refreshBehavior: c.refreshBehavior,
    target: c.target ?? "editor",
    availabilityScope: c.availabilityScope ?? "editorSession",
    available: c.available,
    requiresPackages: c.requiresPackages,
    equivalentUnityMcp: c.equivalentUnityMcp,
    equivalentUnityMcpNote: c.equivalentUnityMcpNote,
    cliCommand: c.cliCommand
  }));

  const data: Record<string, unknown> = {
    command: "context.list",
    target: await optionalTargetSummary(flags, runtime),
    count: summaries.length,
    contexts: summaries
  };

  const format = getString(flags, "format");
  if (format === "text") {
    const lines = summaries.map((s) => `${s.name} [${s.category}] ${s.source} ${s.refreshBehavior ?? ""} — ${s.title}`);
    data.text = lines.join("\n");
  }

  return success(requestId, data);
}

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

  const builtinContext = findContextByName(contextName);
  const context = builtinContext ?? await findProjectContextByName(contextName, flags, runtime, requestId);

  if (!context) {
    return failure(requestId, "E_CONTEXT_NOT_FOUND", `Context not found: ${contextName}.`, [
      "Run unity_cli context list to see all available contexts.",
      "Run unity_cli context list --include-custom to discover project custom contexts."
    ]);
  }

  const isProjectContext = builtinContext === undefined;
  // Derive mode/requiresUnity from the handler, not from builtin vs project.
  // Built-in contexts use handler "bridge" and require Unity connection;
  // only truly offline handlers (e.g. future offline context) require no Unity.
  const requiresUnity = context.handler !== "offline";
  const mode = requiresUnity ? "bridge" : "offline";
  const data: Record<string, unknown> = {
    command: "context.describe",
    target: await optionalTargetSummary(flags, runtime),
    mode,
    requiresUnity,
    ...context
  };

  return success(requestId, data);
}

async function handleContextRead(
  contextName: string | undefined,
  positional: string[],
  flags: Record<string, string | boolean>,
  runtime: ContextRuntime,
  requestId: string
): Promise<CliResult> {
  if (!contextName) {
    return failure(requestId, "E_CONTEXT_NAME_REQUIRED", "Context name is required: unity_cli context read <name>.", [
      "Run unity_cli context list to see all available contexts."
    ]);
  }

  // Validate context exists in metadata
  const builtinContext = findContextByName(contextName);
  const includeCustom = shouldDiscoverContexts(flags, runtime);
  let projectContext: ContextMetadata | undefined;

  if (!builtinContext && includeCustom) {
    const customContexts = await discoverProjectContexts(flags, runtime, requestId);
    if (customContexts.ok === false) return customContexts;
    projectContext = customContexts.contexts.find((c: ContextMetadata) => c.name === contextName);
  }

  const context = builtinContext ?? projectContext;
  if (!context) {
    return failure(requestId, "E_CONTEXT_NOT_FOUND", `Context not found: ${contextName}.`, [
      "Run unity_cli context list to see all available contexts."
    ]);
  }

  // Enforce read-only: context.read must never mutate
  // (enforced at type level by readOnlyHint=true, but belt-and-suspenders check)
  if (context.readOnlyHint !== true) {
    return failure(requestId, "E_CONTEXT_READONLY_VIOLATION", `Context "${contextName}" does not declare readOnlyHint=true. This is a descriptor contract violation.`, [
      "Report this as a bug: context descriptors must always be read-only.",
      "If this capability has side effects, it should be a Tool, not a Context."
    ]);
  }

  // Parse --input for context read
  const input = await parseContextInput(flags);

  // Route to bridge: context.read bridge command
  return executeBridgePayload(requestId, "context.read", { ...input, contextName }, flags, runtime);
}

async function parseContextInput(flags: Record<string, string | boolean>): Promise<Record<string, unknown>> {
  const inputString = getString(flags, "input");
  if (inputString !== undefined) {
    try {
      const parsed = JSON.parse(inputString);
      return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed : { input: parsed };
    } catch {
      return {};
    }
  }
  return {};
}

async function optionalTargetSummary(flags: Record<string, string | boolean>, runtime: ContextRuntime): Promise<Record<string, unknown> | null> {
  try {
    const selection = await selectInstanceForFlags(flags, runtime.dataDir ?? process.cwd());
    return selection ? targetSummary(selection.instance) : null;
  } catch (error) {
    const result = instanceSelectionFailure("context_target_probe", error);
    if (result) {
      return null;
    }
    throw error;
  }
}

async function findProjectContextByName(
  name: string,
  flags: Record<string, string | boolean>,
  runtime: ContextRuntime,
  requestId: string
): Promise<ContextMetadata | undefined> {
  const customContexts = await discoverProjectContexts(flags, runtime, requestId);
  if (customContexts.ok === false) {
    return undefined;
  }
  return customContexts.contexts.find((c: ContextMetadata) => c.name === name);
}
