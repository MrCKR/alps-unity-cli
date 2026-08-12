import { createHash } from "node:crypto";
import { executeBridgePayload } from "../../bridge/index.js";
import { withTransitionBinding, type BridgeFlags } from "../../bridge/types.js";
import { waitForEditorTransitionEnd } from "../editor/readiness.js";
import { blockerNextActions, blockerTimeoutMessage, blockersFromDetails, StructuredToolError } from "../errors.js";
import type { ToolContext } from "../define.js";

export type OcclusionBakeState = "idle" | "running" | "completed" | "cancelled" | "failed";

export interface OcclusionBakeSettings {
  smallestOccluder: number;
  smallestHole: number;
  backfaceThreshold: number;
}

export interface OcclusionBakeSnapshot {
  umbraDataSizeBytes: number;
  doesSceneHaveManualPortals: boolean;
  settings: OcclusionBakeSettings;
}

export interface OcclusionSceneProof {
  path: string;
  guid: string;
  sha256: string;
  sizeBytes: number;
  dirty: false;
}

export interface OcclusionPlannedEffect {
  type: "occlusion.bake";
  target: "open-scenes";
  targetCount: number;
}

export interface OcclusionBakeDryRunResult {
  dryRun: true;
  targetCount: number;
  scenes: OcclusionSceneProof[];
  effectiveSettings: OcclusionBakeSettings;
  before: OcclusionBakeSnapshot;
  plannedEffects: OcclusionPlannedEffect[];
}

export interface OcclusionBakeTerminalResult {
  operationId: string;
  status: "completed";
  startedAt: string;
  finishedAt: string;
  durationMs: number;
  effectiveSettings: OcclusionBakeSettings;
  before: OcclusionBakeSnapshot;
  after: OcclusionBakeSnapshot;
  scenes: OcclusionSceneProof[];
  plannedEffects: OcclusionPlannedEffect[];
}

export type OcclusionBakeResult = OcclusionBakeDryRunResult | OcclusionBakeTerminalResult;

export interface OcclusionBakeStatusResult {
  state: OcclusionBakeState;
  active: boolean;
  operationId: string | null;
  phase: "queued" | "starting" | "baking" | "terminal" | "unknown" | null;
  startedAt: string | null;
  finishedAt: string | null;
  durationMs: number;
  progress: number | null;
  targetCount: number;
  umbraDataSizeBytes: number;
  doesSceneHaveManualPortals: boolean;
  settings: OcclusionBakeSettings;
}

export interface OcclusionCancelResult {
  cancelled: boolean;
  operationId: string | null;
  status: OcclusionBakeStatusResult;
}

type RecordValue = Record<string, unknown>;

/** 以一个公开requestId启动Occlusion durable root，只在terminal scene/Umbra proof后返回。 */
export async function runOcclusionBake(
  input: { smallestOccluder?: number; smallestHole?: number; backfaceThreshold?: number },
  context: ToolContext,
): Promise<OcclusionBakeResult> {
  const binding = await bind(context, "occlusion.bake", true);
  const deadline = deadlineOf(context);
  const request = { expectedEditorSessionId: binding.sessionId, ...input };
  if (context.execution.dryRun) {
    const planned = await call(childId(context.requestId, "_occlusion_plan"), "internal.occlusion.bake.plan", request, binding.flags, context, withTransitionBinding({ mode: "read", mutation: false, absoluteDeadlineMs: deadline }));
    return normalizeDryRun(record(planned.data));
  }

  const execute = withTransitionBinding({ mode: "execute", mutation: true, absoluteDeadlineMs: deadline });
  const observe = withTransitionBinding({ mode: "read", mutation: false, absoluteDeadlineMs: deadline });
  let accepted = false;
  let terminalReceived = false;
  try {
    let root = record((await call(context.requestId, "internal.occlusion.bake.start", request, binding.flags, context, execute)).data);
    accepted = root.accepted === true || root.terminal === true;
    const rootRequestDigest = root.terminal === true ? undefined : typeof root.rootRequestDigest === "string" && /^[a-f0-9]{64}$/.test(root.rootRequestDigest) ? root.rootRequestDigest : undefined;
    if (root.terminal !== true && !rootRequestDigest) throw invalid("start omitted its exact root digest");
    while (root.terminal !== true) {
      if (root.terminal !== false || root.accepted !== true) throw invalid("start returned neither acceptance nor a root terminal");
      if (Date.now() >= deadline) throw timeout("occlusion.bake", "E_EXECUTION_TIMEOUT");
      await delay(Math.min(100, Math.max(1, deadline - Date.now())));
      root = record((await call(childId(context.requestId, "_occlusion_status"), "internal.occlusion.bake.status", { rootRequestId: context.requestId, rootRequestDigest }, binding.flags, context, observe)).data);
    }
    terminalReceived = true;
    return terminal(root, context.requestId);
  } catch (error) {
    if (terminalReceived || !accepted || (error instanceof StructuredToolError && error.code === "E_EXECUTION_TIMEOUT")) throw error;
    const cause = error instanceof StructuredToolError ? error : undefined;
    throw timeout("occlusion.bake", cause?.code ?? "BRIDGE_CONNECTION_FAILED", cause?.details);
  }
}

/** 读取当前process session的有界Occlusion状态，不获取mutation lease。 */
export async function readOcclusionBakeStatus(context: ToolContext): Promise<OcclusionBakeStatusResult> {
  const deadline = deadlineOf(context);
  const flags: BridgeFlags = { ...(context.flags ?? {}) };
  const response = await call(childId(context.requestId, "_occlusion_snapshot"), "internal.occlusion.bake.snapshot", {}, flags, context, withTransitionBinding({ mode: "read", mutation: false, absoluteDeadlineMs: deadline }));
  return normalizeStatus(record(response.data));
}

/** 冻结exact operationId；仅queued operation可到达cancelled terminal，running返回false。 */
export async function cancelOcclusionBake(requestedOperationId: string | undefined, context: ToolContext): Promise<OcclusionCancelResult> {
  const deadline = deadlineOf(context);
  const flags: BridgeFlags = { ...(context.flags ?? {}) };
  const observe = withTransitionBinding({ mode: "read", mutation: false, absoluteDeadlineMs: deadline });
  let status: OcclusionBakeStatusResult;
  let operationId = requestedOperationId;
  if (operationId === undefined) {
    status = normalizeStatus(record((await call(childId(context.requestId, "_occlusion_cancel_snapshot"), "internal.occlusion.bake.snapshot", {}, flags, context, observe)).data));
    if (!status.active || status.state !== "running" || !status.operationId) return { cancelled: false, operationId: null, status };
    operationId = status.operationId;
  }

  await context.commitEffect?.();
  const cancellation = record((await call(childId(context.requestId, "_occlusion_cancel"), "internal.occlusion.bake.cancel", { operationId }, flags, context, withTransitionBinding({ mode: "execute", mutation: true, absoluteDeadlineMs: deadline }))).data);
  status = normalizeStatus(record(cancellation.status));
  if (cancellation.cancelled !== true) return { cancelled: false, operationId, status };
  if (!status.active && status.operationId === operationId && status.state === "cancelled") return { cancelled: true, operationId, status };
  if (!status.active && status.operationId === operationId && status.state === "failed") throw timeout("occlusion.bake-cancel", "E_EXECUTION_TIMEOUT");
  while (Date.now() < deadline) {
    status = normalizeStatus(record((await call(childId(context.requestId, "_occlusion_cancel_readback"), "internal.occlusion.bake.snapshot", {}, flags, context, observe)).data));
    if (!status.active && status.operationId === operationId && status.state === "cancelled") return { cancelled: true, operationId, status };
    if (!status.active && status.operationId === operationId && status.state === "failed") throw timeout("occlusion.bake-cancel", "E_EXECUTION_TIMEOUT");
    await delay(Math.min(100, Math.max(1, deadline - Date.now())));
  }
  throw timeout("occlusion.bake-cancel", "E_EXECUTION_TIMEOUT");
}

async function bind(context: ToolContext, tool: string, requireEditMode: boolean): Promise<{ flags: BridgeFlags; sessionId: string }> {
  const deadline = deadlineOf(context);
  const state = await waitForEditorTransitionEnd(context, childId(context.requestId, "_occlusion_state"), deadline);
  if (state.status === "blocked") throw new StructuredToolError("E_UNITY_BLOCKED", `${tool} cannot run while the Editor is blocked.`, ["Resolve the Editor blocker, then retry."], { blockers: state.blockers });
  if (state.status === "offline") throw new StructuredToolError("E_INSTANCE_NOT_RUNNING", `${tool} requires an open Unity Editor.`, ["Open the selected Unity project and retry."]);
  if (state.status === "conflict" || !state.session.known || !state.session.editorSessionId) throw new StructuredToolError("E_EDITOR_SESSION_REPLACED", `${tool} could not bind one verified Editor session.`, ["Inspect editor.state and retry after the session is stable."]);
  if (requireEditMode && isPlaying(state.state.playMode.value)) throw new StructuredToolError("E_TOOL_PLAYMODE_UNAVAILABLE", `${tool} is available only in Edit Mode.`, ["Exit Play Mode, then retry."]);
  return { flags: { ...(context.flags ?? {}), instance: state.identity.instanceId }, sessionId: state.session.editorSessionId };
}

async function call(requestId: string, command: string, input: RecordValue, flags: BridgeFlags, context: ToolContext, invocation: ReturnType<typeof withTransitionBinding>): Promise<{ data?: unknown }> {
  const response = await executeBridgePayload(requestId, command, input, flags, { dataDir: context.dataDir, bridgeExecute: context.bridgeExecute }, undefined, invocation);
  if (!response.ok) throw StructuredToolError.from(response.error ?? { code: "BRIDGE_CONNECTION_FAILED", message: "Occlusion bridge command failed." });
  return { data: response.data };
}

function terminal(root: RecordValue, requestId: string): OcclusionBakeTerminalResult {
  const envelope = record(root.rootEnvelope);
  if (envelope.requestId !== requestId || typeof envelope.ok !== "boolean") throw invalid("returned an invalid root terminal envelope");
  if (envelope.ok !== true) {
    const error = record(envelope.error);
    throw new StructuredToolError(string(error.code) ?? "E_EXECUTION_TIMEOUT", string(error.message) ?? "Occlusion bake failed without a structured terminal.", strings(error.nextActions), optionalRecord(error.details), error.transient === true, integer(error.retryAfterMs) ? error.retryAfterMs : 0);
  }
  return normalizeTerminal(record(envelope.data));
}

function normalizeDryRun(value: RecordValue): OcclusionBakeDryRunResult {
  const targetCount = nonnegative(value.targetCount);
  const scenes = Array.isArray(value.scenes) ? value.scenes.map(normalizeScene) : undefined;
  const effectiveSettings = normalizeSettings(record(value.effectiveSettings));
  const before = normalizeSnapshot(record(value.before));
  const effects = normalizeEffects(value.plannedEffects, targetCount);
  if (value.dryRun !== true || targetCount === undefined || targetCount === 0 || !scenes || scenes.length !== targetCount || !effectiveSettings || !before || !effects) throw invalid("returned an invalid dry-run plan");
  return { dryRun: true, targetCount, scenes, effectiveSettings, before, plannedEffects: effects };
}

function normalizeTerminal(value: RecordValue): OcclusionBakeTerminalResult {
  const durationMs = nonnegative(value.durationMs);
  const effectiveSettings = normalizeSettings(record(value.effectiveSettings));
  const before = normalizeSnapshot(record(value.before));
  const after = normalizeSnapshot(record(value.after));
  const scenes = Array.isArray(value.scenes) ? value.scenes.map(normalizeScene) : undefined;
  const effects = normalizeEffects(value.plannedEffects, scenes?.length);
  if (!string(value.operationId) || value.status !== "completed" || !timestamp(value.startedAt) || !timestamp(value.finishedAt)
    || Date.parse(value.finishedAt as string) < Date.parse(value.startedAt as string) || durationMs === undefined || !effectiveSettings || !before || !after
    || !scenes || scenes.length === 0 || !effects || !sameSettings(effectiveSettings, after.settings)) throw invalid("returned an invalid business terminal");
  return { operationId: value.operationId as string, status: "completed", startedAt: value.startedAt as string, finishedAt: value.finishedAt as string, durationMs, effectiveSettings, before, after, scenes, plannedEffects: effects };
}

function normalizeStatus(value: RecordValue): OcclusionBakeStatusResult {
  const states = new Set<OcclusionBakeState>(["idle", "running", "completed", "cancelled", "failed"]);
  const phases = new Set(["queued", "starting", "baking", "terminal", "unknown"]);
  const durationMs = nonnegative(value.durationMs);
  const targetCount = nonnegative(value.targetCount);
  const umbraDataSizeBytes = nonnegative(value.umbraDataSizeBytes);
  const settings = normalizeObservedSettings(record(value.settings));
  const progress = value.progress === null ? null : finiteRange(value.progress, 0, 1);
  if (!states.has(value.state as OcclusionBakeState) || typeof value.active !== "boolean" || durationMs === undefined || targetCount === undefined || umbraDataSizeBytes === undefined
    || typeof value.doesSceneHaveManualPortals !== "boolean" || !settings || progress === undefined || (value.operationId !== null && !string(value.operationId))
    || (value.phase !== null && !phases.has(value.phase as string)) || (value.startedAt !== null && !timestamp(value.startedAt)) || (value.finishedAt !== null && !timestamp(value.finishedAt))
    || (value.state === "running") !== value.active || (value.active && !string(value.operationId))) throw invalid("returned an invalid status snapshot");
  return value as unknown as OcclusionBakeStatusResult;
}

function normalizeSettings(value: RecordValue): OcclusionBakeSettings | undefined {
  const smallestOccluder = finiteRange(value.smallestOccluder, 0.000001, 1_000_000);
  const smallestHole = finiteRange(value.smallestHole, 0.000001, 1_000_000);
  const backfaceThreshold = finiteRange(value.backfaceThreshold, 1, 100);
  return smallestOccluder !== undefined && smallestHole !== undefined && backfaceThreshold !== undefined ? { smallestOccluder, smallestHole, backfaceThreshold } : undefined;
}

/** status是Unity当前值的只读观察，不把mutation输入下限误用于cancel前置读取。 */
function normalizeObservedSettings(value: RecordValue): OcclusionBakeSettings | undefined {
  const smallestOccluder = finiteRange(value.smallestOccluder, 0, 1_000_000);
  const smallestHole = finiteRange(value.smallestHole, 0, 1_000_000);
  const backfaceThreshold = finiteRange(value.backfaceThreshold, 0, 100);
  return smallestOccluder !== undefined && smallestHole !== undefined && backfaceThreshold !== undefined ? { smallestOccluder, smallestHole, backfaceThreshold } : undefined;
}

function normalizeSnapshot(value: RecordValue): OcclusionBakeSnapshot | undefined {
  const umbraDataSizeBytes = nonnegative(value.umbraDataSizeBytes);
  const settings = normalizeSettings(record(value.settings));
  return umbraDataSizeBytes !== undefined && typeof value.doesSceneHaveManualPortals === "boolean" && settings ? { umbraDataSizeBytes, doesSceneHaveManualPortals: value.doesSceneHaveManualPortals, settings } : undefined;
}

function normalizeScene(value: unknown): OcclusionSceneProof {
  const item = record(value);
  if (!string(item.path) || !String(item.path).startsWith("Assets/") || !String(item.path).endsWith(".unity") || !/^[a-f0-9]{32}$/.test(String(item.guid)) || !sha(item.sha256) || !positive(item.sizeBytes) || item.dirty !== false) throw invalid("returned an invalid scene persistence proof");
  return item as unknown as OcclusionSceneProof;
}

function normalizeEffects(value: unknown, targetCount: number | undefined): OcclusionPlannedEffect[] | undefined {
  if (!Array.isArray(value) || value.length !== 1) return undefined;
  const item = record(value[0]);
  const count = nonnegative(item.targetCount);
  if (item.type !== "occlusion.bake" || item.target !== "open-scenes" || count === undefined || count === 0 || (targetCount !== undefined && count !== targetCount)) return undefined;
  return [{ type: "occlusion.bake", target: "open-scenes", targetCount: count }];
}

function sameSettings(left: OcclusionBakeSettings, right: OcclusionBakeSettings): boolean { return close(left.smallestOccluder, right.smallestOccluder) && close(left.smallestHole, right.smallestHole) && close(left.backfaceThreshold, right.backfaceThreshold); }
function close(left: number, right: number): boolean { return Math.abs(left - right) <= 0.00001; }
function deadlineOf(context: ToolContext): number { return context.absoluteDeadlineMs ?? Date.now() + (context.execution.timeoutMs ?? 30_000); }
function timeout(tool: string, causeCode: string, details?: RecordValue): StructuredToolError { const blockers = blockersFromDetails(details); return new StructuredToolError("E_EXECUTION_TIMEOUT", blockerTimeoutMessage(tool, blockers), blockers ? blockerNextActions() : ["Run occlusion.bake-status and inspect the affected scenes before deciding whether to submit a new request."], { causeCode, ...(blockers ? { blockers } : {}) }, false, 0); }
function invalid(reason: string): StructuredToolError { return new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", `Occlusion coordinator ${reason}.`, ["Inspect UnityCliBridge.log and occlusion.bake-status before retrying."]); }
function childId(root: string, suffix: string): string { if (root.length + suffix.length <= 200) return root + suffix; return `${root.slice(0, 160)}_${createHash("sha256").update(root).digest("hex").slice(0, 24)}${suffix}`; }
function isPlaying(value: unknown): boolean { const item = record(value); return value === "play" || value === "playing" || value === "transitioning" || item.isPlaying === true || item.isPlayingOrWillChangePlaymode === true; }
function record(value: unknown): RecordValue { return value && typeof value === "object" && !Array.isArray(value) ? value as RecordValue : {}; }
function optionalRecord(value: unknown): RecordValue | undefined { const item = record(value); return Object.keys(item).length ? item : undefined; }
function string(value: unknown): string | undefined { return typeof value === "string" && value.length > 0 ? value : undefined; }
function strings(value: unknown): string[] { return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : []; }
function integer(value: unknown): value is number { return typeof value === "number" && Number.isSafeInteger(value) && value >= 0; }
function nonnegative(value: unknown): number | undefined { return integer(value) ? value : undefined; }
function positive(value: unknown): boolean { return typeof value === "number" && Number.isSafeInteger(value) && value > 0; }
function finiteRange(value: unknown, min: number, max: number): number | undefined { return typeof value === "number" && Number.isFinite(value) && value >= min && value <= max ? value : undefined; }
function timestamp(value: unknown): boolean { return typeof value === "string" && Number.isFinite(Date.parse(value)); }
function sha(value: unknown): boolean { return typeof value === "string" && /^[a-f0-9]{64}$/i.test(value); }
function delay(ms: number): Promise<void> { return new Promise(resolve => setTimeout(resolve, ms)); }
