import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { getBoolean, getString } from "../args.js";
import { executeBridgePayload, type BridgeRuntime } from "../bridge/index.js";
import { checkContextExtensionAvailability, extensionAvailabilityFlags } from "../extension-availability.js";
import { failure, success } from "../output.js";
import type { ProjectToolRuntime } from "../project-tool-discovery.js";
import { validateSchema } from "../schema/validator.js";
import type { CliResult } from "../types.js";
import { loadContextCatalog, resolveCatalogContext, searchContexts, toContextListEntry, type ContextCatalogRuntime } from "./catalog.js";
import { findDefineContext, StructuredContextError, type ContextReadContext } from "./define.js";
import type { ContextDescriptor } from "./types.js";

export type ContextRuntime = BridgeRuntime & ProjectToolRuntime & ContextCatalogRuntime & { dataDir?: string };

type ParsedInput = { ok: true; input: Record<string, unknown> } | { ok: false; error: CliResult };
/** Context 命令统一从同一 catalog view 解析，read 再执行 schema、availability 与目标选择。 */
export async function dispatchContext(
  operation: "list" | "search" | "describe" | "read",
  name: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: ContextRuntime,
  requestId: string
): Promise<CliResult> {
  // This starts the editor.state observation wall clock before any dispatcher work.
  const executionStartedAt = Date.now();
  if (operation === "list" || operation === "search") {
    const catalog = await loadContextCatalog(flags, runtime, requestId);
    if (catalog.ok === false) return catalog;
    let contexts = catalog.contexts;
    if (operation === "search") {
      if (!name) return failure(requestId, "E_CONTEXT_SEARCH_QUERY_REQUIRED", "Pass a keyword: unity_cli context search <keyword>.", ["Run unity_cli context list to inspect the current catalog."]);
      contexts = searchContexts(name, contexts);
    }
    const category = getString(flags, "category");
    if (category) contexts = contexts.filter((context) => context.category === category);
    const availabilityFlags = await contextAvailabilityFlags(flags, runtime);
    const decorated = await Promise.all(contexts.map((context) => decorateAvailability(context, availabilityFlags)));
    const entries = decorated.map(toContextListEntry);
    const data: Record<string, unknown> = {
      ...(operation === "search" ? { query: name } : {}),
      count: entries.length,
      categories: [...new Set(catalog.contexts.map((context) => context.category))].sort(),
      contexts: entries
    };
    if (getString(flags, "format") === "text") data.text = entries.map((entry) => `${entry.name} [${entry.category}] ${entry.source} ${entry.target} - ${entry.description}`).join("\n");
    return success(requestId, data);
  }

  if (!name) {
    const describe = operation === "describe";
    return failure(requestId, describe ? "CONTEXT_DESCRIBE_NAME_REQUIRED" : "E_CONTEXT_NAME_REQUIRED", `Pass a context name: unity_cli context ${operation} <name>.`, ["Run unity_cli context list to see available contexts."]);
  }
  const resolved = await resolveCatalogContext(name, flags, runtime, requestId);
  if (resolved.ok === false) return resolved;
  if (!resolved.context) return failure(requestId, "E_CONTEXT_NOT_FOUND", `Context not found: ${name}.`, ["Run unity_cli context list to inspect the current catalog.", "Connect a Unity Bridge target to discover live contexts."]);
  // editor.state is a TS-local, always-available observation owner. Do not resolve a
  // project for extension availability before it begins its bounded observation.
  const isEditorState = resolved.context.name === "editor.state";
  const availabilityFlags = isEditorState ? flags : await contextAvailabilityFlags(flags, runtime);
  const descriptor = isEditorState ? resolved.context : await decorateAvailability(resolved.context, availabilityFlags);

  if (operation === "describe") {
    return success(requestId, publicContextDescriptor(descriptor));
  }

  if (!isEditorState && descriptor.available === false && descriptor.source !== "extension") {
    return failure(requestId, "E_CONTEXT_UNAVAILABLE", `Context is not available for the selected target: ${descriptor.name}.`, [
      `Run unity_cli context describe ${descriptor.name} to inspect target, availabilityScope, and playModeSupport.`,
      "Select a compatible Unity target or Editor/Play Mode state and retry."
    ], { target: descriptor.target, availabilityScope: descriptor.availabilityScope, playModeSupport: descriptor.playModeSupport });
  }
  if (!isEditorState) {
    const availability = await checkContextExtensionAvailability(descriptor.name, availabilityFlags);
    if (!availability.available) {
      return failure(requestId, availability.errorCode, availability.message, availability.nextActions, {
        extensionId: availability.extensionId,
        missingPackageIds: availability.availabilityResult?.missingPackageIds ?? descriptor.requiresPackages ?? []
      });
    }
  }
  const parsed = await parseContextInput(flags, requestId);
  if (!parsed.ok) return parsed.error;
  const issues = validateSchema(descriptor.inputSchema, parsed.input);
  if (issues.length > 0) {
    return failure(requestId, "E_SCHEMA_INVALID", `Input schema validation failed at ${issues[0].path}.`, [`Run unity_cli context describe ${descriptor.name} to inspect inputSchema.`], {
      path: issues[0].path,
      expected: issues[0].expected,
      actual: issues[0].actual,
      errors: issues
    });
  }

  const definition = findDefineContext(descriptor.name);
  if (definition) return readLocalContext(descriptor, definition, parsed.input, flags, runtime, requestId, executionStartedAt);
  return executeBridgePayload(
    requestId,
    "context.read",
    {
      name: descriptor.name,
      input: parsed.input,
      ...(descriptor.descriptorFingerprint ? { expectedDescriptorFingerprint: descriptor.descriptorFingerprint } : {}),
    },
    flags,
    runtime,
    undefined,
    { mode: descriptor.name === "editor.state" ? "observeNow" : "read", mutation: false },
  );
}

/** physical gate 优先使用显式 project，否则从当前 target/active instance 推断项目。 */
async function contextAvailabilityFlags(flags: Record<string, string | boolean>, runtime: ContextRuntime): Promise<Record<string, string | boolean>> {
  return extensionAvailabilityFlags(flags, runtime);
}

async function decorateAvailability(context: ContextDescriptor, flags: Record<string, string | boolean>): Promise<ContextDescriptor> {
  const availability = await checkContextExtensionAvailability(context.name, flags);
  if (!availability.extensionId) return context;
  return {
    ...context,
    extensionId: availability.extensionId,
    available: availability.available,
    requiresPackages: context.requiresPackages ?? availability.availabilityResult?.packages.map((pkg) => pkg.packageId)
  };
}

async function readLocalContext(
  descriptor: ContextDescriptor,
  definition: NonNullable<ReturnType<typeof findDefineContext>>,
  input: Record<string, unknown>,
  flags: Record<string, string | boolean>,
  runtime: ContextRuntime,
  requestId: string,
  startedAt: number,
): Promise<CliResult> {
  try {
    const data = await definition.read(input, {
      requestId,
      startedAt,
      flags,
      bridgeExecute: runtime.bridgeExecute,
      dataDir: runtime.dataDir,
      projectTools: runtime.projectTools
    } as ContextReadContext);
    const issues = validateSchema(descriptor.outputSchema, data);
    if (issues.length > 0) {
      return failure(requestId, "E_CONTEXT_OUTPUT_SCHEMA_INVALID", `Context ${descriptor.name} returned data that violates its output schema.`, ["Inspect the Context implementation and its generated output schema."], { issues });
    }
    return success(requestId, data);
  } catch (error) {
    if (error instanceof StructuredContextError) return failure(requestId, error.code, error.message, error.nextActions, error.details);
    return failure(requestId, "CONTEXT_READ_FAILED", error instanceof Error ? error.message : String(error), [`Run unity_cli context describe ${descriptor.name} for input requirements and availability.`]);
  }
}

async function parseContextInput(flags: Record<string, string | boolean>, requestId: string): Promise<ParsedInput> {
  const inputFile = getString(flags, "input-file");
  if (inputFile) {
    try {
      return parseInputText(await readFile(resolve(inputFile), "utf8"), "--input-file", requestId);
    } catch (error) {
      return { ok: false, error: failure(requestId, "E_CONTEXT_INPUT_FILE_READ_FAILED", `Failed to read --input-file: ${error instanceof Error ? error.message : String(error)}`) };
    }
  }
  if (getBoolean(flags, "stdin") && !process.stdin.isTTY) {
    let text = "";
    process.stdin.setEncoding("utf8");
    for await (const chunk of process.stdin) text += chunk;
    if (text.trim()) return parseInputText(text, "--stdin", requestId);
  }
  const input = getString(flags, "input");
  return input === undefined ? { ok: true, input: {} } : parseInputText(input, "--input", requestId);
}

function parseInputText(text: string, source: string, requestId: string): ParsedInput {
  try {
    const value = JSON.parse(text);
    if (!value || typeof value !== "object" || Array.isArray(value)) {
      return { ok: false, error: failure(requestId, "E_SCHEMA_INVALID", `${source} must contain a JSON object.`, ["Pass an object matching the context inputSchema."], {
        path: "$",
        expected: "object",
        actual: Array.isArray(value) ? "array" : value === null ? "null" : typeof value
      }) };
    }
    return { ok: true, input: value as Record<string, unknown> };
  } catch (error) {
    return { ok: false, error: failure(requestId, "E_CONTEXT_INPUT_JSON_INVALID", `${source} must be valid JSON. ${error instanceof Error ? error.message : String(error)}`, ["In PowerShell use single quotes around JSON, or use --input-file for complex input."]) };
  }
}

/** 公开 Context descriptor 不得泄露内部 routing、fingerprint 或 authoring input。 */
export function publicContextDescriptor(descriptor: ContextDescriptor): Record<string, unknown> {
  const { name, summary, description, category, source, target, availabilityScope, playModeSupport, inputSchema, outputSchema, available, unavailableReason, requiresPackages, extensionId } = descriptor;
  return {
    name, summary, description, category, source, target, availabilityScope, playModeSupport, inputSchema, outputSchema,
    examples: descriptor.examples.map(({ description: exampleDescription, argv }) => ({ description: exampleDescription, argv })),
    available: available ?? true,
    ...(unavailableReason ? { unavailableReason } : {}),
    ...(requiresPackages?.length ? { requiresPackages } : {}),
    ...(extensionId ? { extensionId } : {})
  };
}

export { validateSchema as validateContextSchema } from "../schema/validator.js";
