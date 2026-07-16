import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { getBoolean, getString } from "../args.js";
import {
  bridgeCommandRegistry,
  executeBridgeCommand,
  executeBridgePayload,
  startBridgeCommandJob,
  type BridgeJobRuntime,
  type BridgeRuntime,
  type BridgeCommandSpec,
  type BridgeSecurityDescriptor
} from "../bridge/index.js";
import type { BuiltinDocsRuntime } from "../builtin-docs.js";
import type { JobRunner } from "../job-runner.js";
import type { JobStore } from "../job-store.js";
import { failure, success } from "../output.js";
import { PackageManagerError } from "../package-manager.js";
import type { ContextCatalogRuntime } from "../contexts/catalog.js";
import type { ProjectToolRuntime } from "../project-tool-discovery.js";
import type { ContextDescriptor } from "../project-context-discovery.js";
import { confirmDestructiveExecution } from "../security.js";
import { validateSchema } from "../schema/validator.js";
import type { CliResult } from "../types.js";
import { checkToolExtensionAvailability, extensionAvailabilityFlags } from "../extension-availability.js";
import { resolveCatalogTool } from "./catalog.js";
import { findDefineTool, type ToolContext } from "./define.js";
import { StructuredToolError } from "./errors.js";
import { EXTENSION_GROUPS, findToolByName, type ToolMetadata } from "./metadata/index.js";

export type ToolsRuntime = ProjectToolRuntime & ContextCatalogRuntime & Partial<BridgeRuntime> & Partial<BuiltinDocsRuntime> & {
  dataDir?: string;
  store?: JobStore;
  runner?: JobRunner;
};

type ParsedInput = { ok: true; input: Record<string, unknown> } | { ok: false; error: CliResult };

/**
 * Tool 分发顺序固定为输入解析、catalog 解析、schema 校验、安全确认、目标选择、执行与 envelope。
 * destructive proof 只在确认完成后生成，并始终绑定实际 tool name 与 wrapper bridge command。
 */
export async function dispatchTool(
  name: string | undefined,
  positionalTarget: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string,
  seed: Record<string, unknown> = {}
): Promise<CliResult> {
  if (!name) return failure(requestId, "E_TOOL_NOT_FOUND_FOR_TARGET", "Tool name is required for tools run.", ["Run unity_cli tools group to see tool groups."]);

  const parsed = await parseTypedInput(flags, requestId);
  if (!parsed.ok) return parsed.error;
  const mergedFlags = mergeInputFlags(parsed.input, flags);
  const localTool = findToolByName(name);
  if (localTool) {
    const input = requestFromInput(localTool, { ...parsed.input, ...seed }, mergedFlags, positionalTarget);
    return dispatchResolvedTool(localTool, input, mergedFlags, runtime, requestId);
  }

  const coreSpec = bridgeCommandRegistry.specs().find((spec) => (spec.toolName === name || spec.bridgeCommand === name) && !EXTENSION_GROUPS.has(spec.group) && spec.internal !== true);
  // 注入式 bridgeExecute 用静态 spec 做隔离测试；真实 CLI 必须先经过 live descriptor/schema。
  if (coreSpec && runtime.bridgeExecute) return executeCoreBridgeSpec(name, coreSpec, parsed.input, mergedFlags, runtime, requestId, positionalTarget);

  const resolved = await resolveCatalogTool(name, mergedFlags, runtime, requestId);
  if (resolved.ok === false) return resolved;
  const tool = resolved.tool;
  if (!tool) return failure(requestId, "E_TOOL_NOT_FOUND_FOR_TARGET", `Tool not found: ${name}.`, ["Run unity_cli tools group to see tool groups.", "Run unity_cli tools search <keyword> to find related tools."]);

  const input = requestFromInput(tool, { ...parsed.input, ...seed }, mergedFlags, positionalTarget);
  return dispatchResolvedTool(tool, input, mergedFlags, runtime, requestId);
}

async function dispatchResolvedTool(
  tool: ToolMetadata,
  input: Record<string, unknown>,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  const availabilityFlags = await extensionAvailabilityFlags(flags, runtime);
  const availability = await checkToolExtensionAvailability(tool, availabilityFlags);
  if (!availability.available) return failure(requestId, availability.errorCode, availability.message, availability.nextActions, { extensionId: availability.extensionId, missingPackageIds: availability.availabilityResult?.missingPackageIds ?? tool.requiresPackages ?? [] });
  const schemaErrors = validateSchema(tool.inputSchema, input);
  if (schemaErrors.length > 0) return failure(requestId, "E_SCHEMA_INVALID", `Input schema validation failed at ${schemaErrors[0].path}.`, [`Run unity_cli tools describe ${tool.name} to inspect inputSchema.`], {
    path: schemaErrors[0].path,
    expected: schemaErrors[0].expected,
    actual: schemaErrors[0].actual,
    errors: schemaErrors
  });
  const manifestAction = tool.sideEffects?.includes("manifest-change") === true ? String(input.action) : undefined;
  const manifestWrite = manifestAction !== undefined && ["set", "clear", "add", "remove"].includes(manifestAction);
  const readOnlyManifestAction = manifestAction === "get" || manifestAction === "list";
  // 启动作业不会降低风险：构建、测试、Package resolve 等会立即产生持久副作用，必须尊重 descriptor。
  const destructiveHint = manifestWrite || (tool.destructiveHint && !readOnlyManifestAction);
  const confirmation = await confirmDestructiveExecution(flags, { command: tool.name, summary: tool.summary, destructiveHint, supportsDryRun: tool.supportsDryRun, requestId });
  if (confirmation) return confirmation;
  const definition = findDefineTool(tool.name);
  return definition ? executeDefinition(tool, definition, input, flags, runtime, requestId) : executeLiveTool(tool, input, flags, runtime, requestId);
}

/** 已有 core bridge spec 使用统一参数构造与安全门禁，不进入 live custom wrapper。 */
async function executeCoreBridgeSpec(
  name: string,
  spec: BridgeCommandSpec,
  input: Record<string, unknown>,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string,
  positionalTarget?: string
): Promise<CliResult> {
  const destructiveHint = spec.destructiveHint === true;
  const supportsDryRun = spec.supportsDryRun === true;
  const confirmation = await confirmDestructiveExecution(flags, { command: name, summary: name, destructiveHint, supportsDryRun, requestId });
  if (confirmation) return confirmation;

  let liveDescriptorFingerprint: string | undefined;
  const injectedLiveDescriptor = runtime.projectTools?.find((tool) => tool.name === name);
  if (destructiveHint && !(getBoolean(flags, "dry-run") && supportsDryRun) && (!runtime.bridgeExecute || injectedLiveDescriptor)) {
    const resolved = await resolveCatalogTool(name, flags, runtime, requestId);
    if (resolved.ok === false) return resolved;
    if (!resolved.tool || resolved.tool.destructiveHint !== true || !resolved.tool.liveDescriptorFingerprint) {
      return failure(requestId, "E_SECURITY_POLICY_MISMATCH", `Live destructive descriptor is unavailable or inconsistent for ${name}.`, [
        `Run unity_cli tools describe ${name} against the selected Unity instance.`,
        "Refresh the Bridge registry before retrying the destructive call."
      ]);
    }
    liveDescriptorFingerprint = resolved.tool.liveDescriptorFingerprint;
  }

  let args = await spec.buildArgs({ command: name, flags: bridgeWrapperFlags(flags, input), positionalTarget });
  if (spec.toolName) args = { name: spec.toolName, input: args };
  const security: BridgeSecurityDescriptor = { name, destructiveHint, supportsDryRun, bridgeCommand: spec.bridgeCommand, ...(liveDescriptorFingerprint ? { liveDescriptorFingerprint } : {}) };
  if (getBoolean(flags, "job")) {
    if (spec.supportsJob !== true || !runtime.dataDir || !runtime.store || !runtime.runner) {
      return failure(requestId, "E_TOOL_JOB_UNSUPPORTED", `Tool does not support a batch Bridge job: ${name}.`, [`Run unity_cli tools describe ${name} to inspect execution support.`]);
    }
    return startBridgeCommandJob(requestId, name, spec.bridgeCommand, args, flags, runtime as BridgeJobRuntime, spec);
  }
  return executeBridgePayload(requestId, spec.bridgeCommand, args, flags, runtime, security);
}

async function executeDefinition(
  tool: ToolMetadata,
  definition: NonNullable<ReturnType<typeof findDefineTool>>,
  input: Record<string, unknown>,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  try {
    const projectPath = typeof input.project === "string" ? input.project : process.cwd();
    const data = await definition.execute(input, {
      requestId,
      projectPath,
      execution: executionFromFlags(flags),
      dataDir: runtime.dataDir,
      artifactDir: typeof input.artifactDir === "string" ? input.artifactDir : runtime.dataDir ?? projectPath,
      logPath: typeof input.logPath === "string" ? input.logPath : undefined,
      flags,
      projectTools: runtime.projectTools,
      projectContexts: runtime.projectContexts as ContextDescriptor[] | undefined,
      bridgeExecute: runtime.bridgeExecute,
      bridgeEndpoint: runtime.bridgeEndpoint,
      fetch: runtime.fetch,
      jobStore: runtime.store,
      jobRunner: runtime.runner
    });
    return success(requestId, data);
  } catch (error) {
    if (error instanceof PackageManagerError) return failure(requestId, error.code, error.message, error.nextActions);
    if (error instanceof StructuredToolError) return failure(requestId, error.code, error.message, error.nextActions, error.details,
      error.transient === undefined ? undefined : { transient: error.transient, retryAfterMs: error.retryAfterMs });
    return failure(requestId, "COMMAND_FAILED", error instanceof Error ? error.message : String(error), [`Run unity_cli tools describe ${tool.name} for input requirements and examples.`]);
  }
}

async function executeLiveTool(
  tool: ToolMetadata,
  input: Record<string, unknown>,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  const runtimeTarget = tool.target === "runtime" || (tool.target === "both" && getBoolean(flags, "runtime"));
  const wrapper = runtimeTarget ? "runtime-tool.run" : "project-tool.run";
  const security: BridgeSecurityDescriptor = {
    name: tool.name,
    destructiveHint: tool.destructiveHint,
    supportsDryRun: tool.supportsDryRun === true,
    bridgeCommand: wrapper,
    ...(tool.liveDescriptorFingerprint ? { liveDescriptorFingerprint: tool.liveDescriptorFingerprint } : {})
  };

  if (getBoolean(flags, "job")) {
    return failure(requestId, "E_TOOL_JOB_UNSUPPORTED", `Live catalog tool does not declare a batch job bridge route: ${tool.name}.`, [`Run unity_cli tools describe ${tool.name} and check cancellation.`]);
  }
  // live descriptor 已完成 schema 校验，wrapper 必须直接承载该 typed input；不得再用旧 spec 从 execution flags 重建业务字段。
  return executeBridgeCommand(requestId, wrapper, bridgeWrapperFlags(flags, input), tool.name, runtime, security);
}

async function parseTypedInput(flags: Record<string, string | boolean>, requestId: string): Promise<ParsedInput> {
  const inputFile = getString(flags, "input-file");
  if (inputFile) return parseInputText(await readFile(resolve(inputFile), "utf8"), "--input-file", requestId);
  if (getBoolean(flags, "stdin")) {
    const stdin = await readStdin();
    if (stdin.trim()) return parseInputText(stdin, "--stdin", requestId);
  }
  const input = getString(flags, "input");
  return input === undefined ? { ok: true, input: {} } : parseInputText(input, "--input", requestId);
}

function parseInputText(text: string, source: string, requestId: string): ParsedInput {
  try {
    const value = JSON.parse(text);
    if (!value || typeof value !== "object" || Array.isArray(value)) {
      return { ok: false, error: failure(requestId, "E_SCHEMA_INVALID", `${source} must contain a JSON object.`, ["Pass an object matching the tool inputSchema."], {
        path: "$",
        expected: "object",
        actual: Array.isArray(value) ? "array" : value === null ? "null" : typeof value
      }) };
    }
    return { ok: true, input: value as Record<string, unknown> };
  } catch (error) {
    return { ok: false, error: failure(requestId, "E_TOOL_INPUT_JSON_INVALID", `${source} must be valid JSON. ${error instanceof Error ? error.message : String(error)}`, ["In PowerShell use single quotes around JSON, or use --input-file for complex input."]) };
  }
}

async function readStdin(): Promise<string> {
  if (process.stdin.isTTY) return "";
  let data = "";
  process.stdin.setEncoding("utf8");
  for await (const chunk of process.stdin) data += chunk;
  return data;
}

function requestFromInput(tool: ToolMetadata, input: Record<string, unknown>, flags: Record<string, string | boolean>, positionalTarget?: string): Record<string, unknown> {
  const request = { ...input };
  const required = Array.isArray(tool.inputSchema.required) ? tool.inputSchema.required.map(String) : [];
  if (positionalTarget !== undefined && required.length === 1) request[required[0]] ??= positionalTarget;
  const properties = tool.inputSchema.properties && typeof tool.inputSchema.properties === "object"
    ? tool.inputSchema.properties as Record<string, unknown>
    : {};
  if (positionalTarget !== undefined && "positionalTarget" in properties) request.positionalTarget ??= positionalTarget;
  for (const [key, value] of Object.entries(flags)) {
    if (key.startsWith("__") || ["input", "input-file", "stdin", "yes", "raw"].includes(key)) continue;
    const property = key.replace(/-([a-z])/g, (_match, letter: string) => letter.toUpperCase());
    if (property in properties && request[property] === undefined) request[property] = coerceFlagValue(value, properties[property]);
  }
  return request;
}

function coerceFlagValue(value: string | boolean, schema: unknown): unknown {
  if (typeof value !== "string" || !schema || typeof schema !== "object" || Array.isArray(schema)) return value;
  const type = (schema as Record<string, unknown>).type;
  if (type === "number" && value.trim() !== "" && Number.isFinite(Number(value))) return Number(value);
  if (type === "boolean" && (value === "true" || value === "false")) return value === "true";
  return value;
}

function mergeInputFlags(input: Record<string, unknown>, flags: Record<string, string | boolean>): Record<string, string | boolean> {
  const merged = { ...flags };
  for (const [key, value] of Object.entries(input)) {
    const flag = key.replace(/[A-Z]/g, (letter) => `-${letter.toLowerCase()}`);
    if (merged[flag] !== undefined) continue;
    if (typeof value === "string" || typeof value === "boolean") merged[flag] = value;
    else if (typeof value === "number") merged[flag] = String(value);
  }
  return withStructuredInput(merged, input);
}

function executionFromFlags(flags: Record<string, string | boolean>): ToolContext["execution"] {
  const number = (name: string): number | undefined => typeof flags[name] === "string" && Number.isFinite(Number(flags[name])) ? Number(flags[name]) : undefined;
  return {
    ...(getBoolean(flags, "dry-run") ? { dryRun: true } : {}),
    ...(getBoolean(flags, "job") ? { job: true } : {}),
    ...(getBoolean(flags, "wait") ? { wait: true } : {}),
    ...(number("timeout-ms") !== undefined ? { timeoutMs: number("timeout-ms") } : {}),
    ...(number("wait-timeout-ms") !== undefined ? { waitTimeoutMs: number("wait-timeout-ms") } : {}),
    ...(number("retry") !== undefined ? { retry: number("retry") } : {})
  };
}

function withStructuredInput(flags: Record<string, string | boolean>, input: Record<string, unknown>): Record<string, string | boolean> {
  return Object.keys(input).length > 0 ? { ...flags, __structuredInput: JSON.stringify(input) } : flags;
}

/** wrapper spec 只读取 input/input-file，因此显式替换为已解析并校验的目标 payload。 */
function bridgeWrapperFlags(flags: Record<string, string | boolean>, input: Record<string, unknown>): Record<string, string | boolean> {
  const result: Record<string, string | boolean> = { ...flags, input: JSON.stringify(input), __structuredInput: JSON.stringify(input) };
  delete result["input-file"];
  delete result.stdin;
  return result;
}
