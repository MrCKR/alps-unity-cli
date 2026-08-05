import { createHash, randomBytes } from "node:crypto";
import { executeBridgePayload } from "../../bridge/index.js";
import { withTransitionBinding, type BridgeResponse } from "../../bridge/types.js";
import type { ToolContext } from "../define.js";
import { waitForEditorTransitionEnd } from "../editor/readiness.js";
import { blockerNextActions, blockerTimeoutMessage, blockersFromDetails, StructuredToolError } from "../errors.js";

export interface LiveResolvedPackage {
  name: string;
  version: string;
  source: string;
}

export interface LiveResolveResult {
  status: "resolved";
  manifestHash: string;
  lockHash: string;
  packageSnapshotHash: string;
  packageCount: number;
  packages: LiveResolvedPackage[];
}

export interface PackageResolvePredicate {
  name: string;
  requestedVersion?: string;
  version?: string;
  present?: boolean;
  resolvedPath?: string;
  packageTreeHash?: string;
}

export interface PackageFileMutationBundle {
  expectedManifestSha256: string;
  predicates: PackageResolvePredicate[];
  filesProof: Record<string, unknown>;
  publicResultSeed: Record<string, unknown>;
}

export interface PackageFileMutationDriver {
  /** effect 前构造确定性文件计划；redelivery 使用 journal 中同一计划。 */
  prepare(signal: AbortSignal): Promise<Record<string, unknown>>;
  /** commit barrier 后执行一次文件 mutation；生产实现必须在独立 Worker 退出后才返回。 */
  apply(plan: Record<string, unknown>, signal: AbortSignal, stopAtMs: number): Promise<PackageFileMutationBundle>;
  /** response/process loss 后只读恢复已持久化计划，禁止重做 mutation。 */
  recover(plan: Record<string, unknown>, signal: AbortSignal): Promise<PackageFileMutationBundle>;
}

interface RootState {
  accepted: boolean;
  terminal: boolean;
  phase: string;
  fileExecutorAlive: boolean;
  fileExecutorOwnedByCaller: boolean;
  operation?: string;
  rootInputSha256?: string;
  filePlan?: Record<string, unknown>;
  terminalEnvelope?: Record<string, unknown>;
}

const FILE_CONTROL_RESERVE_MS = 1_000;

export interface LivePackageBinding {
  instanceId: string;
  expectedEditorSessionId: string;
}

interface PrivateRequest {
  requestId: string;
  command: string;
  args: Record<string, unknown>;
}

/** Package Resolve 是首个 effect 时也必须走同一个公开 requestId root。 */
export async function runLivePackageResolve<T>(context: ToolContext, operation: string, input: Record<string, unknown>, files?: PackageFileMutationDriver, existingBinding?: LivePackageBinding): Promise<T> {
  const binding = existingBinding ?? await bindLivePackageProject(context, operation);
  const executorCapability = randomBytes(32).toString("hex");
  const rootInput = JSON.parse(JSON.stringify(input)) as Record<string, unknown>;
  let root = await startRoot(context, binding, operation, rootInput, executorCapability);
  if (root.terminal) return terminalResult<T>(context.requestId, root);

  if (operation === "resolve") {
    await context.commitEffect?.();
  } else if (root.phase === "awaiting_files") {
    if (!files) throw new StructuredToolError("E_INTERNAL", "Package file driver is missing.", ["Report this internal package Tool error."]);
    root = await claimFiles(context, binding, operation, executorCapability);
    if (root.terminal) return terminalResult<T>(context.requestId, root);
    if (!root.fileExecutorOwnedByCaller) return waitForTerminal<T>(context, binding, executorCapability);
    let plan: Record<string, unknown>;
    try { plan = await runFileDriverStep(context, (signal) => files.prepare(signal)); }
    catch (error) {
      root = await failFiles(context, binding, operation, executorCapability, error);
      return root.terminal ? terminalResult<T>(context.requestId, root) : waitForTerminal<T>(context, binding, executorCapability);
    }
    await context.commitEffect?.();
    root = await commitFiles(context, binding, operation, executorCapability, plan);
    if (root.terminal) return terminalResult<T>(context.requestId, root);
    if (!root.fileExecutorOwnedByCaller) return waitForTerminal<T>(context, binding, executorCapability);
    try {
      const bundle = await runFileDriverStep(context, (signal, stopAtMs) => files.apply(plan, signal, stopAtMs));
      root = await proveFiles(context, binding, operation, executorCapability, bundle);
    } catch (error) {
      root = await failFiles(context, binding, operation, executorCapability, error);
    }
  } else if (root.phase === "files_committing" && !root.fileExecutorAlive) {
    if (!files || !root.filePlan) throw new StructuredToolError("E_EXECUTION_TIMEOUT", "Package file work did not return a terminal result before the request deadline.", ["Inspect package files before deciding whether to submit a new request."]);
    const recoveryPlan = root.filePlan;
    await context.commitEffect?.();
    root = await commitFiles(context, binding, operation, executorCapability, recoveryPlan);
    if (root.terminal) return terminalResult<T>(context.requestId, root);
    if (!root.fileExecutorOwnedByCaller) return waitForTerminal<T>(context, binding, executorCapability);
    try {
      const bundle = await runFileDriverStep(context, (signal) => files.recover(recoveryPlan, signal));
      root = await proveFiles(context, binding, operation, executorCapability, bundle);
    } catch (error) {
      root = await failFiles(context, binding, operation, executorCapability, error);
    }
  }
  if (root.terminal) return terminalResult<T>(context.requestId, root);
  return waitForTerminal<T>(context, binding, executorCapability);
}

/** start 只接受公开 root；transport loss 后通过同一 root status 判定，不创建 child journal。 */
async function startRoot(context: ToolContext, binding: LivePackageBinding, operation: string, input: Record<string, unknown>, executorCapability: string): Promise<RootState> {
  const request: PrivateRequest = {
    requestId: context.requestId,
    command: "internal.package.resolve.start",
    args: { expectedEditorSessionId: binding.expectedEditorSessionId, operation, input },
  };
  try {
    const response = await execute(context, binding, request, true, operation);
    return rootState(response, context.requestId);
  } catch (error) {
    const code = error instanceof StructuredToolError ? error.code : undefined;
    if (!code || !new Set(["E_EXECUTION_TIMEOUT", "E_UNITY_TRANSITION_TIMEOUT", "BRIDGE_CONNECTION_FAILED", "E_BRIDGE_RESPONSE_INVALID", "E_BRIDGE_RESPONSE_MISMATCH"]).has(code)) throw error;
    try {
      const root = await status(context, binding, executorCapability);
      if (root.operation === operation && root.rootInputSha256 === hashRecord(input)) return root;
    } catch {
      // status 只有证明 exact root 已接受时才能恢复 start response loss。
    }
    throw error;
  }
}

/** 在任何文件规划或 mutation 前取得 durable executor ownership。 */
async function claimFiles(context: ToolContext, binding: LivePackageBinding, operation: string, executorCapability: string): Promise<RootState> {
  return control(
    context, binding, operation, executorCapability, "internal.package.files.claim", "files-claim",
    { rootRequestId: context.requestId, executorPid: process.pid, executorCapability },
    (root) => root.phase === "awaiting_files" && root.fileExecutorOwnedByCaller,
  );
}

async function commitFiles(context: ToolContext, binding: LivePackageBinding, operation: string, executorCapability: string, filePlan: Record<string, unknown>): Promise<RootState> {
  return control(
    context, binding, operation, executorCapability, "internal.package.files.commit", "files-commit",
    { rootRequestId: context.requestId, filePlan, executorPid: process.pid, executorCapability },
    (root) => root.phase === "files_committing" && root.fileExecutorOwnedByCaller && sameRecord(root.filePlan, filePlan),
  );
}

async function proveFiles(context: ToolContext, binding: LivePackageBinding, operation: string, executorCapability: string, bundle: PackageFileMutationBundle): Promise<RootState> {
  assertSha256(bundle.expectedManifestSha256, "expectedManifestSha256");
  return control(context, binding, operation, executorCapability, "internal.package.files.prove", "files-prove", {
    rootRequestId: context.requestId,
    executorPid: process.pid,
    executorCapability,
    expectedManifestSha256: bundle.expectedManifestSha256,
    predicates: bundle.predicates,
    filesProof: bundle.filesProof,
    publicResultSeed: bundle.publicResultSeed,
  }, (root) => root.phase !== "awaiting_files" && root.phase !== "files_committing");
}

async function failFiles(context: ToolContext, binding: LivePackageBinding, operation: string, executorCapability: string, error: unknown): Promise<RootState> {
  const failure = failureData(error);
  return control(
    context, binding, operation, executorCapability, "internal.package.files.fail", "files-fail",
    { rootRequestId: context.requestId, executorPid: process.pid, executorCapability, ...failure },
    (root) => root.terminal,
  );
}

async function control(
  context: ToolContext,
  binding: LivePackageBinding,
  operation: string,
  executorCapability: string,
  command: string,
  suffix: string,
  args: Record<string, unknown>,
  recoveredTransitionHolds: (root: RootState) => boolean,
): Promise<RootState> {
  const request: PrivateRequest = { requestId: childRequestId(context.requestId, suffix), command, args };
  try {
    const response = await execute(context, binding, request, true, operation);
    return rootState(response, request.requestId);
  } catch (error) {
    try {
      const root = await status(context, binding, executorCapability);
      if (root.terminal || recoveredTransitionHolds(root)) return root;
    } catch {
      // status 只有证明 exact root 已进入预期状态时才能恢复 control response loss。
    }
    if (isAcceptedObservationFailure(error)) throw acceptedExecutionTimeout(error);
    throw error;
  }
}

async function status(context: ToolContext, binding: LivePackageBinding, executorCapability: string): Promise<RootState> {
  const request: PrivateRequest = {
    requestId: childRequestId(context.requestId, "status"), command: "internal.package.resolve.status",
    args: { rootRequestId: context.requestId, executorPid: process.pid, executorCapability },
  };
  return rootState(await execute(context, binding, request, false), request.requestId);
}

async function waitForTerminal<T>(context: ToolContext, binding: LivePackageBinding, executorCapability: string): Promise<T> {
  const deadline = context.absoluteDeadlineMs ?? Date.now() + (context.execution.timeoutMs ?? 30_000);
  while (Date.now() < deadline) {
    try {
      const root = await status(context, binding, executorCapability);
      if (root.terminal) return terminalResult<T>(context.requestId, root);
    } catch (error) {
      const structured = error instanceof StructuredToolError ? error : undefined;
      if (structured && new Set(["E_EDITOR_BLOCKED", "E_UNITY_BLOCKED", "E_UNITY_TRANSITIONING", "E_UNITY_TRANSITION_TIMEOUT", "E_INSTANCE_NOT_RUNNING", "E_INSTANCE_CONFLICT", "E_EDITOR_SESSION_REPLACED", "E_EXECUTION_TIMEOUT"]).has(structured.code)) {
        const blockers = blockersFromDetails(structured.details);
        throw new StructuredToolError(
          "E_EXECUTION_TIMEOUT",
          blockerTimeoutMessage("Package call", blockers),
          blockers ? blockerNextActions() : ["Inspect Package Manager and package files before submitting a new request."],
          { causeCode: structured.code, ...(blockers ? { blockers } : {}) },
          false,
          0,
        );
      }
      throw error;
    }
    await delay(50);
  }
  try {
    const root = await status(context, binding, executorCapability);
    if (root.terminal) return terminalResult<T>(context.requestId, root);
  } catch {
    // Deadline 后只保留统一 timeout，不泄露 control request 的 transport 细节。
  }
  throw new StructuredToolError(
    "E_EXECUTION_TIMEOUT",
    "Package call did not return a terminal result before the immutable deadline.",
    ["Inspect Package Manager and package files before submitting a new request."],
    undefined,
    false,
    0,
  );
}

/** 生产路径由 SessionSupervisor 选择真实 Editor；bridgeExecute 仅用于隔离测试注入。 */
async function execute(context: ToolContext, binding: LivePackageBinding, request: PrivateRequest, mutation: boolean, operation?: string): Promise<BridgeResponse> {
  const deadline = context.absoluteDeadlineMs ?? Date.now() + (context.execution.timeoutMs ?? 30_000);
  const result = await executeBridgePayload(
    request.requestId,
    request.command,
    request.args,
    { ...(context.flags ?? {}), instance: binding.instanceId },
    { dataDir: context.dataDir, bridgeExecute: context.bridgeExecute },
    undefined,
    withTransitionBinding({ mode: "execute", mutation, absoluteDeadlineMs: deadline }),
  );
  return result as BridgeResponse;
}

/** 通过一次公开 editor.state 同时绑定 session 与 canonical project，文件 plan 不再读取 CLI CWD。 */
export async function bindLivePackageProject(context: ToolContext, operation: string): Promise<LivePackageBinding> {
  const deadline = context.absoluteDeadlineMs ?? Date.now() + (context.execution.timeoutMs ?? 30_000);
  const state = await waitForEditorTransitionEnd(context, childRequestId(context.requestId, "package-state"), deadline);
  if (state.status === "blocked") throw new StructuredToolError("E_UNITY_BLOCKED", `package.${operation} cannot start while the Editor is blocked.`, ["Resolve the Editor blocker, then retry."], { blockers: state.blockers });
  if (state.status === "offline") throw new StructuredToolError("E_INSTANCE_NOT_RUNNING", `package.${operation} requires an open Unity Editor.`, ["Open the selected Unity project and retry."]);
  if (state.status === "conflict" || !state.session.known || !state.session.editorSessionId) throw new StructuredToolError("E_EDITOR_SESSION_REPLACED", `package.${operation} could not bind one verified Editor session.`, ["Inspect editor.state and retry after the session is stable."]);
  context.projectPath = state.identity.canonicalProjectPath;
  return { instanceId: state.identity.instanceId, expectedEditorSessionId: state.session.editorSessionId };
}

function rootState(response: BridgeResponse, requestId: string): RootState {
  if (!response.ok) throw bridgeError(response);
  const data = record(response.data);
  if (!data) throw new StructuredToolError("E_BRIDGE_PROTOCOL", "Package coordinator returned an invalid state envelope.", ["Inspect the Unity bridge and retry with a new requestId."], { requestId });
  const nested = record(data.terminalEnvelope);
  const state: RootState = {
    accepted: data.accepted === true,
    terminal: data.terminal === true,
    phase: typeof data.phase === "string" ? data.phase : "unknown",
    fileExecutorAlive: data.fileExecutorAlive === true,
    fileExecutorOwnedByCaller: data.fileExecutorOwnedByCaller === true,
  };
  if (typeof data.operation === "string") state.operation = data.operation;
  if (typeof data.rootInputSha256 === "string") state.rootInputSha256 = data.rootInputSha256;
  const plan = record(data.filePlan);
  if (plan) state.filePlan = plan;
  if (nested) state.terminalEnvelope = nested;
  return state;
}

function terminalResult<T>(rootRequestId: string, root: RootState): T {
  const envelope = root.terminalEnvelope;
  if (!envelope || envelope.requestId !== rootRequestId || typeof envelope.ok !== "boolean") throw new StructuredToolError("E_BRIDGE_PROTOCOL", "Package root terminal envelope is invalid.", ["Inspect package files before deciding whether to submit a new request."]);
  if (envelope.ok === true) return envelope.data as T;
  const error = record(envelope.error);
  throw new StructuredToolError(
    typeof error?.code === "string" ? error.code : "E_EXECUTION_FAILED",
    typeof error?.message === "string" ? error.message : "Package root failed.",
    Array.isArray(error?.nextActions) ? error.nextActions.filter((value): value is string => typeof value === "string") : [],
    record(error?.details),
    error?.transient === true,
    typeof error?.retryAfterMs === "number" ? error.retryAfterMs : 0,
  );
}

function bridgeError(response: Extract<BridgeResponse, { ok: false }>): StructuredToolError {
  return new StructuredToolError(response.error.code, response.error.message, response.error.nextActions ?? [], response.error.details, response.error.transient ?? false, response.error.retryAfterMs ?? 0);
}

/** 调用终态前的 selector、transport 与 blocker 失败统一为 timeout。 */
function isAcceptedObservationFailure(error: unknown): boolean {
  return error instanceof StructuredToolError && new Set([
    "E_EDITOR_BLOCKED", "E_UNITY_BLOCKED", "E_UNITY_TRANSITIONING", "E_UNITY_TRANSITION_TIMEOUT", "E_INSTANCE_NOT_RUNNING", "E_INSTANCE_CONFLICT",
    "E_EDITOR_SESSION_REPLACED", "E_EXECUTION_TIMEOUT", "BRIDGE_CONNECTION_FAILED", "E_BRIDGE_RESPONSE_INVALID", "E_BRIDGE_RESPONSE_MISMATCH"
  ]).has(error.code);
}

/** 保留原始 cause 与已清洗 blocker，不泄漏 control transport 细节。 */
function acceptedExecutionTimeout(error: unknown): StructuredToolError {
  const structured = error as StructuredToolError;
  const blockers = blockersFromDetails(structured.details);
  return new StructuredToolError(
    "E_EXECUTION_TIMEOUT",
    blockerTimeoutMessage("Package call", blockers),
    blockers ? blockerNextActions() : ["Inspect Package Manager and package files before deciding whether to submit a new request."],
    { causeCode: structured.code, ...(blockers ? { blockers } : {}) },
    false,
    0,
  );
}

function childRequestId(root: string, suffix: string): string {
  const value = `${root}_${suffix}`;
  return value.length <= 200 ? value : `${root.slice(0, 180)}_${suffix}`;
}

function record(value: unknown): Record<string, unknown> | undefined {
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

/** Package root input 仅以 SHA-256 做 response-loss 等价判定，status 不回传原始输入。 */
function hashRecord(value: Record<string, unknown>): string {
  return sha256CanonicalJson(value);
}

/** 仅为 durable request 等价比较生成稳定摘要，不参与授权。 */
export function sha256CanonicalJson(value: unknown): string {
  return createHash("sha256").update(canonicalJson(value), "utf8").digest("hex");
}

function canonicalJson(value: unknown): string {
  if (value === null || typeof value === "string" || typeof value === "boolean") return JSON.stringify(value);
  if (typeof value === "number") {
    if (!Number.isFinite(value)) throw new TypeError("Canonical JSON does not support non-finite numbers.");
    return Object.is(value, -0) ? "0" : JSON.stringify(value);
  }
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(",")}]`;
  if (typeof value === "object") {
    const record = value as Record<string, unknown>;
    return `{${Object.keys(record).filter((key) => record[key] !== undefined).sort().map((key) => `${JSON.stringify(key)}:${canonicalJson(record[key])}`).join(",")}}`;
  }
  throw new TypeError(`Canonical JSON does not support ${typeof value}.`);
}

/** 对 response-loss 后的 durable plan 做与键顺序无关的 JSON 等价比较。 */
function sameRecord(left: Record<string, unknown> | undefined, right: Record<string, unknown>): boolean {
  return left !== undefined && sameJson(left, right);
}

/** 递归比较 JSON value 与 object keys。 */
function sameJson(left: unknown, right: unknown): boolean {
  if (Object.is(left, right)) return true;
  if (Array.isArray(left) || Array.isArray(right)) {
    return Array.isArray(left) && Array.isArray(right) && left.length === right.length
      && left.every((value, index) => sameJson(value, right[index]));
  }
  const leftRecord = record(left);
  const rightRecord = record(right);
  if (!leftRecord || !rightRecord) return false;
  const leftKeys = Object.keys(leftRecord).sort();
  const rightKeys = Object.keys(rightRecord).sort();
  return leftKeys.length === rightKeys.length && leftKeys.every((key, index) => key === rightKeys[index] && sameJson(leftRecord[key], rightRecord[key]));
}

function assertSha256(value: string, field: string): void {
  if (!/^[0-9a-f]{64}$/.test(value)) throw new StructuredToolError("E_EXECUTION_TIMEOUT", `Package ${field} is not a fresh SHA-256 proof.`, ["Inspect package files before deciding whether to submit a new request."], undefined, false, 0);
}

function failureData(error: unknown): { code: string; message: string; nextActions: string[] } {
  const candidate = error as { code?: unknown; message?: unknown; nextActions?: unknown } | undefined;
  return {
    code: typeof candidate?.code === "string" ? candidate.code : "E_EXECUTION_FAILED",
    message: typeof candidate?.message === "string" && candidate.message ? candidate.message : "Package file mutation did not reach fresh proof.",
    nextActions: Array.isArray(candidate?.nextActions) ? candidate.nextActions.filter((value): value is string => typeof value === "string") : [],
  };
}

/** 为 file control 回写保留 deadline 预算；生产 apply 会把同一 stopAtMs 交给独立 Worker。 */
async function runFileDriverStep<T>(
  context: ToolContext,
  run: (signal: AbortSignal, stopAtMs: number) => Promise<T>,
): Promise<T> {
  const deadline = context.absoluteDeadlineMs ?? Date.now() + (context.execution.timeoutMs ?? 30_000);
  const remainingMs = deadline - Date.now();
  const reserveMs = Math.min(FILE_CONTROL_RESERVE_MS, Math.max(100, Math.floor(remainingMs / 10)));
  const stopAtMs = deadline - reserveMs;
  if (stopAtMs <= Date.now()) throw fileDriverDeadlineError();
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(fileDriverDeadlineError()), Math.max(1, stopAtMs - Date.now()));
  try {
    controller.signal.throwIfAborted();
    const result = await run(controller.signal, stopAtMs);
    controller.signal.throwIfAborted();
    return result;
  } finally {
    clearTimeout(timer);
  }
}

/** 将预留控制预算耗尽投影为确定的本地 timeout。 */
function fileDriverDeadlineError(): StructuredToolError {
  return new StructuredToolError(
    "E_EXECUTION_TIMEOUT",
    "Package file work did not settle before the reserved control deadline.",
    ["Inspect package files before deciding whether to submit a new request."],
    undefined,
    false,
    0,
  );
}

function delay(ms: number): Promise<void> { return new Promise(resolve => setTimeout(resolve, ms)); }
