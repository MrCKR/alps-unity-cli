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
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { executeBridgeCommand, type BridgeRuntime } from "../bridge/index.js";
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
      `${e.name} [${e.category}] ${e.source} ${e.target} - ${e.description}`
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
    const customDescs = await discoverCustomContexts(runtime, requestId, flags);
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

  const parsedInput = await parseContextInput(flags, requestId);
  if (!parsedInput.ok) return parsedInput.error!;
  const inputArgs = parsedInput.input;

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

interface ParsedContextInput {
  ok: boolean;
  input: Record<string, unknown>;
  error?: CliResult;
}

/** 解析 context read 输入；非法 JSON 返回结构化错误，避免静默读错上下文。 */
async function parseContextInput(flags: Record<string, string | boolean>, requestId: string): Promise<ParsedContextInput> {
  const inputFilePath = getString(flags, "input-file");
  if (inputFilePath) {
    try {
      const content = await readFile(resolve(inputFilePath), "utf8");
      return parseContextInputString(content, requestId, "--input-file");
    } catch (error) {
      return {
        ok: false,
        input: {},
        error: failure(requestId, "E_CONTEXT_INPUT_FILE_READ_FAILED", `Failed to read --input-file: ${error instanceof Error ? error.message : String(error)}`)
      };
    }
  }

  const inputString = getString(flags, "input");
  if (!inputString) {
    return { ok: true, input: {} };
  }
  return parseContextInputString(inputString, requestId, "--input");
}

function parseContextInputString(inputString: string, requestId: string, source: "--input" | "--input-file"): ParsedContextInput {
  const parsed = parseJsonWithPowerShellFallback(inputString);
  if (parsed.ok) {
    const value = parsed.value;
    return { ok: true, input: typeof value === "object" && value !== null && !Array.isArray(value) ? value as Record<string, unknown> : {} };
  }
  return {
    ok: false,
    input: {},
    error: failure(requestId, "E_CONTEXT_INPUT_JSON_INVALID", `${source} must be valid JSON. ${parsed.message}`, [
      "In PowerShell, use single quotes with unescaped JSON quotes, or pass --input-file <path> for complex JSON.",
      "Do not type backslashes before JSON quotes inside PowerShell single quotes.",
      "Run unity_cli context describe <context> and copy the inputSchema field names exactly."
    ])
  };
}

function parseJsonWithPowerShellFallback(inputString: string): { ok: true; value: unknown } | { ok: false; message: string } {
  try {
    return { ok: true, value: JSON.parse(inputString) };
  } catch (error) {
    const backslashQuoted = recoverBackslashQuotedJson(inputString);
    if (backslashQuoted !== undefined) {
      return { ok: true, value: backslashQuoted };
    }
    const recovered = recoverPowerShellEscapedJsonObject(inputString);
    if (recovered) {
      return { ok: true, value: recovered };
    }
    return { ok: false, message: error instanceof Error ? error.message : String(error) };
  }
}

function recoverBackslashQuotedJson(inputString: string): unknown | undefined {
  const trimmed = inputString.trim();
  if (!/^[{\[]\\"/.test(trimmed)) {
    return undefined;
  }
  try {
    return JSON.parse(trimmed.replace(/\\"/g, '"'));
  } catch {
    return undefined;
  }
}

function recoverPowerShellEscapedJsonObject(inputString: string): Record<string, unknown> | undefined {
  const trimmed = inputString.trim();
  if (!trimmed.startsWith("{\\") || (!trimmed.endsWith("}") && !trimmed.endsWith("\\}"))) {
    return undefined;
  }
  const body = trimmed.replace(/^\{\\\s*/, "").replace(/\\?\}\s*$/, "");
  const result: Record<string, unknown> = {};
  for (const pair of body.split(/\\?,(?=\\?[^\\:]+\\:)/g)) {
    const match = pair.match(/^\s*\\?([^\\:]+)\\:(.*)$/);
    if (!match) return undefined;
    const key = match[1].trim();
    if (!key) return undefined;
    result[key] = parseRecoveredScalar(match[2].replace(/\\$/g, "").trim());
  }
  return result;
}

function parseRecoveredScalar(value: string): unknown {
  if (value === "true") return true;
  if (value === "false") return false;
  if (value === "null") return null;
  if (/^-?\d+(?:\.\d+)?$/.test(value)) return Number(value);
  return value.replace(/^\\/, "").replace(/\\:/g, ":").replace(/\\,/g, ",").replace(/\\\\/g, "\\");
}
