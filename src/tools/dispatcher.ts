import { open } from "node:fs/promises";
import { resolve } from "node:path";
import { getBoolean, getString } from "../args.js";
import {
  bridgeCommandRegistry,
  executeBridgeCommand,
  executeBridgePayload,
  SessionSupervisor,
  SessionSupervisorError,
  type BridgeRuntime,
  type BridgeCommandSpec,
  type BridgeDescriptorBinding,
  type RootAdmissionLease
} from "../bridge/index.js";
import { failure, success } from "../output.js";
import { PackageManagerError } from "../package-manager.js";
import { instanceSelectionFailure, selectInstanceForFlags, selectObservationInstanceForFlags, type InstanceSelection, type InstanceTarget } from "../instances.js";
import { EditorBlockerObserver, sanitizeEditorBlockers } from "../editor-blocker-observer.js";
import { defaultDataDir } from "../platform.js";
import type { ProjectToolRuntime } from "../project-tool-discovery.js";
import { validateSchema } from "../schema/validator.js";
import type { CliResult } from "../types.js";
import { checkToolExtensionAvailability, extensionAvailabilityFlags } from "../extension-availability.js";
import { resolveCatalogIndependentInvocationTool, resolveCatalogTool } from "./catalog.js";
import { generatedToolCompletionContract, type GeneratedToolCompletionContract } from "./.generated-completion-registry.js";
import { findDefineTool, type ToolContext } from "./define.js";
import { StructuredToolError } from "./errors.js";
import { EXTENSION_GROUPS, findToolByName, toolIsMutation, type ToolMetadata } from "./metadata/index.js";

export type ToolsRuntime = ProjectToolRuntime & Partial<BridgeRuntime> & {
  dataDir?: string;
  fetch?: (url: string) => Promise<{ ok: boolean; status: number; text: () => Promise<string> }>;
};

type ParsedInput = { ok: true; input: Record<string, unknown> } | { ok: false; error: CliResult };
const MULTI_TARGET_CATALOG_TOOLS = new Set(["tools.group", "tools.group-list", "tools.search", "tools.describe", "diagnose.capabilities"]);
type TargetAdmissionContext = {
  admissions: Partial<Record<InstanceTarget, RootAdmissionLease>>;
  supervisors: Partial<Record<InstanceTarget, SessionSupervisor>>;
  executionAbsoluteDeadlineMs: number;
};

/**
 * Tool 分发顺序固定为输入解析、catalog 解析、schema 校验、目标选择、执行与 envelope。
 * 调用即授权，只保留 schema、目标与 descriptor 的正确性验证。
 */
export async function dispatchTool(
  name: string | undefined,
  positionalTarget: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string,
  seed: Record<string, unknown> = {},
  resolvedTool?: ToolMetadata
): Promise<CliResult> {
  const dispatchStartedAtMs = Date.now();
  if (!name) return failure(requestId, "E_TOOL_NOT_FOUND_FOR_TARGET", "Tool name is required for tools run.", ["Run unitycli tools group to see tool groups.", "Run unitycli tools search <k1|k2|...> to find related tools."]);
  const executionFlagError = validateExecutionTimeoutFlags(flags, requestId);
  if (executionFlagError) return executionFlagError;

  const codeInputDeadlineMs = name === "code.exec"
    ? dispatchStartedAtMs + (typeof flags["timeout-ms"] === "string" ? Number(flags["timeout-ms"]) : 60_000)
    : undefined;
  const parsed = await parseTypedInput(flags, requestId, name === "code.exec" ? 1024 * 1024 : undefined, codeInputDeadlineMs);
  if (!parsed.ok) return parsed.error;
  const dispatchFlags = withStructuredInput(flags, parsed.input);
  // command入口已从同一次live catalog读取schema；直接使用该descriptor，避免二次发现改变绑定。
  if (resolvedTool) {
    if (resolvedTool.name !== name) return failure(requestId, "E_TOOL_SCHEMA_CONFLICT", `Resolved Tool name ${resolvedTool.name} does not match requested name ${name}.`, [
      "Refresh the selected Unity instance catalog and retry the same command."
    ], { requestedName: name, resolvedName: resolvedTool.name });
    const input = requestFromInput(resolvedTool, { ...parsed.input, ...seed }, dispatchFlags, positionalTarget);
    return dispatchResolvedTool(resolvedTool, input, dispatchFlags, runtime, requestId, dispatchStartedAtMs);
  }
  const localMetadata = findToolByName(name);
  const localProperties = localMetadata?.inputSchema.properties as Record<string, unknown> | undefined;
  // TS-local schema 的 project 仅约束已选实例；不得把其他业务字段写入 selector/execution flags。
  const catalogFlags = localMetadata && localProperties && "project" in localProperties
    && typeof parsed.input.project === "string" && dispatchFlags.project === undefined
    ? { ...dispatchFlags, project: parsed.input.project }
    : dispatchFlags;
  // exact 弹窗恢复及其 describe 学习入口不依赖被同一 modal 阻断的 Live catalog。
  const catalogIndependentTool = resolveCatalogIndependentInvocationTool(
    name,
    typeof seed.name === "string" ? seed.name : undefined,
  );
  if (catalogIndependentTool) {
    const input = requestFromInput(catalogIndependentTool, { ...parsed.input, ...seed }, dispatchFlags, positionalTarget);
    return dispatchResolvedTool(catalogIndependentTool, input, dispatchFlags, runtime, requestId, dispatchStartedAtMs);
  }
  const coreSpec = bridgeCommandRegistry.specs().find((spec) => (spec.toolName === name || spec.bridgeCommand === name) && !EXTENSION_GROUPS.has(spec.group) && spec.internal !== true);
  // Runtime descriptor 的唯一 owner 在 TS catalog；执行直接进入共享 runtime wrapper，目标缺失时返回 runtime selector 错误。
  if (localMetadata?.handler === "bridge-runtime") {
    const input = requestFromInput(localMetadata, { ...parsed.input, ...seed }, dispatchFlags, positionalTarget);
    return dispatchResolvedTool(localMetadata, input, dispatchFlags, runtime, requestId, dispatchStartedAtMs);
  }
  // 无 catalog fixture 的注入式 bridgeExecute 仅用于隔离测试；其余路径必须 catalog-first。
  if (runtime.bridgeExecute && runtime.projectTools === undefined && localMetadata) {
    const input = requestFromInput(localMetadata, { ...parsed.input, ...seed }, dispatchFlags, positionalTarget);
    return dispatchResolvedTool(localMetadata, input, dispatchFlags, runtime, requestId, dispatchStartedAtMs);
  }
  if (coreSpec && runtime.bridgeExecute && runtime.projectTools === undefined) {
    return executeCoreBridgeSpec(name, coreSpec, parsed.input, dispatchFlags, runtime, requestId, positionalTarget);
  }

  const resolved = await resolveCatalogTool(name, catalogFlags, runtime, requestId);
  if (resolved.ok === false) return enrichTargetFailureWithDialogBlocker(resolved, catalogFlags, runtime);
  const tool = resolved.tool;
  if (!tool) return failure(requestId, "E_TOOL_NOT_FOUND_FOR_TARGET", `Tool not found: ${name}.`, ["Run unitycli tools group to see tool groups.", "Run unitycli tools search <k1|k2|...> to find related tools."]);

  const input = requestFromInput(tool, { ...parsed.input, ...seed }, dispatchFlags, positionalTarget);
  return dispatchResolvedTool(tool, input, dispatchFlags, runtime, requestId, dispatchStartedAtMs);
}

/** 公开统一catalog对每个在线target session分别占一个slot，禁止跨池旁路输出。 */
export async function dispatchCatalogProjection(
  command: string,
  input: Record<string, unknown>,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string,
  execute: (rootRuntime: ToolsRuntime) => Promise<CliResult>,
): Promise<CliResult> {
  const startedAtMs = Date.now();
  const timeoutMs = Math.max(1, executionFromFlags(flags).timeoutMs ?? 30_000);
  const acquired = await acquireCatalogAdmissions(command, input, flags, runtime, requestId, timeoutMs, startedAtMs + timeoutMs, "catalog-projection-v1");
  if ("result" in acquired) return acquired.result;
  const primary = acquired.admissions.editor ?? acquired.admissions.runtime!;
  const result = await execute({ ...runtime, rootAdmission: primary, rootAdmissions: acquired.admissions });
  await releaseUnconsumedCatalogAdmissions(acquired);
  return result;
}

async function acquireCatalogAdmissions(
  command: string,
  input: Record<string, unknown>,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string,
  timeoutMs: number,
  admissionAbsoluteDeadlineMs: number,
  completionContractIdentity: string,
): Promise<TargetAdmissionContext | { result: CliResult }> {
  const context: TargetAdmissionContext = { admissions: {}, supervisors: {}, executionAbsoluteDeadlineMs: 0 };
  let lastOffline: CliResult | undefined;
  for (const target of ["editor", "runtime"] as InstanceTarget[]) {
    const supervisor = new SessionSupervisor(flags, runtime);
    try {
      const admission = await supervisor.acquireRootAdmission({ requestId, command, input, dryRun: false, target, completionContractIdentity, timeoutMs, admissionAbsoluteDeadlineMs });
      context.admissions[target] = admission;
      context.supervisors[target] = supervisor;
    } catch (error) {
      const projected = catalogAdmissionFailure(requestId, error);
      if (projected.error.code === "E_INSTANCE_NOT_RUNNING" || projected.error.code === "E_RUNTIME_NOT_RUNNING") { lastOffline = projected; continue; }
      await releaseUnconsumedCatalogAdmissions(context);
      return { result: projected };
    }
  }
  const leases = Object.values(context.admissions).filter((value): value is RootAdmissionLease => !!value);
  if (leases.length === 0) return { result: lastOffline ?? failure(requestId, "E_INSTANCE_NOT_RUNNING", "No live Unity target is available for catalog projection.", ["Open the target Unity project or development Player."]) };
  const now = Date.now();
  if (leases.some((lease) => lease.grantExpiresAtMs <= now)) {
    await releaseUnconsumedCatalogAdmissions(context);
    return { result: failure(requestId, "E_UNITY_BUSY", "The Unity task-pool grants for the live catalog did not overlap before the admission deadline; no catalog request was sent.", ["Wait for active UnityCLI tasks to finish, then submit a new request."], { phase: "admission", businessRequestSent: false }, { transient: true, retryAfterMs: 100 }) };
  }
  context.executionAbsoluteDeadlineMs = now + timeoutMs;
  for (const lease of leases) lease.executionAbsoluteDeadlineMs = context.executionAbsoluteDeadlineMs;
  return context;
}

async function releaseUnconsumedCatalogAdmissions(context: TargetAdmissionContext): Promise<void> {
  for (const target of ["editor", "runtime"] as InstanceTarget[]) {
    const admission = context.admissions[target];
    const supervisor = context.supervisors[target];
    if (admission && supervisor) await supervisor.completeRootAdmission(admission);
  }
}

function catalogAdmissionFailure(requestId: string, error: unknown): CliResult & { ok: false } {
  const selectionFailure = instanceSelectionFailure(requestId, error);
  if (selectionFailure) return selectionFailure as CliResult & { ok: false };
  if (error instanceof SessionSupervisorError) return failure(requestId, error.code, error.message, error.nextActions, error.details, { transient: error.transient, retryAfterMs: error.retryAfterMs }) as CliResult & { ok: false };
  return failure(requestId, "BRIDGE_CONNECTION_FAILED", error instanceof Error ? error.message : String(error), ["Inspect the selected Unity session."], undefined, { transient: true, retryAfterMs: 100 }) as CliResult & { ok: false };
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
  const executionTool = tool;
  const completion = generatedToolCompletionContract(executionTool.name) ?? executionTool.completionContract;
  if (!completion) return failure(requestId, "E_TOOL_COMPLETION_CONTRACT_REQUIRED", `Tool ${executionTool.name} has no explicit completion contract.`, [
    "Declare execution class, completion predicate, proof source, reload policy, timeout error, and outcome-unknown policy before registering this Tool."
  ], { toolName: executionTool.name });
  const descriptorMutation = toolIsMutation(executionTool);
  if (completion.mutation !== descriptorMutation) return failure(requestId, "E_TOOL_COMPLETION_CONTRACT_MISMATCH", `Tool ${executionTool.name} completion contract disagrees with its descriptor mutation signals.`, [
    `Run unitycli tools describe ${executionTool.name} and fix the descriptor or completion contract before execution.`
  ], { toolName: executionTool.name, contractMutation: completion.mutation, descriptorMutation });
  const availabilityFlags = await extensionAvailabilityFlags(flags, runtime);
  const availability = await checkToolExtensionAvailability(executionTool, availabilityFlags);
  if (!availability.available) return failure(requestId, availability.errorCode, availability.message, availability.nextActions, { extensionId: availability.extensionId, missingPackageIds: availability.availabilityResult?.missingPackageIds ?? executionTool.requiresPackages ?? [] });
  if (getBoolean(flags, "dry-run") && !executionTool.supportsDryRun) return failure(requestId, "E_DRY_RUN_UNSUPPORTED", `Tool ${executionTool.name} does not support dry-run.`, [
    "Remove --dry-run and invoke the Tool only when its declared side effects are acceptable.",
  ], { toolName: executionTool.name });
  const schemaErrors = validateSchema(executionTool.inputSchema, input);
  if (schemaErrors.length > 0) return failure(requestId, "E_SCHEMA_INVALID", `Input schema validation failed at ${schemaErrors[0].path}.`, [`Run unitycli tools describe ${executionTool.name} to inspect inputSchema.`], {
    path: schemaErrors[0].path,
    expected: schemaErrors[0].expected,
    actual: schemaErrors[0].actual,
    errors: schemaErrors
  });
  return definition && executionTool.handler !== "bridge-runtime"
    ? executeDefinition(executionTool, completion, definition, input, flags, runtime, requestId, dispatchStartedAtMs)
    : executeLiveTool(executionTool, completion, input, flags, runtime, requestId, dispatchStartedAtMs);
}

/** 已有 core bridge spec 使用统一参数构造，不进入 live custom wrapper。 */
async function executeCoreBridgeSpec(
  name: string,
  spec: BridgeCommandSpec,
  input: Record<string, unknown>,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string,
  positionalTarget?: string
): Promise<CliResult> {
  let args = await spec.buildArgs({ command: name, flags: bridgeWrapperFlags(flags, input), positionalTarget });
  if (spec.toolName) args = { name: spec.toolName, input: args };
  return enrichTargetFailureWithDialogBlocker(await executeBridgePayload(requestId, spec.bridgeCommand, args, flags, runtime, undefined, {
    mode: "execute",
    mutation: true,
  }), flags, runtime);
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
  const execution = executionFromFlags(flags);
  const timeoutMs = Math.max(1, execution.timeoutMs ?? tool.timeoutMs ?? 30_000);
  const admissionAbsoluteDeadlineMs = dispatchStartedAtMs + timeoutMs;
  let selected: InstanceSelection | undefined;

  // package本地Tool也是live target的一等root；先固定实例，再由同一实例原子发放准入。
  try {
    if (!runtime.bridgeExecute && runtime.projectTools === undefined) {
      const selectionFlags = typeof input.project === "string" && flags.project === undefined
        ? { ...flags, project: input.project }
        : flags;
      try {
        selected = await selectInstanceForFlags(selectionFlags, runtime.dataDir ?? defaultDataDir());
      } catch (error) {
        const selectionFailure = instanceSelectionFailure(requestId, error);
        if (tool.group !== "tools" || selectionFailure?.ok !== false || selectionFailure.error.code !== "E_INSTANCE_NOT_RUNNING") throw error;
        selected = await selectInstanceForFlags(selectionFlags, runtime.dataDir ?? defaultDataDir(), process.cwd(), "runtime");
      }
      projectPath = selected.instance.canonicalProjectPath;
    }
  } catch (error) {
    const selectionFailure = instanceSelectionFailure(requestId, error);
    if (selectionFailure) return selectionFailure;
    throw error;
  }

  const admissionContext = await acquireToolAdmission(tool, completion, input, flags, runtime, requestId, timeoutMs, admissionAbsoluteDeadlineMs, selected);
  if ("result" in admissionContext) return admissionContext.result;
  const { admission, admissions, supervisors, executionAbsoluteDeadlineMs } = admissionContext;
  const absoluteDeadlineMs = executionAbsoluteDeadlineMs;
  const rootRuntime: ToolsRuntime = { ...runtime, rootAdmission: admission, rootAdmissions: admissions };

  const run = async (): Promise<CliResult> => {
    try {
      const data = await definition.execute(input, {
        requestId,
        projectPath,
        absoluteDeadlineMs,
        execution,
        dataDir: rootRuntime.dataDir,
        artifactDir: getString(flags, "artifact-dir") ?? rootRuntime.dataDir ?? projectPath,
        logPath: typeof input.logPath === "string" ? input.logPath : undefined,
        flags,
        projectTools: rootRuntime.projectTools,
        rootAdmission: admission,
        rootAdmissions: admissions,
        bridgeExecute: rootRuntime.bridgeExecute,
        fetch: rootRuntime.fetch,
        commitEffect: async () => undefined,
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
      validateLocalCompletionProof(tool.name, completion, normalizedData, input);
      return success(requestId, normalizedData);
    } catch (error) {
      if (error instanceof PackageManagerError) return failure(requestId, error.code, error.message, error.nextActions);
      if (error instanceof StructuredToolError) return failure(requestId, error.code, error.message, error.nextActions, error.details,
        error.transient === undefined ? undefined : { transient: error.transient, retryAfterMs: error.retryAfterMs });
      return failure(requestId, "COMMAND_FAILED", error instanceof Error ? error.message : String(error), [`Run unitycli tools describe ${tool.name} for input requirements and examples.`]);
    }
  };

  let result: CliResult;
  try {
    result = await settleToolExecutionAtDeadline(tool.name, requestId, completion.mutation, absoluteDeadlineMs, run);
  } catch (error) {
    if (error instanceof PackageManagerError) result = failure(requestId, error.code, error.message, error.nextActions);
    else if (error instanceof StructuredToolError) result = failure(requestId, error.code, error.message, error.nextActions, error.details);
    else result = failure(requestId, "COMMAND_FAILED", error instanceof Error ? error.message : String(error), [
      `Run unitycli tools describe ${tool.name} for input requirements and examples.`
    ]);
  }
  if (rootResultHasTrustedTerminal(result)) await releaseUnconsumedCatalogAdmissions({ admissions, supervisors, executionAbsoluteDeadlineMs });
  return result;
}

/** Tool自己的wait未按时settle时由框架结束CLI等待；已开始的Unity effect不因此停止。 */
export async function settleToolExecutionAtDeadline(
  toolName: string,
  requestId: string,
  mutation: boolean,
  absoluteDeadlineMs: number,
  run: () => Promise<CliResult>,
): Promise<CliResult> {
  const remainingMs = absoluteDeadlineMs - Date.now();
  if (remainingMs <= 0) return toolDeadlineFailure(toolName, requestId, mutation);
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      run(),
      new Promise<CliResult>((resolveTimeout) => {
        timer = setTimeout(() => resolveTimeout(toolDeadlineFailure(toolName, requestId, mutation)), remainingMs);
      }),
    ]);
  } finally {
    if (timer) clearTimeout(timer);
  }
}

/** mutation超时只能读取业务状态，不能建议直接重试或声称底层执行已取消。 */
function toolDeadlineFailure(toolName: string, requestId: string, mutation: boolean): CliResult {
  return failure(
    requestId,
    "E_EXECUTION_TIMEOUT",
    mutation
      ? `Tool ${toolName} did not return a terminal result before the immutable deadline; its effect may still be running or partially applied.`
      : `Tool ${toolName} did not return a read result before the immutable deadline.`,
    mutation
      ? ["Inspect editor.state and the affected business state before deciding whether to submit a new request."]
      : ["Inspect editor.state and submit a new read only after Unity is responsive."],
    mutation ? { outcome: "unknown" } : undefined,
    { transient: false, retryAfterMs: 0 },
  );
}

/** TS Tool 的 terminal proof 与 C# driver 使用同一 execution-class 约束。 */
export function validateLocalCompletionProof(
  toolName: string,
  completion: GeneratedToolCompletionContract | NonNullable<ToolMetadata["completionContract"]>,
  result: unknown,
  input?: Record<string, unknown>,
): void {
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
  const toolSpecificProof = validateKnownLocalTerminal(toolName, completion, result, input);
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
  input?: Record<string, unknown>,
): boolean {
  const record = objectRecord(result);
  if (!record) return false;
  if (toolName === "package.deploy") {
    if (record.status === "dry_run") return true;
    const files = objectRecord(record.files);
    if (record.status !== "deployed" || !nonEmptyString(record.targetPath) || !sha256(record.sourceHash) || !positiveInteger(record.copiedFileCount)
      || files?.proven !== true || !sha256(files.sourceHash) || !validLivePackageResolve(record.resolve)) throw invalidCompletionProof(toolName, completion, "did not prove deployed files and terminal PackageInfo resolution");
    return true;
  }
  if (toolName === "package.restore") {
    if (record.status === "dry_run") return true;
    const files = objectRecord(record.files);
    if (record.status !== "restored" || !nonEmptyString(record.targetPath) || !nonnegativeInteger(record.copiedFileCount)
      || files?.proven !== true || typeof files.restoredFromBackup !== "boolean" || typeof files.removedDeployedPackage !== "boolean"
      || !validLivePackageResolve(record.resolve)) throw invalidCompletionProof(toolName, completion, "did not prove restored files and terminal PackageInfo resolution");
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
    const files = objectRecord(record.files);
    const dependency = objectRecord(record.dependency);
    if (record.action !== "install" || !nonEmptyString(record.manifestPath) || !sha256(record.manifestHash)
      || files?.proven !== true || files.manifestHash !== record.manifestHash
      || dependency?.present !== true || !nonEmptyString(dependency.name) || !nonEmptyString(dependency.version)
      || !validLivePackageResolve(record.resolve)) throw invalidCompletionProof(toolName, completion, "did not prove manifest installation and terminal PackageInfo resolution");
    return true;
  }
  if (toolName === "package.remove") {
    if (record.dryRun === true) return true;
    const files = objectRecord(record.files);
    const dependency = objectRecord(record.dependency);
    if (record.action !== "remove" || !nonEmptyString(record.manifestPath) || !sha256(record.manifestHash)
      || files?.proven !== true || files.manifestHash !== record.manifestHash
      || dependency?.present !== false || !nonEmptyString(dependency.name)
      || !validLivePackageResolve(record.resolve)) throw invalidCompletionProof(toolName, completion, "did not prove manifest removal and terminal PackageInfo resolution");
    return true;
  }
  if (toolName === "package.resolve") {
    if (!validLivePackageResolve(record)) throw invalidCompletionProof(toolName, completion, "did not prove terminal Unity PackageManager resolution");
    return true;
  }
  if (toolName === "package.bridge-remove") {
    if (record.action !== "bridge-remove" || !nonEmptyString(record.bridgePath) || !nonEmptyString(record.installMetadataPath)) throw invalidCompletionProof(toolName, completion, "did not prove the Bridge package removal target");
    return true;
  }
  if (toolName === "code.exec") {
    const compile = objectRecord(record.compile);
    const runtime = objectRecord(record.runtime);
    const probeRequested = Object.prototype.hasOwnProperty.call(input ?? {}, "completionProbe");
    if (!Array.isArray(compile?.diagnostics) || !runtime
      || !Object.prototype.hasOwnProperty.call(runtime, "returnValue")
      || typeof runtime.stdout !== "string" || typeof runtime.stderr !== "string"
      || typeof runtime.stdoutTruncated !== "boolean" || typeof runtime.stderrTruncated !== "boolean") {
      throw invalidCompletionProof(toolName, completion, "did not return compilation and runtime results");
    }
    if (!probeRequested) return true;
    const readback = objectRecord(record.completion);
    const assertions = readback?.assertions;
    if (!Array.isArray(assertions) || assertions.length === 0 || assertions.some((item) => {
      const assertion = objectRecord(item);
      return !assertion || !nonEmptyString(assertion.path) || !Object.prototype.hasOwnProperty.call(assertion, "value");
    })) throw invalidCompletionProof(toolName, completion, "did not return completionProbe fresh readback");
    return true;
  }
  if (toolName === "asset.refresh") {
    const effects = record.plannedEffects;
    const effect = Array.isArray(effects) && effects.length === 1 ? objectRecord(effects[0]) : undefined;
    const requestedPath = input?.path ?? input?.assetPath ?? input?.target;
    if (!nonEmptyString(requestedPath) || record.path !== requestedPath || record.refreshed !== true || !nonEmptyString(record.options)
      || !effect || effect.type !== "asset.import" || effect.path !== record.path) {
      throw invalidCompletionProof(toolName, completion, "did not prove the requested asset reimport and stable path readback");
    }
    return true;
  }
  if (toolName === "package.embed") {
    const effects = record.plannedEffects;
    const effect = Array.isArray(effects) && effects.length === 1 ? objectRecord(effects[0]) : undefined;
    const packageId = input?.package;
    const embeddedPath = typeof record.embeddedPath === "string" ? record.embeddedPath.replaceAll("\\", "/") : "";
    if (!nonEmptyString(packageId) || record.packageId !== packageId || !embeddedPath.endsWith(`/Packages/${packageId}`)
      || !effect || effect.type !== "package.embed" || effect.path !== packageId) {
      throw invalidCompletionProof(toolName, completion, "did not prove the requested embedded package and local package path");
    }
    return true;
  }
  if (toolName === "editor.compile") {
    if (record.dryRun === true) return true;
    const compile = objectRecord(record.compile);
    const status = compile?.status;
    if (compile?.completed !== true || compile.errorCount !== 0 || (status !== undefined && status !== "clean" && status !== "warnings")) throw invalidCompletionProof(toolName, completion, "did not prove a clean or warnings-only compile terminal");
    return true;
  }
  if (toolName === "navmesh.bake" || toolName === "navmesh.surface.bake") {
    if (record.dryRun === true) return true;
    const expectedKind = toolName === "navmesh.bake" ? "legacy" : "surfaces";
    const topology = objectRecord(record.topology);
    const effects = Array.isArray(record.plannedEffects) ? record.plannedEffects : [];
    const effect = effects.length === 1 ? objectRecord(effects[0]) : undefined;
    const surfaces = Array.isArray(record.surfaces) ? record.surfaces : undefined;
    if (!nonEmptyString(record.operationId) || record.kind !== expectedKind || record.status !== "completed" || !timestamp(record.startedAt) || !timestamp(record.finishedAt)
      || !nonnegativeInteger(record.durationMs) || !topology || !nonnegativeInteger(topology.vertexCount) || !nonnegativeInteger(topology.indexCount) || !sha256(topology.sha256)
      || !surfaces || (expectedKind === "legacy" ? surfaces.length !== 0 : surfaces.length === 0)
      || !effect || effect.type !== toolName || !positiveInteger(effect.targetCount)) {
      throw invalidCompletionProof(toolName, completion, "did not prove the exact durable NavMesh bake terminal");
    }
    if (expectedKind === "surfaces" && surfaces.some((value) => {
      const surface = objectRecord(value);
      return !surface || !nonEmptyString(surface.globalId) || !nonEmptyString(surface.dataPath) || !sha256(surface.dataHash) || !positiveInteger(surface.dataSizeBytes);
    })) throw invalidCompletionProof(toolName, completion, "did not prove every NavMeshSurface data asset");
    return true;
  }
  if (toolName === "occlusion.bake") {
    if (record.dryRun === true) return true;
    const effectiveSettings = objectRecord(record.effectiveSettings);
    const before = objectRecord(record.before);
    const after = objectRecord(record.after);
    const afterSettings = objectRecord(after?.settings);
    const scenes = Array.isArray(record.scenes) ? record.scenes : undefined;
    const effects = Array.isArray(record.plannedEffects) ? record.plannedEffects : [];
    const effect = effects.length === 1 ? objectRecord(effects[0]) : undefined;
    if (!nonEmptyString(record.operationId) || record.status !== "completed" || !timestamp(record.startedAt) || !timestamp(record.finishedAt)
      || Date.parse(record.finishedAt as string) < Date.parse(record.startedAt as string) || !nonnegativeInteger(record.durationMs)
      || !effectiveSettings || !validOcclusionSettings(effectiveSettings) || !validOcclusionSnapshot(before) || !validOcclusionSnapshot(after)
      || !afterSettings || !sameOcclusionSettings(effectiveSettings, afterSettings)
      || !scenes || scenes.length === 0 || !effect || effect.type !== "occlusion.bake" || effect.target !== "open-scenes" || effect.targetCount !== scenes.length) {
      throw invalidCompletionProof(toolName, completion, "did not prove the exact durable Occlusion bake terminal");
    }
    if (scenes.some((value) => {
      const scene = objectRecord(value);
      return !scene || !nonEmptyString(scene.path) || !String(scene.path).startsWith("Assets/") || !String(scene.path).endsWith(".unity")
        || !/^[a-f0-9]{32}$/i.test(String(scene.guid)) || !sha256(scene.sha256) || !positiveInteger(scene.sizeBytes) || scene.dirty !== false;
    })) throw invalidCompletionProof(toolName, completion, "did not prove every frozen Occlusion scene artifact");
    return true;
  }
  if (toolName === "build.run") {
    const targets = record.targets;
    if (!Array.isArray(targets) || targets.length === 0) throw invalidCompletionProof(toolName, completion, "did not return terminal BuildReport targets");
    for (const target of targets) {
      const item = objectRecord(target);
      const artifact = objectRecord(item?.artifact);
      if (!item || !artifact || item.result !== "Succeeded" || !nonEmptyString(item.target) || !relativePublicPath(item.outputPath)
        || artifact.exists !== true || artifact.outputPath !== item.outputPath || !relativePublicPath(artifact.outputPath)
        || !positiveInteger(artifact.fileCount) || !positiveNumber(artifact.size)
        || !(sha256(artifact.hash) || sha256(artifact.treeHash))) {
        throw invalidCompletionProof(toolName, completion, "did not prove a successful project-relative BuildReport artifact");
      }
    }
    return true;
  }
  if (toolName === "test.run") {
    const summary = objectRecord(record.summary);
    const failures = record.failures;
    if (!summary || !Array.isArray(failures) || !["EditMode", "PlayMode"].includes(String(summary.platform))
      || summary.status !== "passed" || !nonnegativeInteger(summary.testCount) || !nonnegativeInteger(summary.passed)
      || !nonnegativeInteger(summary.failed) || !nonnegativeInteger(summary.skipped) || !nonnegativeInteger(summary.inconclusive)
      || typeof summary.duration !== "number" || summary.duration < 0 || !timestamp(summary.startedAt) || !timestamp(summary.finishedAt)) {
      throw invalidCompletionProof(toolName, completion, "did not prove a passed RunFinished callback terminal");
    }
    if (record.xml !== undefined) {
      const xml = objectRecord(record.xml);
      if (!xml || xml.kind !== "nunit-xml" || !nonEmptyString(xml.path) || !sha256(xml.hash) || !positiveInteger(xml.size)) throw invalidCompletionProof(toolName, completion, "did not prove the NUnit XML artifact");
    }
    return true;
  }
  if (toolName === "editor.refresh") {
    const editorState = objectRecord(record.editorState);
    if (typeof record.changed !== "boolean" || typeof record.compilationTriggered !== "boolean"
      || typeof record.sourceFileCount !== "number" || !Number.isSafeInteger(record.sourceFileCount) || record.sourceFileCount < 0
      || !sha256(record.sourceFingerprint) || !sha256(record.dependencyVersion)
      || !editorState || editorState.status !== "ready" || editorState.compile !== false || editorState.import !== false) {
      throw invalidCompletionProof(toolName, completion, "did not prove a stable source and dependency refresh readback");
    }
    return true;
  }
  if (toolName === "editor.dialog.click") {
    if (record.clicked !== true || !nonEmptyString(record.dialogId) || !nonEmptyString(record.title) || !nonEmptyString(record.button)) {
      throw invalidCompletionProof(toolName, completion, "did not prove a completed dialog click");
    }
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

function positiveInteger(value: unknown): boolean {
  return typeof value === "number" && Number.isSafeInteger(value) && value > 0;
}

function positiveNumber(value: unknown): boolean {
  return typeof value === "number" && Number.isFinite(value) && value > 0;
}

function nonnegativeInteger(value: unknown): boolean {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}

function timestamp(value: unknown): boolean {
  return typeof value === "string" && Number.isFinite(Date.parse(value));
}

function sha256(value: unknown): boolean {
  return typeof value === "string" && /^[a-f0-9]{64}$/i.test(value);
}

function relativePublicPath(value: unknown): boolean {
  return typeof value === "string" && value.length > 0 && !/^(?:[A-Za-z]:[\\/]|\\\\|\/)/.test(value);
}

function validOcclusionSettings(value: Record<string, unknown> | undefined): boolean {
  return !!value && positiveFiniteNumber(value.smallestOccluder) && positiveFiniteNumber(value.smallestHole)
    && typeof value.backfaceThreshold === "number" && Number.isFinite(value.backfaceThreshold)
    && value.backfaceThreshold >= 1 && value.backfaceThreshold <= 100;
}

function validOcclusionSnapshot(value: Record<string, unknown> | undefined): boolean {
  return !!value && nonnegativeInteger(value.umbraDataSizeBytes) && typeof value.doesSceneHaveManualPortals === "boolean"
    && validOcclusionSettings(objectRecord(value.settings));
}

function sameOcclusionSettings(left: Record<string, unknown>, right: Record<string, unknown>): boolean {
  return ["smallestOccluder", "smallestHole", "backfaceThreshold"].every((field) =>
    typeof left[field] === "number" && typeof right[field] === "number" && Math.abs((left[field] as number) - (right[field] as number)) <= 0.00001);
}

function positiveFiniteNumber(value: unknown): boolean {
  return typeof value === "number" && Number.isFinite(value) && value > 0;
}

function validLivePackageResolve(value: unknown): boolean {
  const result = objectRecord(value);
  return !!result && result.status === "resolved" && sha256(result.manifestHash) && sha256(result.lockHash)
    && sha256(result.packageSnapshotHash) && nonnegativeInteger(result.packageCount) && Array.isArray(result.packages)
    && result.packages.every((item) => {
      const pkg = objectRecord(item);
      return !!pkg && nonEmptyString(pkg.name) && nonEmptyString(pkg.version) && nonEmptyString(pkg.source);
    });
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

type ToolAdmissionContext = ({ admission: RootAdmissionLease; supervisor: SessionSupervisor } & TargetAdmissionContext) | { result: CliResult };

async function acquireToolAdmission(
  tool: ToolMetadata,
  completion: GeneratedToolCompletionContract | NonNullable<ToolMetadata["completionContract"]>,
  input: Record<string, unknown>,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string,
  timeoutMs: number,
  admissionAbsoluteDeadlineMs: number,
  selected?: InstanceSelection,
): Promise<ToolAdmissionContext> {
  if (MULTI_TARGET_CATALOG_TOOLS.has(tool.name)) {
    const acquired = await acquireCatalogAdmissions(tool.name, input, flags, runtime, requestId, timeoutMs, admissionAbsoluteDeadlineMs, JSON.stringify(completion));
    if ("result" in acquired) return acquired;
    const admission = acquired.admissions.editor ?? acquired.admissions.runtime!;
    const supervisor = acquired.supervisors[admission.target]!;
    return { admission, supervisor, ...acquired };
  }
  const target = selected?.instance.activeSession?.target ?? selected?.instance.target ?? toolTarget(tool, flags);
  const admissionFlags = selected ? { ...flags, instance: selected.instance.instanceId } : flags;
  const supervisor = new SessionSupervisor(admissionFlags, runtime);
  try {
    const admission = await supervisor.acquireRootAdmission({
      requestId,
      command: tool.name,
      input,
      dryRun: getBoolean(flags, "dry-run") || getBoolean(flags, "preflight"),
      target,
      completionContractIdentity: JSON.stringify(completion),
      timeoutMs,
      admissionAbsoluteDeadlineMs,
    });
    return {
      admission,
      supervisor,
      admissions: { [admission.target]: admission },
      supervisors: { [admission.target]: supervisor },
      executionAbsoluteDeadlineMs: admission.executionAbsoluteDeadlineMs,
    };
  } catch (error) {
    const selectionFailure = instanceSelectionFailure(requestId, error);
    if (selectionFailure) return { result: selectionFailure };
    if (error instanceof SessionSupervisorError) {
      return { result: failure(requestId, error.code, error.message, error.nextActions, error.details, {
        transient: error.transient,
        retryAfterMs: error.retryAfterMs,
      }) };
    }
    return { result: failure(requestId, "BRIDGE_CONNECTION_FAILED", error instanceof Error ? error.message : String(error), [
      "Run unitycli instances list and inspect the selected Unity session.",
    ], undefined, { transient: true, retryAfterMs: 100 }) };
  }
}

function toolTarget(tool: ToolMetadata, flags: Record<string, string | boolean>): InstanceTarget {
  return tool.target === "runtime" || (tool.target === "both" && getBoolean(flags, "runtime")) ? "runtime" : "editor";
}

const UNKNOWN_ROOT_OUTCOME_CODES = new Set([
  "E_EXECUTION_TIMEOUT",
  "BRIDGE_CONNECTION_FAILED",
  "E_BRIDGE_RESPONSE_INVALID",
  "E_BRIDGE_RESPONSE_MISMATCH",
  "COMMAND_FAILED",
]);

function rootResultHasTrustedTerminal(result: CliResult): boolean {
  return result.ok || !UNKNOWN_ROOT_OUTCOME_CODES.has(result.error.code);
}

async function executeLiveTool(
  tool: ToolMetadata,
  completion: GeneratedToolCompletionContract | NonNullable<ToolMetadata["completionContract"]>,
  input: Record<string, unknown>,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string,
  dispatchStartedAtMs: number,
): Promise<CliResult> {
  const timeoutMs = Math.max(1, executionFromFlags(flags).timeoutMs ?? tool.timeoutMs ?? 30_000);
  const admissionContext = await acquireToolAdmission(tool, completion, input, flags, runtime, requestId, timeoutMs, dispatchStartedAtMs + timeoutMs);
  if ("result" in admissionContext) return admissionContext.result;
  const { admission, supervisor } = admissionContext;
  const runtimeTarget = admission.target === "runtime";
  const wrapper = runtimeTarget ? "runtime-tool.run" : "project-tool.run";
  const descriptorBinding: BridgeDescriptorBinding = {
    ...(tool.liveDescriptorFingerprint ? { liveDescriptorFingerprint: tool.liveDescriptorFingerprint } : {})
  };
  const rootRuntime: ToolsRuntime = { ...runtime, rootAdmission: admission };

  // live descriptor已完成schema校验，wrapper直接承载typed input并复用同一root admission。
  const mutation = toolIsMutation(tool);
  const result = await enrichTargetFailureWithDialogBlocker(await executeBridgeCommand(requestId, wrapper, bridgeWrapperFlags(flags, input), tool.name, rootRuntime, descriptorBinding, {
    mode: "execute",
    mutation,
    defaultTimeoutMs: timeoutMs,
    absoluteDeadlineMs: admission.executionAbsoluteDeadlineMs,
  }), flags, rootRuntime);
  if (rootResultHasTrustedTerminal(result)) await supervisor.completeRootAdmission(admission);
  return result;
}

/** Bridge wrappers may discard supervisor details; append one independent read-only modal diagnosis to target failures. */
export async function enrichTargetFailureWithDialogBlocker(result: CliResult, flags: Record<string, string | boolean>, runtime: ToolsRuntime, observer = new EditorBlockerObserver()): Promise<CliResult> {
  if (result.ok || (result.error.details && typeof result.error.details === "object" && Array.isArray((result.error.details as Record<string, unknown>).blockers))) return result;
  try {
    const deadlineMs = Date.now() + 10_000;
    const selection = await selectObservationInstanceForFlags(flags, runtime.dataDir ?? defaultDataDir(), Math.max(0, deadlineMs - Date.now()));
    const instance = selection.instance;
    const observation = await observer.observe({ instanceId: instance.instanceId, canonicalProjectPath: instance.canonicalProjectPath, displayProjectPath: instance.displayProjectPath }, deadlineMs, instance.activeSession);
    const blockers = observation.outcome === "observed-blocked" ? sanitizeEditorBlockers(observation.blockers) : [];
    if (blockers.length === 0) return result;
    const details = result.error.details && typeof result.error.details === "object" && !Array.isArray(result.error.details) ? result.error.details : {};
    const title = blockers[0].title;
    return { ...result, error: { ...result.error, message: `${result.error.message} Modal dialog '${title}' was observed.`, details: { ...details, blockers }, nextActions: [
      "Run unitycli tools run editor.dialogs to inspect the current modal dialog.",
      "Run unitycli tools run editor.dialog.click with a current dialogId and exact button label.",
    ] } };
  } catch {
    // An unproven probe must not be represented as an empty blocker list or replace the original failure.
    return result;
  }
}

async function parseTypedInput(flags: Record<string, string | boolean>, requestId: string, maxBytes?: number, absoluteDeadlineMs?: number): Promise<ParsedInput> {
  try {
    requireInputDeadline(absoluteDeadlineMs);
    const inputFile = getString(flags, "input-file");
    if (inputFile) {
      const text = await readBoundedInputFile(resolve(inputFile), maxBytes, absoluteDeadlineMs);
      if (text === undefined) return inputTooLarge(requestId, "--input-file", maxBytes!);
      requireInputDeadline(absoluteDeadlineMs);
      return parseInputText(text, "--input-file", requestId);
    }
    if (getBoolean(flags, "stdin")) {
      const stdin = await readStdin(maxBytes, absoluteDeadlineMs);
      if (stdin === undefined) return inputTooLarge(requestId, "--stdin", maxBytes!);
      requireInputDeadline(absoluteDeadlineMs);
      if (stdin.trim()) return parseInputText(stdin, "--stdin", requestId);
    }
    const input = getString(flags, "input");
    if (input !== undefined && maxBytes !== undefined && Buffer.byteLength(input, "utf8") > maxBytes) return inputTooLarge(requestId, "--input", maxBytes);
    requireInputDeadline(absoluteDeadlineMs);
    return input === undefined ? { ok: true, input: {} } : parseInputText(input, "--input", requestId);
  } catch (error) {
    if (error instanceof InputDeadlineExceeded) return inputDeadlineExceeded(requestId);
    throw error;
  }
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

class InputDeadlineExceeded extends Error {}

/** 从同一文件句柄读取typed input，并在JSON物化前执行字节和deadline上限。 */
async function readBoundedInputFile(path: string, maxBytes?: number, absoluteDeadlineMs?: number): Promise<string | undefined> {
  const openPromise = open(path, "r");
  const handle = await beforeInputDeadline(openPromise, absoluteDeadlineMs, () => {
    void openPromise.then((lateHandle) => lateHandle.close()).catch(() => undefined);
  });
  const chunks: Buffer[] = [];
  let total = 0;
  let closeDeferred = false;
  try {
    while (true) {
      requireInputDeadline(absoluteDeadlineMs);
      const capacity = maxBytes === undefined ? 64 * 1024 : Math.min(64 * 1024, maxBytes + 1 - total);
      const buffer = Buffer.allocUnsafe(capacity);
      const readPromise = handle.read(buffer, 0, buffer.length, null);
      const { bytesRead } = await beforeInputDeadline(readPromise, absoluteDeadlineMs, () => {
        closeDeferred = true;
        void readPromise.then(() => handle.close(), () => handle.close()).catch(() => undefined);
      });
      if (bytesRead === 0) break;
      total += bytesRead;
      if (maxBytes !== undefined && total > maxBytes) return undefined;
      chunks.push(buffer.subarray(0, bytesRead));
    }
    requireInputDeadline(absoluteDeadlineMs);
    return Buffer.concat(chunks, total).toString("utf8");
  } finally {
    if (!closeDeferred) await handle.close();
  }
}

/** 事件式读取stdin，达到字节或deadline上限时立即解除监听。 */
async function readStdin(maxBytes?: number, absoluteDeadlineMs?: number): Promise<string | undefined> {
  if (process.stdin.isTTY) return "";
  requireInputDeadline(absoluteDeadlineMs);
  return new Promise<string | undefined>((resolvePromise, rejectPromise) => {
    let data = "";
    let total = 0;
    let timer: NodeJS.Timeout | undefined;
    const cleanup = (): void => {
      if (timer) clearTimeout(timer);
      process.stdin.off("data", onData);
      process.stdin.off("end", onEnd);
      process.stdin.off("error", onError);
      process.stdin.pause();
    };
    const finish = (value: string | undefined): void => { cleanup(); resolvePromise(value); };
    const onData = (chunk: string | Buffer): void => {
      const text = typeof chunk === "string" ? chunk : chunk.toString("utf8");
      total += Buffer.byteLength(text, "utf8");
      if (maxBytes !== undefined && total > maxBytes) { finish(undefined); return; }
      data += text;
    };
    const onEnd = (): void => finish(data);
    const onError = (error: Error): void => { cleanup(); rejectPromise(error); };
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", onData);
    process.stdin.once("end", onEnd);
    process.stdin.once("error", onError);
    if (absoluteDeadlineMs !== undefined) timer = setTimeout(() => { cleanup(); rejectPromise(new InputDeadlineExceeded()); }, Math.max(1, absoluteDeadlineMs - Date.now()));
    process.stdin.resume();
  });
}

/** 将一个不可取消的本地I/O等待限制在入口deadline内，并由调用方安排迟到资源回收。 */
async function beforeInputDeadline<T>(operation: Promise<T>, absoluteDeadlineMs?: number, onLate?: () => void): Promise<T> {
  if (absoluteDeadlineMs === undefined) return operation;
  requireInputDeadline(absoluteDeadlineMs);
  let timer: NodeJS.Timeout | undefined;
  try {
    return await Promise.race([
      operation,
      new Promise<never>((_, rejectPromise) => { timer = setTimeout(() => { onLate?.(); rejectPromise(new InputDeadlineExceeded()); }, Math.max(1, absoluteDeadlineMs - Date.now())); }),
    ]);
  } finally { if (timer) clearTimeout(timer); }
}

function requireInputDeadline(absoluteDeadlineMs?: number): void {
  if (absoluteDeadlineMs !== undefined && (!Number.isFinite(absoluteDeadlineMs) || Date.now() >= absoluteDeadlineMs)) throw new InputDeadlineExceeded();
}

/** 统一生成code.exec本地typed input超限错误。 */
function inputTooLarge(requestId: string, source: string, maxBytes: number): ParsedInput {
  return { ok: false, error: failure(requestId, "E_CODE_INPUT_TOO_LARGE", `${source} exceeds the ${maxBytes}-byte code.exec typed-input limit.`, ["Use a smaller JSON input and keep Runtime Code source below its documented local budget."]) };
}

function inputDeadlineExceeded(requestId: string): ParsedInput {
  return { ok: false, error: failure(requestId, "E_EXECUTION_TIMEOUT", "code.exec typed input did not finish reading before the immutable deadline.", ["Provide bounded input from a responsive local source or submit a new request with a sufficient timeout."]) };
}

const TOOL_CONTROL_FLAGS = new Set([
  "input", "input-file", "stdin", "pretty", "format",
  "instance", "project", "runtime", "timeout-ms", "artifact-dir", "dry-run"
]);

/** typed input 与 execution/selector 控制项保持严格隔离。 */
function requestFromInput(tool: ToolMetadata, input: Record<string, unknown>, flags: Record<string, string | boolean>, positionalTarget?: string): Record<string, unknown> {
  const request = { ...input };
  const required = Array.isArray(tool.inputSchema.required) ? tool.inputSchema.required.map(String) : [];
  if (positionalTarget !== undefined && required.length === 1) request[required[0]] ??= positionalTarget;
  const properties = tool.inputSchema.properties && typeof tool.inputSchema.properties === "object"
    ? tool.inputSchema.properties as Record<string, unknown>
    : {};
  if (positionalTarget !== undefined && "positionalTarget" in properties) request.positionalTarget ??= positionalTarget;
  for (const [key, value] of Object.entries(flags)) {
    if (key.startsWith("__") || TOOL_CONTROL_FLAGS.has(key)) continue;
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

/** execution timeout 是命令控制面；非法值必须在任何 selector、journal 或 effect 前失败。 */
function validateExecutionTimeoutFlags(flags: Record<string, string | boolean>, requestId: string): CliResult | undefined {
  for (const name of ["timeout-ms"] as const) {
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
    ...(number("timeout-ms") !== undefined ? { timeoutMs: number("timeout-ms") } : {})
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
