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
import { ProjectIdentityError, resolveProjectIdentity } from "../project-identity.js";
import { InstanceRegistryError, currentInstanceProjectPath, resolveInstanceJournalIdentity } from "../instances.js";
import { currentInvocationOrigin } from "../invocation-origin.js";
import { executeWithLocalRequestJournal } from "../local-request-journal.js";
import { defaultDataDir } from "../platform.js";
import type { ContextCatalogRuntime } from "../contexts/catalog.js";
import { discoverProjectTools, discoverRuntimeTools, type ProjectToolRuntime } from "../project-tool-discovery.js";
import type { ContextDescriptor } from "../project-context-discovery.js";
import { confirmDestructiveExecution } from "../security.js";
import { validateSchema } from "../schema/validator.js";
import type { CliResult } from "../types.js";
import { checkToolExtensionAvailability, extensionAvailabilityFlags } from "../extension-availability.js";
import { resolveCatalogTool } from "./catalog.js";
import { generatedToolCompletionContract, type GeneratedToolCompletionContract } from "./.generated-completion-registry.js";
import { findDefineTool, type ToolContext } from "./define.js";
import { StructuredToolError } from "./errors.js";
import { EXTENSION_GROUPS, findToolByName, toolIsMutation, type ToolMetadata } from "./metadata/index.js";

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
  const dispatchStartedAtMs = Date.now();
  if (!name) return failure(requestId, "E_TOOL_NOT_FOUND_FOR_TARGET", "Tool name is required for tools run.", ["Run unity_cli tools group to see tool groups."]);
  const executionFlagError = validateExecutionTimeoutFlags(flags, requestId);
  if (executionFlagError) return executionFlagError;

  const parsed = await parseTypedInput(flags, requestId);
  if (!parsed.ok) return parsed.error;
  const invocationContract = generatedToolCompletionContract(name);
  const frozenDelivery = currentInvocationOrigin() === "batch-frozen" && invocationContract?.frozenDeliveryPolicy === "preserved";
  const dryRun = getBoolean(flags, "dry-run");
  const channel = typeof parsed.input.channel === "string" ? parsed.input.channel : getString(flags, "channel");
  const deferredDelivery = getBoolean(flags, "job") || channel === "batchmode";
  if (!dryRun && !frozenDelivery && invocationContract?.directInvocationPolicy === "prohibited-unprovable") {
    return failure(requestId, "E_DIRECT_BUSINESS_PROOF_UNAVAILABLE", `${name} cannot prove arbitrary Unity side effects for direct delivery.`, [
      "Use a dedicated Tool with a registered business-state readback predicate."
    ], { toolName: name, delivery: "direct", directInvocationPolicy: invocationContract.directInvocationPolicy });
  }
  if (!dryRun && !frozenDelivery && invocationContract?.directInvocationPolicy === "deferred-only" && !deferredDelivery) {
    return failure(requestId, "E_DIRECT_BUSINESS_PROOF_UNAVAILABLE", `${name} requires deferred delivery because live direct execution cannot independently prove arbitrary Unity side effects.`, [
      "Use --job or channel=batchmode to retain the frozen deferred execution path.",
      "Use a dedicated direct Tool when the operation needs a terminal business readback."
    ], { toolName: name, delivery: "direct", directInvocationPolicy: invocationContract.directInvocationPolicy });
  }
  const mergedFlags = mergeInputFlags(parsed.input, flags);
  const localTool = findToolByName(name);
  if (localTool) {
    const input = requestFromInput(localTool, { ...parsed.input, ...seed }, mergedFlags, positionalTarget);
    return dispatchResolvedTool(localTool, input, mergedFlags, runtime, requestId, dispatchStartedAtMs);
  }

  const coreSpec = bridgeCommandRegistry.specs().find((spec) => (spec.toolName === name || spec.bridgeCommand === name) && !EXTENSION_GROUPS.has(spec.group) && spec.internal !== true);
  // 注入式 bridgeExecute 用静态 spec 做隔离测试；真实 CLI 必须先经过 live descriptor/schema。
  if (coreSpec && runtime.bridgeExecute) return executeCoreBridgeSpec(name, coreSpec, parsed.input, mergedFlags, runtime, requestId, positionalTarget);

  const resolved = await resolveCatalogTool(name, mergedFlags, runtime, requestId);
  if (resolved.ok === false) return resolved;
  const tool = resolved.tool;
  if (!tool) return failure(requestId, "E_TOOL_NOT_FOUND_FOR_TARGET", `Tool not found: ${name}.`, ["Run unity_cli tools group to see tool groups.", "Run unity_cli tools search <keyword> to find related tools."]);

  const input = requestFromInput(tool, { ...parsed.input, ...seed }, mergedFlags, positionalTarget);
  return dispatchResolvedTool(tool, input, mergedFlags, runtime, requestId, dispatchStartedAtMs);
}

async function dispatchResolvedTool(
  tool: ToolMetadata,
  input: Record<string, unknown>,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string,
  dispatchStartedAtMs: number,
): Promise<CliResult> {
  const definition = findDefineTool(tool.name);
  let executionTool = tool;
  const offlineUiRead = tool.name === "ui.document.info" && typeof input.path === "string";
  if (!definition && !offlineUiRead) {
    const runtimeTarget = tool.target === "runtime" || (tool.target === "both" && getBoolean(flags, "runtime"));
    const discovered = runtimeTarget
      ? await discoverRuntimeTools(flags, runtime, requestId, { requireBridge: true })
      : await discoverProjectTools(flags, runtime, requestId, { requireBridge: true });
    if (discovered.ok === false) return discovered;
    const current = discovered.tools.find((candidate) => candidate.name === tool.name);
    if (!current) return failure(requestId, "E_TOOL_UNAVAILABLE", `Tool ${tool.name} is not registered in the selected Unity generation.`, [
      "Run unity_cli tools describe against the installed CLI package to inspect its static requirements.",
      "Use a Unity version and installed Extension set that registers this Tool."
    ], { toolName: tool.name, selectedTarget: runtimeTarget ? "runtime" : "editor" });
    executionTool = current;
  }
  const completion = generatedToolCompletionContract(executionTool.name) ?? executionTool.completionContract;
  if (!completion) return failure(requestId, "E_TOOL_COMPLETION_CONTRACT_REQUIRED", `Tool ${executionTool.name} has no explicit completion contract.`, [
    "Declare execution class, completion predicate, proof source, reload policy, timeout error, and outcome-unknown policy before registering this Tool."
  ], { toolName: executionTool.name });
  const descriptorMutation = toolIsMutation(executionTool);
  if (completion.mutation !== descriptorMutation) return failure(requestId, "E_TOOL_COMPLETION_CONTRACT_MISMATCH", `Tool ${executionTool.name} completion contract disagrees with its descriptor mutation signals.`, [
    `Run unity_cli tools describe ${executionTool.name} and fix the descriptor or completion contract before execution.`
  ], { toolName: executionTool.name, contractMutation: completion.mutation, descriptorMutation });
  const availabilityFlags = await extensionAvailabilityFlags(flags, runtime);
  const availability = await checkToolExtensionAvailability(executionTool, availabilityFlags);
  if (!availability.available) return failure(requestId, availability.errorCode, availability.message, availability.nextActions, { extensionId: availability.extensionId, missingPackageIds: availability.availabilityResult?.missingPackageIds ?? executionTool.requiresPackages ?? [] });
  const schemaErrors = validateSchema(executionTool.inputSchema, input);
  if (schemaErrors.length > 0) return failure(requestId, "E_SCHEMA_INVALID", `Input schema validation failed at ${schemaErrors[0].path}.`, [`Run unity_cli tools describe ${executionTool.name} to inspect inputSchema.`], {
    path: schemaErrors[0].path,
    expected: schemaErrors[0].expected,
    actual: schemaErrors[0].actual,
    errors: schemaErrors
  });
  const manifestAction = executionTool.sideEffects?.includes("manifest-change") === true ? String(input.action) : undefined;
  const manifestWrite = manifestAction !== undefined && ["set", "clear", "add", "remove"].includes(manifestAction);
  const readOnlyManifestAction = manifestAction === "get" || manifestAction === "list";
  // 启动作业不会降低风险：构建、测试、Package resolve 等会立即产生持久副作用，必须尊重 descriptor。
  const destructiveHint = manifestWrite || (executionTool.destructiveHint && !readOnlyManifestAction);
  const confirmation = await confirmDestructiveExecution(flags, { command: executionTool.name, summary: executionTool.summary, destructiveHint, supportsDryRun: executionTool.supportsDryRun, requestId });
  if (confirmation) return confirmation;
  return definition ? executeDefinition(executionTool, completion, definition, input, flags, runtime, requestId, dispatchStartedAtMs) : executeLiveTool(executionTool, input, flags, runtime, requestId);
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
  return executeBridgePayload(requestId, spec.bridgeCommand, args, flags, runtime, security, {
    mode: "execute",
    mutation: true,
  });
}

async function executeDefinition(
  tool: ToolMetadata,
  completion: GeneratedToolCompletionContract | NonNullable<ToolMetadata["completionContract"]>,
  definition: NonNullable<ReturnType<typeof findDefineTool>>,
  input: Record<string, unknown>,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string,
  dispatchStartedAtMs: number,
): Promise<CliResult> {
  let projectPath = typeof input.project === "string" ? input.project : process.cwd();
  let journalIdentity: { instanceId: string; canonicalProjectPath: string } | undefined;
  if (tool.name === "build.cancel" && runtime.store) {
    const jobId = [input.jobId, input.id, input.job].find((value): value is string => typeof value === "string" && value.length > 0);
    if (jobId) {
      const job = await runtime.store.get(jobId);
      if (job?.projectPath) projectPath = job.projectPath;
    }
  }
  const execution = executionFromFlags(flags);
  const absoluteDeadlineMs = dispatchStartedAtMs + Math.max(1, execution.timeoutMs ?? tool.timeoutMs ?? 30_000);
  const frozenDelivery = (currentInvocationOrigin() === "batch-frozen" && completion.frozenDeliveryPolicy === "preserved")
    || getBoolean(flags, "job")
    || completion.directInvocationPolicy === "deferred-only"
    || definition.handler === "job"
    || ("deliveryMode" in completion && completion.deliveryMode === "job-deferred");
  // editor.compile 的 completion journal 由 Bridge root request 持有；TS 只轮询其 terminal。
  const bridgeRootJournal = tool.name === "editor.compile";
  const requiresLocalJournal = completion.mutation && completion.journalPolicy === "required" && !execution.dryRun && !frozenDelivery && !bridgeRootJournal;
  const run = async (commitEffect: () => Promise<void>): Promise<CliResult> => {
    try {
      const data = await definition.execute(input, {
      requestId,
      projectPath,
      absoluteDeadlineMs,
      execution,
      dataDir: runtime.dataDir,
      artifactDir: typeof input.artifactDir === "string" ? input.artifactDir : runtime.dataDir ?? projectPath,
      logPath: typeof input.logPath === "string" ? input.logPath : undefined,
      flags,
      projectTools: runtime.projectTools,
      projectContexts: runtime.projectContexts as ContextDescriptor[] | undefined,
      bridgeExecute: runtime.bridgeExecute,
      fetch: runtime.fetch,
      jobStore: runtime.store,
      jobRunner: runtime.runner,
      commitEffect,
      localJournalActive: requiresLocalJournal
    });
    if (!tool.outputSchema) throw new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", `Tool ${tool.name} has no output schema for terminal proof.`, [
      `Regenerate the Tool schema and completion registry for ${tool.name}.`
    ]);
    let normalizedData: unknown;
    try {
      normalizedData = JSON.parse(JSON.stringify(data));
    } catch (error) {
      throw new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", `Tool ${tool.name} result is not JSON serializable.`, [
        "Return only values allowed by the public output schema."
      ], { toolName: tool.name, reason: error instanceof Error ? error.message : String(error) });
    }
    const outputErrors = validateSchema(tool.outputSchema, normalizedData);
    if (outputErrors.length > 0) throw new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", `Tool ${tool.name} result failed output schema validation at ${outputErrors[0].path}.`, [
      "Fix the Tool result or completion driver before retrying."
    ], { toolName: tool.name, errors: outputErrors });
    validateLocalCompletionProof(tool.name, completion, normalizedData, getBoolean(flags, "job"));
    return success(requestId, normalizedData);
  } catch (error) {
    if (error instanceof PackageManagerError) return failure(requestId, error.code, error.message, error.nextActions);
    if (error instanceof StructuredToolError) return failure(requestId, error.code, error.message, error.nextActions, error.details,
      error.transient === undefined ? undefined : { transient: error.transient, retryAfterMs: error.retryAfterMs });
      return failure(requestId, "COMMAND_FAILED", error instanceof Error ? error.message : String(error), [`Run unity_cli tools describe ${tool.name} for input requirements and examples.`]);
    }
  };

  if (!requiresLocalJournal) return run(async () => undefined);
  try {
    // --instance 必须在 journal acceptance 前绑定 registry v2 identity，绝不能由调用 CWD 代替。
    if (tool.name === "editor.launch-dialog.dismiss") {
      const selectedProjectPath = await currentInstanceProjectPath(flags, runtime.dataDir ?? defaultDataDir());
      const identity = resolveProjectIdentity(selectedProjectPath ?? projectPath);
      journalIdentity = { instanceId: identity.instanceId, canonicalProjectPath: identity.canonicalProjectPath };
    } else {
      journalIdentity = await resolveInstanceJournalIdentity(flags, runtime.dataDir ?? defaultDataDir());
      if (!journalIdentity) {
        const identity = resolveProjectIdentity(projectPath);
        journalIdentity = { instanceId: identity.instanceId, canonicalProjectPath: identity.canonicalProjectPath };
      }
    }
    projectPath = journalIdentity.canonicalProjectPath;
    return await executeWithLocalRequestJournal({
      requestId,
      toolName: tool.name,
      input,
      projectPath,
      identity: journalIdentity,
      dataDir: runtime.dataDir ?? defaultDataDir(),
      absoluteDeadlineMs,
      executionClass: completion.executionClass,
      completionDriverId: completion.completionDriverId,
    }, run);
  } catch (error) {
    if (error instanceof PackageManagerError) return failure(requestId, error.code, error.message, error.nextActions);
    if (error instanceof StructuredToolError) return failure(requestId, error.code, error.message, error.nextActions, error.details);
    if (error instanceof ProjectIdentityError) return failure(requestId, error.code, error.message, ["Pass the root of an existing Unity project."], error.details);
    if (error instanceof InstanceRegistryError) return failure(requestId, error.code, error.message, error.nextActions, error.details);
    return failure(requestId, "E_REQUEST_JOURNAL_IO", error instanceof Error ? error.message : String(error), [
      "Verify the Unity CLI data directory is writable before retrying."
    ], { toolName: tool.name });
  }
}

/** TS Tool 的 terminal proof 与 C# driver 使用同一 execution-class 约束。 */
export function validateLocalCompletionProof(
  toolName: string,
  completion: GeneratedToolCompletionContract | NonNullable<ToolMetadata["completionContract"]>,
  result: unknown,
  jobMode: boolean,
): void {
  if (jobMode || isDeferredJobRecord(result) || ("deliveryMode" in completion && completion.deliveryMode === "job-deferred")) return;
  if (completion.executionClass === "atomic" && completion.mutation === false) return;
  const preview = objectRecord(result);
  if (preview?.dryRun === true || preview?.status === "dry_run") return;
  if (containsIntermediateCompletionState(result)) {
    throw invalidCompletionProof(toolName, completion, "returned an intermediate accepted/requested/queued/started/triggered/running state");
  }
  const failedStatus = findFailedCompletionStatus(result);
  if (failedStatus) {
    throw invalidCompletionProof(toolName, completion, `returned failed terminal status ${failedStatus}`);
  }
  const toolSpecificProof = validateKnownLocalTerminal(toolName, completion, result);
  if (completion.executionClass === "atomic") return;
  if (!toolSpecificProof) {
    throw invalidCompletionProof(toolName, completion, `has no registered Tool-specific ${completion.executionClass} validator`);
  }
}

/** TS local/hybrid Tool 只接受工具专属业务断言，禁止把通用字段名当成 proof。 */
function validateKnownLocalTerminal(
  toolName: string,
  completion: GeneratedToolCompletionContract | NonNullable<ToolMetadata["completionContract"]>,
  result: unknown,
): boolean {
  const record = objectRecord(result);
  if (!record) return false;
  const liveRefresh = objectRecord(record.liveRefresh);
  if (toolName === "package.deploy") {
    if (record.status === "dry_run") return true;
    if (record.status !== "deployed" || !nonEmptyString(record.targetPath) || !nonEmptyString(record.sourceHash) || typeof record.copiedFileCount !== "number") throw invalidCompletionProof(toolName, completion, "did not prove the deployed package artifact");
    if (liveRefresh && liveRefresh.status !== "completed" && liveRefresh.status !== "skipped") throw invalidCompletionProof(toolName, completion, "did not reach a terminal package refresh state");
    return true;
  }
  if (toolName === "package.restore") {
    if (record.status === "dry_run") return true;
    if (record.status !== "restored" || !nonEmptyString(record.targetPath) || typeof record.copiedFileCount !== "number") throw invalidCompletionProof(toolName, completion, "did not prove the restored package artifact");
    if (liveRefresh && liveRefresh.status !== "completed" && liveRefresh.status !== "skipped") throw invalidCompletionProof(toolName, completion, "did not reach a terminal package refresh state");
    return true;
  }
  if (toolName === "package.registry.set") {
    if (!nonEmptyString(record.manifestPath) || !nonEmptyString(record.registry)) throw invalidCompletionProof(toolName, completion, "did not prove the manifest registry readback");
    return true;
  }
  if (toolName === "package.registry.clear") {
    if (!nonEmptyString(record.manifestPath) || record.registryCleared !== true) throw invalidCompletionProof(toolName, completion, "did not prove the cleared manifest registry readback");
    return true;
  }
  if (toolName === "package.scoped-registry.add") {
    const registry = objectRecord(record.registry);
    if (!nonEmptyString(record.manifestPath) || !registry || !nonEmptyString(registry.name) || !nonEmptyString(registry.url) || !Array.isArray(registry.scopes) || registry.scopes.some((scope) => !nonEmptyString(scope))) throw invalidCompletionProof(toolName, completion, "did not prove the scoped registry manifest readback");
    return true;
  }
  if (toolName === "package.scoped-registry.remove") {
    if (record.dryRun !== true && (!nonEmptyString(record.manifestPath) || record.registryRemoved !== true)) throw invalidCompletionProof(toolName, completion, "did not prove the removed scoped registry readback");
    return true;
  }
  if (toolName === "package.install") {
    if (record.action !== "install" || !nonEmptyString(record.manifestPath) || !nonEmptyString(record.installedVersion) || record.dependencyInstalled !== true) throw invalidCompletionProof(toolName, completion, "did not prove the manifest installation readback");
    return true;
  }
  if (toolName === "package.remove") {
    if (record.dryRun !== true && record.status !== "dry_run" && (record.action !== "remove" || !nonEmptyString(record.manifestPath) || record.dependencyRemoved !== true)) throw invalidCompletionProof(toolName, completion, "did not prove the manifest removal readback");
    return true;
  }
  if (toolName === "package.resolve") {
    if (record.resolve !== "live_bridge_refresh" || liveRefresh?.status !== "completed") throw invalidCompletionProof(toolName, completion, "did not prove terminal Unity package resolution");
    return true;
  }
  if (toolName === "package.bridge-remove") {
    if (record.action !== "bridge-remove" || !nonEmptyString(record.bridgePath) || !nonEmptyString(record.installMetadataPath)) throw invalidCompletionProof(toolName, completion, "did not prove the Bridge package removal target");
    return true;
  }
  if (toolName === "code.exec") {
    const execution = objectRecord(record.execution);
    const compile = objectRecord(record.compile);
    const runtime = objectRecord(record.runtime);
    if (execution?.exitState !== "completed" || compile?.ok !== true || runtime?.ok !== true) throw invalidCompletionProof(toolName, completion, "did not return a completed frozen Runtime Code terminal");
    return true;
  }
  if (toolName === "editor.compile") {
    if (record.dryRun === true) return true;
    const compile = objectRecord(record.compile);
    const status = compile?.status;
    if (compile?.completed !== true || compile.errorCount !== 0 || (status !== undefined && status !== "clean" && status !== "warnings")) throw invalidCompletionProof(toolName, completion, "did not prove a clean or warnings-only compile terminal");
    return true;
  }
  if (toolName === "editor.launch-dialog.dismiss") {
    if (!["dismissed", "not_found", "dry_run"].includes(String(record.status))) throw invalidCompletionProof(toolName, completion, "did not return an allowed dialog observation terminal");
    return true;
  }
  return false;
}

function invalidCompletionProof(
  toolName: string,
  completion: GeneratedToolCompletionContract | NonNullable<ToolMetadata["completionContract"]>,
  reason: string,
): StructuredToolError {
  return new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", `Tool ${toolName} ${reason}.`, [
    "Wait for the business terminal and return its stable readback."
  ], { toolName, completionDriverId: completion.completionDriverId });
}

function objectRecord(value: unknown): Record<string, unknown> | undefined {
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

function nonEmptyString(value: unknown): boolean {
  return typeof value === "string" && value.length > 0;
}

function isDeferredJobRecord(value: unknown): boolean {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const record = value as Record<string, unknown>;
  return typeof record.jobId === "string"
    && typeof record.kind === "string"
    && typeof record.status === "string";
}

function containsIntermediateCompletionState(value: unknown): boolean {
  if (Array.isArray(value)) return value.some(containsIntermediateCompletionState);
  if (!value || typeof value !== "object") return false;
  for (const [key, nested] of Object.entries(value as Record<string, unknown>)) {
    if (key.toLowerCase() === "status" && typeof nested === "string" && ["accepted", "requested", "queued", "started", "triggered", "running", "in_progress"].includes(nested.toLowerCase())) return true;
    if (["accepted", "requested", "queued", "started", "triggered"].includes(key.toLowerCase()) && nested !== undefined && nested !== null && nested !== false && nested !== "") return true;
    if (containsIntermediateCompletionState(nested)) return true;
  }
  return false;
}

/** 任何嵌套 operation 的失败终态都会使整个直接调用失败，顶层成功字段不能覆盖它。 */
function findFailedCompletionStatus(value: unknown): string | undefined {
  if (Array.isArray(value)) {
    for (const item of value) {
      const failed = findFailedCompletionStatus(item);
      if (failed) return failed;
    }
    return undefined;
  }
  if (!value || typeof value !== "object") return undefined;
  for (const [key, nested] of Object.entries(value as Record<string, unknown>)) {
    if (key.toLowerCase() === "status" && typeof nested === "string"
      && ["failed", "error", "timeout", "wait_timeout", "compile_errors", "console_errors", "blocked", "cancelled", "canceled", "aborted", "outcome_unknown", "unsupported"].includes(nested.toLowerCase())) {
      return nested;
    }
    const failed = findFailedCompletionStatus(nested);
    if (failed) return failed;
  }
  return undefined;
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
  const mutation = toolIsMutation(tool);
  return executeBridgeCommand(requestId, wrapper, bridgeWrapperFlags(flags, input), tool.name, runtime, security, {
    mode: "execute",
    mutation,
    defaultTimeoutMs: tool.timeoutMs,
  });
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

/** execution timeout 是命令控制面；非法值必须在任何 selector、journal 或 effect 前失败。 */
function validateExecutionTimeoutFlags(flags: Record<string, string | boolean>, requestId: string): CliResult | undefined {
  for (const name of ["timeout-ms", "wait-timeout-ms"] as const) {
    const raw = flags[name];
    if (raw === undefined) continue;
    const value = typeof raw === "string" && raw.trim() !== "" ? Number(raw) : Number.NaN;
    if (!Number.isSafeInteger(value) || value <= 0) {
      return failure(requestId, "E_SCHEMA_INVALID", `--${name} must be a positive integer.`, [
        `Pass --${name} with a positive integer number of milliseconds.`
      ], { path: `$.execution.${name === "timeout-ms" ? "timeoutMs" : "waitTimeoutMs"}`, expected: "positive integer", actual: raw });
    }
  }
  return undefined;
}

function executionFromFlags(flags: Record<string, string | boolean>): ToolContext["execution"] {
  const number = (name: string): number | undefined => typeof flags[name] === "string" && Number.isFinite(Number(flags[name])) ? Number(flags[name]) : undefined;
  return {
    ...(getBoolean(flags, "dry-run") ? { dryRun: true } : {}),
    ...(getBoolean(flags, "job") ? { job: true } : {}),
    ...(getBoolean(flags, "wait") ? { wait: true } : {}),
    ...(number("timeout-ms") !== undefined ? { timeoutMs: number("timeout-ms") } : {}),
    ...(number("wait-timeout-ms") !== undefined ? { waitTimeoutMs: number("wait-timeout-ms") } : {})
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
