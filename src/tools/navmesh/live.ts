import { createHash } from "node:crypto";
import { executeBridgePayload } from "../../bridge/index.js";
import { withTransitionBinding, type BridgeFlags } from "../../bridge/types.js";
import { waitForEditorTransitionEnd } from "../editor/readiness.js";
import { rejectUnavailableEditorState } from "../editor/state.js";
import { blockerNextActions, blockerTimeoutMessage, blockersFromDetails, StructuredToolError } from "../errors.js";
import type { ToolContext } from "../define.js";

export type NavMeshBakeKind = "legacy" | "surfaces";
export type NavMeshBakeState = "idle" | "running" | "completed" | "cancelled" | "failed";

export interface NavMeshTopology {
  vertexCount: number;
  indexCount: number;
  sha256: string;
}

export interface NavMeshBakeTarget {
  scenePath: string;
  sceneGuid?: string;
  globalId?: string;
  name?: string;
  beforeDataIdentity?: string;
  beforeDataHash?: string;
}

export interface NavMeshSceneProof {
  path: string;
  guid: string;
  sha256: string;
  sizeBytes: number;
  dirty: false;
}

export interface NavMeshSurfaceProof {
  globalId: string;
  name: string;
  scenePath: string;
  sceneDirty: boolean;
  dataPath: string;
  dataGuid: string;
  dataLocalId: string;
  dataIdentity: string;
  dataHash: string;
  dataSizeBytes: number;
}

export interface NavMeshPlannedEffect {
  type: "navmesh.bake" | "navmesh.surface.bake";
  target: "open-scenes" | "all-active-surfaces";
  targetCount: number;
}

export interface NavMeshBakeDryRunResult {
  dryRun: true;
  kind: NavMeshBakeKind;
  targetCount: number;
  targets: NavMeshBakeTarget[];
  scenes: NavMeshSceneProof[];
  topology: NavMeshTopology;
  plannedEffects: NavMeshPlannedEffect[];
}

export interface NavMeshBakeTerminalResult {
  operationId: string;
  kind: NavMeshBakeKind;
  status: "completed";
  startedAt: string;
  finishedAt: string;
  durationMs: number;
  topology: NavMeshTopology;
  scenes: NavMeshSceneProof[];
  surfaces: NavMeshSurfaceProof[];
  plannedEffects: NavMeshPlannedEffect[];
}

export type NavMeshBakeResult = NavMeshBakeDryRunResult | NavMeshBakeTerminalResult;

export interface NavMeshBakeStatusResult {
  state: NavMeshBakeState;
  active: boolean;
  operationId: string | null;
  kind: NavMeshBakeKind | null;
  phase: "queued" | "starting" | "baking" | "terminal" | "unknown" | null;
  startedAt: string | null;
  finishedAt: string | null;
  durationMs: number;
  progress: number | null;
  targetCount: number;
  topology: NavMeshTopology;
}

export interface NavMeshCancelResult {
  cancelled: boolean;
  operationId: string | null;
  status: NavMeshBakeStatusResult;
}

type RecordValue = Record<string, unknown>;

/** 以一个公开 requestId 启动 durable root，并只在 terminal proof 后返回。 */
export async function runNavMeshBake(kind: NavMeshBakeKind, context: ToolContext): Promise<NavMeshBakeResult> {
  // C# coordinator先做exact replay，再只对首次创建执行EditMode/readiness校验。
  const binding = await bind(context, kind === "legacy" ? "navmesh.bake" : "navmesh.surface.bake", false);
  const deadline = deadlineOf(context);
  const input = { expectedEditorSessionId: binding.sessionId, kind };
  if (context.execution.dryRun) {
    const planned = await call(childId(context.requestId, "_navmesh_plan"), "internal.navmesh.bake.plan", input, binding.flags, context, withTransitionBinding({ mode: "read", mutation: false, absoluteDeadlineMs: deadline }));
    return normalizeDryRun(record(planned.data), kind);
  }

  const execute = withTransitionBinding({ mode: "execute", mutation: true, absoluteDeadlineMs: deadline });
  const observe = withTransitionBinding({ mode: "read", mutation: false, absoluteDeadlineMs: deadline });
  let accepted = false;
  let terminalReceived = false;
  try {
    let root = record((await call(context.requestId, "internal.navmesh.bake.start", input, binding.flags, context, execute)).data);
    accepted = root.accepted === true || root.terminal === true;
    const rootRequestDigest = root.terminal === true ? undefined : typeof root.rootRequestDigest === "string" && /^[a-f0-9]{64}$/.test(root.rootRequestDigest) ? root.rootRequestDigest : undefined;
    if (root.terminal !== true && !rootRequestDigest) throw invalid("start omitted its exact root digest");
    while (root.terminal !== true) {
      if (root.terminal !== false || root.accepted !== true) throw invalid("start returned neither acceptance nor a root terminal");
      if (Date.now() >= deadline) throw timeout(kind === "legacy" ? "navmesh.bake" : "navmesh.surface.bake", "E_EXECUTION_TIMEOUT");
      await delay(Math.min(100, Math.max(1, deadline - Date.now())));
      root = record((await call(childId(context.requestId, "_navmesh_status"), "internal.navmesh.bake.status", { rootRequestId: context.requestId, rootRequestDigest }, binding.flags, context, observe)).data);
    }
    terminalReceived = true;
    return terminal(root, context.requestId, kind);
  } catch (error) {
    if (terminalReceived || !accepted || (error instanceof StructuredToolError && error.code === "E_EXECUTION_TIMEOUT")) throw error;
    const cause = error instanceof StructuredToolError ? error : undefined;
    throw timeout(kind === "legacy" ? "navmesh.bake" : "navmesh.surface.bake", cause?.code ?? "BRIDGE_CONNECTION_FAILED", cause?.details);
  }
}

/** 读取当前 session 的有界 bake snapshot；不会获取 mutation lease。 */
export async function readNavMeshBakeStatus(context: ToolContext): Promise<NavMeshBakeStatusResult> {
  const deadline = deadlineOf(context);
  const flags: BridgeFlags = { ...(context.flags ?? {}) };
  const response = await call(childId(context.requestId, "_navmesh_snapshot"), "internal.navmesh.bake.snapshot", {}, flags, context, withTransitionBinding({ mode: "read", mutation: false, absoluteDeadlineMs: deadline }));
  return normalizeStatus(record(response.data));
}

/** 冻结 exact active operation 后提交取消；显式 operationId 由 coordinator 原子校验，不需要先做第二次 snapshot。 */
export async function cancelNavMeshBake(requestedOperationId: string | undefined, context: ToolContext): Promise<NavMeshCancelResult> {
  const deadline = deadlineOf(context);
  const flags: BridgeFlags = { ...(context.flags ?? {}) };
  const observe = withTransitionBinding({ mode: "read", mutation: false, absoluteDeadlineMs: deadline });
  let status: NavMeshBakeStatusResult;
  let operationId = requestedOperationId;
  if (operationId === undefined) {
    status = normalizeStatus(record((await call(childId(context.requestId, "_navmesh_cancel_snapshot"), "internal.navmesh.bake.snapshot", {}, flags, context, observe)).data));
    if (!status.active || status.state !== "running" || !status.operationId) return { cancelled: false, operationId: null, status };
    operationId = status.operationId;
  }

  await context.commitEffect?.();
  const cancellation = record((await call(childId(context.requestId, "_navmesh_cancel"), "internal.navmesh.bake.cancel", { operationId }, flags, context, withTransitionBinding({ mode: "execute", mutation: true, absoluteDeadlineMs: deadline }))).data);
  status = normalizeStatus(record(cancellation.status));
  if (cancellation.cancelled !== true) return { cancelled: false, operationId: null, status };
  if (!status.active && status.operationId === operationId && status.state === "cancelled") return { cancelled: true, operationId, status };
  while (Date.now() < deadline) {
    status = normalizeStatus(record((await call(childId(context.requestId, "_navmesh_cancel_readback"), "internal.navmesh.bake.snapshot", {}, flags, context, observe)).data));
    if (!status.active && status.operationId === operationId && status.state === "cancelled") return { cancelled: true, operationId, status };
    await delay(Math.min(100, Math.max(1, deadline - Date.now())));
  }
  throw timeout("navmesh.bake-cancel", "E_EXECUTION_TIMEOUT");
}

async function bind(context: ToolContext, tool: string, requireEditMode: boolean): Promise<{ flags: BridgeFlags; sessionId: string }> {
  const deadline = deadlineOf(context);
  const state = await waitForEditorTransitionEnd(context, childId(context.requestId, "_navmesh_state"), deadline);
  if (state.status === "blocked") throw new StructuredToolError("E_UNITY_BLOCKED", `${tool} cannot run while the Editor is blocked.`, ["Resolve the Editor blocker, then retry."], { blockers: state.blockers });
  rejectUnavailableEditorState(state, tool);
  if (!state.session.known || !state.session.editorSessionId) throw new StructuredToolError("E_EDITOR_SESSION_REPLACED", `${tool} could not bind one verified Editor session.`, ["Inspect editor.state and retry after the session is stable."]);
  if (requireEditMode && isPlaying(state.state.playMode.value)) throw new StructuredToolError("E_TOOL_PLAYMODE_UNAVAILABLE", `${tool} is available only in Edit Mode.`, ["Exit Play Mode, then retry."]);
  return { flags: { ...(context.flags ?? {}), instance: state.identity.instanceId }, sessionId: state.session.editorSessionId };
}

async function call(requestId: string, command: string, input: RecordValue, flags: BridgeFlags, context: ToolContext, invocation: ReturnType<typeof withTransitionBinding>): Promise<{ data?: unknown }> {
  const response = await executeBridgePayload(requestId, command, input, flags, { dataDir: context.dataDir, rootAdmission: context.rootAdmission, bridgeExecute: context.bridgeExecute }, invocation);
  if (!response.ok) throw StructuredToolError.from(response.error ?? { code: "BRIDGE_CONNECTION_FAILED", message: "NavMesh bridge command failed." });
  return { data: response.data };
}

function terminal(root: RecordValue, requestId: string, expectedKind: NavMeshBakeKind): NavMeshBakeTerminalResult {
  const envelope = record(root.rootEnvelope);
  if (envelope.requestId !== requestId || typeof envelope.ok !== "boolean") throw invalid("returned an invalid root terminal envelope");
  if (envelope.ok !== true) {
    const error = record(envelope.error);
    throw new StructuredToolError(string(error.code) ?? "E_EXECUTION_TIMEOUT", string(error.message) ?? "NavMesh bake failed without a structured terminal.", strings(error.nextActions), optionalRecord(error.details), error.transient === true, integer(error.retryAfterMs) ? error.retryAfterMs : 0);
  }
  const value = normalizeTerminal(record(envelope.data));
  if (value.kind !== expectedKind) throw invalid("returned a terminal for another bake kind");
  return value;
}

function normalizeDryRun(value: RecordValue, kind: NavMeshBakeKind): NavMeshBakeDryRunResult {
  const targetCount = nonnegative(value.targetCount);
  const targets = Array.isArray(value.targets) ? value.targets.map(normalizeTarget) : undefined;
  const scenes = Array.isArray(value.scenes) ? value.scenes.map(normalizeScene) : undefined;
  const topology = normalizeTopology(record(value.topology));
  const effects = normalizeEffects(value.plannedEffects, kind, targetCount);
  if (value.dryRun !== true || value.kind !== kind || targetCount === undefined || !targets || targets.length !== targetCount || !scenes || scenes.length === 0 || !topology || !effects) throw invalid("returned an invalid dry-run plan");
  return { dryRun: true, kind, targetCount, targets, scenes, topology, plannedEffects: effects };
}

function normalizeTerminal(value: RecordValue): NavMeshBakeTerminalResult {
  const kind = value.kind;
  const durationMs = nonnegative(value.durationMs);
  const topology = normalizeTopology(record(value.topology));
  const scenes = Array.isArray(value.scenes) ? value.scenes.map(normalizeScene) : undefined;
  const surfaces = Array.isArray(value.surfaces) ? value.surfaces.map(normalizeSurface) : undefined;
  const targetCount = kind === "surfaces" ? surfaces?.length : undefined;
  const effects = (kind === "legacy" || kind === "surfaces") ? normalizeEffects(value.plannedEffects, kind, kind === "legacy" ? undefined : targetCount) : undefined;
  if (!string(value.operationId) || (kind !== "legacy" && kind !== "surfaces") || value.status !== "completed" || !timestamp(value.startedAt) || !timestamp(value.finishedAt)
    || Date.parse(value.finishedAt as string) < Date.parse(value.startedAt as string) || durationMs === undefined || !topology || !scenes || scenes.length === 0 || !surfaces || !effects
    || (kind === "legacy" && surfaces.length !== 0) || (kind === "surfaces" && surfaces.length === 0)) throw invalid("returned an invalid business terminal");
  return { operationId: value.operationId as string, kind, status: "completed", startedAt: value.startedAt as string, finishedAt: value.finishedAt as string, durationMs, topology, scenes, surfaces, plannedEffects: effects };
}

function normalizeStatus(value: RecordValue): NavMeshBakeStatusResult {
  const states = new Set<NavMeshBakeState>(["idle", "running", "completed", "cancelled", "failed"]);
  const phases = new Set(["queued", "starting", "baking", "terminal", "unknown"]);
  const durationMs = nonnegative(value.durationMs);
  const targetCount = nonnegative(value.targetCount);
  const topology = normalizeTopology(record(value.topology));
  const progress = value.progress === null ? null : finiteRange(value.progress, 0, 1);
  if (!states.has(value.state as NavMeshBakeState) || typeof value.active !== "boolean" || durationMs === undefined || targetCount === undefined || !topology || progress === undefined
    || (value.operationId !== null && !string(value.operationId)) || (value.kind !== null && value.kind !== "legacy" && value.kind !== "surfaces")
    || (value.phase !== null && !phases.has(value.phase as string)) || (value.startedAt !== null && !timestamp(value.startedAt)) || (value.finishedAt !== null && !timestamp(value.finishedAt))
    || (value.state === "running") !== value.active || (value.active && (!string(value.operationId) || (value.kind !== "legacy" && value.kind !== "surfaces")))) throw invalid("returned an invalid status snapshot");
  return value as unknown as NavMeshBakeStatusResult;
}

function normalizeTarget(value: unknown): NavMeshBakeTarget {
  const item = record(value);
  if (!string(item.scenePath) || (item.sceneGuid !== undefined && !/^[a-f0-9]{32}$/.test(String(item.sceneGuid))) || (item.globalId !== undefined && !string(item.globalId)) || (item.name !== undefined && typeof item.name !== "string") || (item.beforeDataIdentity !== undefined && typeof item.beforeDataIdentity !== "string") || (item.beforeDataHash !== undefined && item.beforeDataHash !== "" && !sha(item.beforeDataHash))) throw invalid("returned an invalid bake target");
  return item as unknown as NavMeshBakeTarget;
}
function normalizeScene(value: unknown): NavMeshSceneProof {
  const item = record(value);
  if (!string(item.path) || !String(item.path).startsWith("Assets/") || !String(item.path).endsWith(".unity") || !/^[a-f0-9]{32}$/.test(String(item.guid)) || !sha(item.sha256) || !positive(item.sizeBytes) || item.dirty !== false) throw invalid("returned an invalid scene persistence proof");
  return item as unknown as NavMeshSceneProof;
}
function normalizeSurface(value: unknown): NavMeshSurfaceProof {
  const item = record(value);
  if (!string(item.globalId) || !string(item.name) || !string(item.scenePath) || item.sceneDirty !== false || !string(item.dataPath) || !/^[a-f0-9]{32}$/.test(String(item.dataGuid))
    || !/^-?\d+$/.test(String(item.dataLocalId)) || item.dataIdentity !== `${item.dataGuid}:${item.dataLocalId}` || !sha(item.dataHash) || !positive(item.dataSizeBytes)) throw invalid("returned an invalid NavMeshSurface proof");
  return item as unknown as NavMeshSurfaceProof;
}
function normalizeTopology(value: RecordValue): NavMeshTopology | undefined {
  const vertexCount = nonnegative(value.vertexCount);
  const indexCount = nonnegative(value.indexCount);
  return vertexCount !== undefined && indexCount !== undefined && sha(value.sha256) ? { vertexCount, indexCount, sha256: value.sha256 as string } : undefined;
}
function normalizeEffects(value: unknown, kind: NavMeshBakeKind, targetCount: number | undefined): NavMeshPlannedEffect[] | undefined {
  if (!Array.isArray(value) || value.length !== 1) return undefined;
  const item = record(value[0]);
  const count = nonnegative(item.targetCount);
  const type = kind === "legacy" ? "navmesh.bake" : "navmesh.surface.bake";
  const target = kind === "legacy" ? "open-scenes" : "all-active-surfaces";
  if (item.type !== type || item.target !== target || count === undefined || (targetCount !== undefined && count !== targetCount) || count === 0) return undefined;
  return [{ type, target, targetCount: count }];
}

function deadlineOf(context: ToolContext): number { return context.absoluteDeadlineMs ?? Date.now() + (context.execution.timeoutMs ?? 30_000); }
function timeout(tool: string, causeCode: string, details?: RecordValue): StructuredToolError { const blockers = blockersFromDetails(details); return new StructuredToolError("E_EXECUTION_TIMEOUT", blockerTimeoutMessage(tool, blockers), blockers ? blockerNextActions() : ["Run navmesh.bake-status and inspect the affected scenes before deciding whether to submit a new request."], { causeCode, ...(blockers ? { blockers } : {}) }, false, 0); }
function invalid(reason: string): StructuredToolError { return new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", `NavMesh coordinator ${reason}.`, ["Inspect UnityCliBridge.log and navmesh.bake-status before retrying."]); }
function childId(root: string, suffix: string): string {
  if (root.length + suffix.length <= 200) return root + suffix;
  const digest = createHash("sha256").update(root).digest("hex").slice(0, 24);
  const prefixLength = 200 - 1 - digest.length - suffix.length;
  return `${root.slice(0, Math.max(1, prefixLength))}_${digest}${suffix}`;
}
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
