import { executeBridgePayload } from "../../bridge/index.js";
import { withObservationSelection } from "../../bridge/types.js";
import {
  InstanceRegistryError,
  applyProjectProcessClaimInspection,
  inspectProjectProcessClaims,
  selectObservationInstanceForFlags,
  verifyRegistrySessionProcess,
  type ProjectProcessClaimInspection,
  type RegistryIdentity,
  type RegistrySession,
  type SessionProcessVerification,
  type InstanceSelection,
  type UnityInstance,
} from "../../instances.js";
import { ProjectIdentityError } from "../../project-identity.js";
import { EditorBlockerObserver, sanitizeEditorBlockers, type EditorBlocker, type EditorBlockerObservation } from "../../editor-blocker-observer.js";
import type { AnyJson } from "../../schema/any-json.js";
import { defineTool, type ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface EditorStateRequest {}
type Freshness = "live" | "fresh" | "stale" | "unknown";
type ObservedField = { known: boolean; value?: AnyJson };
type StateField = typeof STATE_FIELDS[number];
type Snapshot = { schemaVersion: 1; observedAt: string; sourceEditorSessionId: string; sourceListenerGeneration: number; fields: Record<StateField, { known: boolean; value?: AnyJson }> };

export interface EditorStateProgress {
  kind: string;
  title: string;
  value?: number | null;
  canWait: boolean;
  source: "unity" | "registry";
}

export type EditorStateBlocker = EditorBlocker;

export interface EditorStateResult {
  identity: { instanceId: string; canonicalProjectPath: string; displayProjectPath: string };
  session: { known: boolean; editorSessionId: string | null };
  status: "ready" | "transitioning" | "blocked" | "unresponsive" | "unverifiable" | "offline" | "conflict";
  state: { compile: ObservedField; import: ObservedField; build: ObservedField; playMode: ObservedField; prefabStage: ObservedField; activeScene: ObservedField; runtime: ObservedField };
  progress: EditorStateProgress[];
  blockers: EditorStateBlocker[];
  observation: { freshness: Freshness };
}

const STATE_FIELDS = ["compile", "import", "build", "playMode", "prefabStage", "activeScene", "runtime"] as const;
const FRESH_SNAPSHOT_MS = 10_000;
// Windows冷启动需在同一上限内完成Bridge、PID/start和canonical project三重证明。
const OBSERVATION_BUDGET_MS = 4_000;
// Bridge子调用只能消费前半段预算；modal时必须给独立process/UI observer留下原deadline内的证明窗口。
const BLOCKER_OBSERVATION_RESERVE_MS = 2_000;
const SENSITIVE_KEYS = new Set(["endpoint", "endpointurl", "bridgeendpoint", "bridgeendpointurl", "bridgeurl", "port", "actualport", "preferredport", "listenerid", "listenergeneration", "generation", "commandline"]);
type EditorStateReadContext = Pick<ToolContext, "requestId" | "flags" | "dataDir" | "bridgeExecute"> & Partial<Pick<ToolContext, "absoluteDeadlineMs">>;
interface EditorStateReadDependencies {
  selectObservation?: typeof selectObservationInstanceForFlags;
  verifySession?: typeof verifyRegistrySessionProcess;
  inspectClaims?: typeof inspectProjectProcessClaims;
  observer?: Pick<EditorBlockerObserver, "observe">;
}

export default defineTool<EditorStateRequest, EditorStateResult>({
  name: "editor.state",
  summary: "Read the selected Unity Editor session state, progress, and external blockers without waiting.",
  description: "Read one selected Unity project's active Editor session without waiting. Input must be empty. This read-only Tool combines a bounded main-thread snapshot, Registry v3, verified process identity, and Windows modal observation; it never refreshes Unity, writes a journal, or dismisses dialogs. Use top-level instances list for registry inventory. After a started mutation timeout, inspect the affected business state before a new request.",
  group: "editor",
  destructiveHint: false,
  sideEffects: [],
  handler: "offline",
  target: "editor",
  playModeSupport: "supported",
  examples: [{ description: "Read the current selected Editor state.", argv: ["tools", "run", "editor.state", "--input", "{}"], input: {} }],
  async execute(_request, context) { return readEditorState(context); },
});

/** 聚合路径完全只读，总预算固定；任一 snapshot 只可单独作为状态字段来源。 */
export async function readEditorState(context: EditorStateReadContext, dependencies: EditorStateReadDependencies = {}): Promise<EditorStateResult> {
  const startedAt = Date.now();
  const selectObservation = dependencies.selectObservation ?? selectObservationInstanceForFlags;
  const observer = dependencies.observer ?? new EditorBlockerObserver();
  const observationDeadlineMs = Math.min(startedAt + OBSERVATION_BUDGET_MS, context.absoluteDeadlineMs ?? Number.POSITIVE_INFINITY);
  if (!context.dataDir) throw new StructuredToolError("E_INSTANCE_NOT_FOUND", "UnityCLI data directory is required to resolve the selected Editor instance.", ["Pass --instance <instanceId|projectPath|alias> or run from a Unity project directory."]);
  let selection: InstanceSelection;
  let instance: UnityInstance;
  try {
    selection = await selectObservation(context.flags ?? {}, context.dataDir, remaining(observationDeadlineMs));
    instance = selection.instance;
  } catch (error) {
    if (error instanceof InstanceRegistryError || error instanceof ProjectIdentityError) {
      throw new StructuredToolError(error.code, error.message, error instanceof InstanceRegistryError ? error.nextActions : ["Pass --instance <instanceId|canonicalProjectPath|alias> or run from a local Unity project directory."], error.details);
    }
    throw error;
  }
  // conflict已由多个verified claim证明；raw unverifiable仍允许observer重新证明唯一PID/start后发现exact modal。
  if (instance.status === "conflict") return makeResult(instance, undefined, "conflict", undefined, [], [], false);
  const session = instance.activeSession;
  if (!session) {
    const observed = await observer.observe(identityOf(instance), observationDeadlineMs);
    if (observed.outcome === "observed-blocked") return makeResult(instance, undefined, "blocked", undefined, [], sanitizeEditorBlockers(observed.blockers), false);
    if (observed.outcome === "conflict") return makeResult(instance, undefined, "conflict", undefined, [], [], false);
    if (observed.outcome === "offline") return makeResult(instance, undefined, "offline", undefined, [], [], false);
    if (observed.outcome === "observed-empty") return makeResult(instance, undefined, "transitioning", undefined, [], [], false);
    return makeResult(instance, undefined, "unverifiable", undefined, [], [], false);
  }

  const fallbackBudgetMs = remaining(observationDeadlineMs);
  if (fallbackBudgetMs <= 0) return makeResult(instance, session, "unresponsive", undefined, [], [], false);
  // 正常Bridge成功路径不访问Windows UI；失败后才证明process和modal。
  // bridgeExecute是隔离测试transport，生产CLI不提供；测试必须复用已通过selector的同一Registry session identity。
  const verificationPromise = dependencies.verifySession
    ? dependencies.verifySession(identityOf(instance), session, fallbackBudgetMs)
    : context.bridgeExecute
      ? Promise.resolve("verified" as const)
      : verifyRegistrySessionProcess(identityOf(instance), session, fallbackBudgetMs);
  const processClaimsPromise = dependencies.inspectClaims
    ? dependencies.inspectClaims(identityOf(instance), fallbackBudgetMs)
    : context.bridgeExecute
      ? Promise.resolve({ verification: "verified" as const, processIds: [session.processId], processClaims: [{ processId: session.processId, processStartedAt: session.processStartedAt }] })
      : inspectProjectProcessClaims(identityOf(instance), fallbackBudgetMs);
  const bridgeDeadlineMs = Math.max(Date.now(), observationDeadlineMs - BLOCKER_OBSERVATION_RESERVE_MS);
  const bridge = await probeBridge(context, bridgeDeadlineMs, session, selection);
  if (bridge && isLiveSnapshot(bridge.snapshot)) {
    const processClaims = await processClaimsPromise;
    const inspected = applyProjectProcessClaimInspection(instance, processClaims);
    if (inspected.status === "conflict" || inspected.status === "unverifiable") return makeResult(inspected, undefined, inspected.status, undefined, [], sanitizeEditorBlockers(inspected.blockers), false);
    const progress = sanitizeProgress(bridge.progress);
    const blockers = sanitizeEditorBlockers(bridge.blockers);
    return makeResult(inspected, session, observedStatus(bridge.snapshot, progress, blockers), bridge.snapshot, progress, blockers, true);
  }

  const [verification, processClaims] = await Promise.all([verificationPromise, processClaimsPromise]);
  const inspected = applyProjectProcessClaimInspection(instance, processClaims);
  if (inspected.status === "conflict") return makeResult(inspected, undefined, "conflict", undefined, [], sanitizeEditorBlockers(inspected.blockers), false);
  if (inspected.status === "unverifiable") {
    // 第一次process scan不完整时只允许独立observer重新证明blocker/conflict/offline；不得恢复ready或信任registry session。
    const independent = await observer.observe(identityOf(instance), observationDeadlineMs);
    if (independent.outcome === "observed-blocked") return makeResult(inspected, undefined, "blocked", undefined, [], sanitizeEditorBlockers(independent.blockers), false);
    if (independent.outcome === "conflict") return makeResult(inspected, undefined, "conflict", undefined, [], [], false);
    if (independent.outcome === "offline") return makeResult(inspected, undefined, "offline", undefined, [], [], false);
    return makeResult(inspected, undefined, "unverifiable", undefined, [], [], false);
  }
  const observed = await observeVerifiedRegistrySession(identityOf(instance), session, observationDeadlineMs, verification, processClaims, observer);
  if (observed.outcome === "conflict") return makeResult(inspected, undefined, "conflict", undefined, [], [], false);
  if (observed.outcome === "offline") return makeResult(inspected, undefined, "offline", undefined, [], [], false);
  if (observed.outcome === "unproven") return makeResult(inspected, undefined, "unverifiable", undefined, [], [], false);
  const snapshot = parseSnapshot(session.lastUnitySnapshot, session);
  const progress = sanitizeProgress(session.progress);
  const blockers = [...sanitizeEditorBlockers(session.blockers), ...observed.blockers];
  const status = observed.outcome === "unsupported"
    ? "unresponsive"
    : fallbackStatus(session, progress, blockers);
  return makeResult(inspected, session, status, snapshot, progress, blockers, false);
}

/** 将身份与可响应性终态映射为稳定公开错误；blocked/transitioning 仍由具体业务决定。 */
export function rejectUnavailableEditorState(state: EditorStateResult, operation: string): void {
  if (state.status === "conflict") {
    throw new StructuredToolError("E_INSTANCE_CONFLICT", `Multiple verified Unity Editor processes claim the project selected for ${operation}.`, ["Close duplicate Unity Editor processes and retry after editor.state reports one verified session."], { status: state.status });
  }
  if (state.status === "unverifiable") {
    throw new StructuredToolError("E_INSTANCE_UNVERIFIABLE", `${operation} cannot prove one Unity process identity safely.`, ["Inspect instances list and editor.state; do not retry a prior mutation whose outcome is unknown."], { status: state.status });
  }
  if (state.status === "unresponsive") {
    throw new StructuredToolError("E_UNITY_UNRESPONSIVE", `${operation} cannot proceed because the verified Unity Editor main thread is not responsive.`, ["Inspect editor.state and editor.dialogs before deciding whether to submit a new request."], { status: state.status }, true, 250);
  }
  if (state.status === "offline") {
    throw new StructuredToolError("E_INSTANCE_NOT_RUNNING", `${operation} requires an open Unity Editor.`, ["Open the selected Unity project and retry."], { status: state.status });
  }
}

/**
 * registry fallback 只有在 session PID、project claim 与 start identity 全部一致后才允许 UIAutomation。
 * 任何多 PID、不可验证或 PID 复用迹象都在 observer 调用前 fail closed。
 */
export async function observeVerifiedRegistrySession(
  identity: RegistryIdentity,
  session: RegistrySession,
  deadlineMs: number,
  verification: SessionProcessVerification,
  processClaims: ProjectProcessClaimInspection,
  observer: Pick<EditorBlockerObserver, "observe"> = new EditorBlockerObserver({ inspect: async () => processClaims }),
): Promise<EditorBlockerObservation> {
  if (verification === "dead") return { outcome: "offline", blockers: [] };
  if (verification !== "verified" || processClaims.verification !== "verified") return { outcome: "unproven", blockers: [], reason: "process-claims-unverifiable" };
  if (processClaims.processIds.length === 0) return { outcome: "offline", blockers: [] };
  if (processClaims.processIds.length !== 1) return { outcome: "conflict", blockers: [], reason: "multiple-process-claims" };
  if (processClaims.processIds[0] !== session.processId) return { outcome: "unproven", blockers: [], reason: "session-process-mismatch" };
  const claim = processClaims.processClaims?.find((item) => item.processId === session.processId);
  if (!claim || Date.parse(claim.processStartedAt) !== Date.parse(session.processStartedAt)) return { outcome: "unproven", blockers: [], reason: "session-process-mismatch" };
  return observer.observe(identity, deadlineMs, session);
}

/** Bridge observation inherits this read's one absolute deadline; it never restarts a remaining budget. */
async function probeBridge(context: EditorStateReadContext, observationDeadlineMs: number, session: RegistrySession, selection: InstanceSelection): Promise<{ snapshot: Snapshot; progress: unknown; blockers: unknown } | undefined> {
  if (remaining(observationDeadlineMs) <= 0) return undefined;
  // selector 只在外层执行一次；私有 Symbol 调用字段保留真实 instanceId，不会进入 Bridge body。
  // snapshot是准入独立观察；父级hybrid Tool的业务lease不得进入该private POST。
  const result = await executeBridgePayload(context.requestId, "internal.editor.state.snapshot", {}, { instance: selection.instance.instanceId }, { bridgeExecute: context.bridgeExecute, dataDir: context.dataDir }, undefined, withObservationSelection({ mode: "observeNow", mutation: false, observationAbsoluteDeadlineMs: observationDeadlineMs }, selection));
  if (!result.ok || !result.data || typeof result.data !== "object" || Array.isArray(result.data)) return undefined;
  const data = result.data as Record<string, unknown>;
  const snapshot = parseSnapshot(data.snapshot ?? data, session);
  return snapshot ? { snapshot, progress: data.progress, blockers: data.blockers } : undefined;
}

/** snapshot 必须为完整 v1 单包，禁止混合 listener 或 registry 的字段。 */
function parseSnapshot(value: unknown, session: RegistrySession | undefined): Snapshot | undefined {
  if (!value || typeof value !== "object" || Array.isArray(value)) return undefined;
  const raw = value as Record<string, unknown>;
  if (raw.schemaVersion !== 1 || typeof raw.observedAt !== "string" || !Number.isFinite(Date.parse(raw.observedAt))
    || typeof raw.sourceEditorSessionId !== "string" || !Number.isInteger(raw.sourceListenerGeneration)
    || !raw.fields || typeof raw.fields !== "object" || Array.isArray(raw.fields)
    || !hasExactKeys(raw, ["schemaVersion", "observedAt", "sourceEditorSessionId", "sourceListenerGeneration", "fields"])) return undefined;
  const observedAtMs = Date.parse(raw.observedAt);
  if (observedAtMs > Date.now()) return undefined;
  if (session && (raw.sourceEditorSessionId !== session.editorSessionId || raw.sourceListenerGeneration !== session.listener.generation)) return undefined;
  const fields = raw.fields as Record<string, unknown>;
  if (Object.keys(fields).length !== STATE_FIELDS.length || STATE_FIELDS.some((name) => !validKnownField(fields[name]))) return undefined;
  return raw as Snapshot;
}

function validKnownField(value: unknown): value is { known: boolean; value?: AnyJson } {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const field = value as Record<string, unknown>;
  if (typeof field.known !== "boolean" || !Object.keys(field).every((key) => key === "known" || key === "value")) return false;
  return field.known ? Object.prototype.hasOwnProperty.call(field, "value") : !Object.prototype.hasOwnProperty.call(field, "value");
}
function hasExactKeys(value: Record<string, unknown>, names: readonly string[]): boolean {
  const keys = Object.keys(value);
  return keys.length === names.length && names.every((name) => Object.prototype.hasOwnProperty.call(value, name));
}

function makeResult(instance: UnityInstance, session: RegistrySession | undefined, status: EditorStateResult["status"], snapshot: Snapshot | undefined, progress: EditorStateProgress[], blockers: EditorStateBlocker[], live: boolean): EditorStateResult {
  const now = Date.now();
  const observedAt = snapshot?.observedAt ?? null;
  const observedAtMs = observedAt ? Date.parse(observedAt) : Number.NaN;
  const age = Number.isFinite(observedAtMs) && observedAtMs <= now ? now - observedAtMs : null;
  const freshness: Freshness = live && snapshot && age !== null && age <= FRESH_SNAPSHOT_MS ? "live" : age === null ? "unknown" : age <= FRESH_SNAPSHOT_MS ? "fresh" : "stale";
  const state = Object.fromEntries(STATE_FIELDS.map((field) => {
    const item = snapshot?.fields[field];
    return [field, item?.known ? { known: true, value: safeValue(item.value) } : { known: false }];
  })) as EditorStateResult["state"];
  return {
    identity: identityOf(instance),
    session: { known: !!session, editorSessionId: session?.editorSessionId ?? null },
    status,
    state,
    progress,
    blockers,
    observation: { freshness },
  };
}

function identityOf(instance: UnityInstance): RegistryIdentity {
  return { instanceId: instance.instanceId, canonicalProjectPath: instance.canonicalProjectPath, displayProjectPath: instance.displayProjectPath };
}

function remaining(observationDeadlineMs: number): number { return Math.max(0, observationDeadlineMs - Date.now()); }
function isLiveSnapshot(snapshot: Snapshot): boolean {
  const observedAtMs = Date.parse(snapshot.observedAt);
  return Number.isFinite(observedAtMs) && observedAtMs <= Date.now() && Date.now() - observedAtMs <= FRESH_SNAPSHOT_MS;
}
function observedStatus(snapshot: Snapshot | undefined, progress: EditorStateProgress[], blockers: EditorStateBlocker[]): EditorStateResult["status"] {
  if (blockers.length > 0) return "blocked";
  if (!snapshot || !isLiveSnapshot(snapshot) || progress.length > 0 || snapshotTransitioning(snapshot)) return "transitioning";
  return "ready";
}
function fallbackStatus(session: RegistrySession, progress: EditorStateProgress[], blockers: EditorStateBlocker[]): EditorStateResult["status"] {
  if (session.lifecycle === "blocked" || blockers.length > 0) return "blocked";
  if (session.lifecycle !== "ready" || progress.length > 0) return "transitioning";
  // Registry publication是历史快照；Bridge/main-thread未响应时不能独立证明ready。
  return "unresponsive";
}
function snapshotTransitioning(snapshot: Snapshot): boolean {
  for (const field of ["compile", "import", "build"] as const) if (snapshot.fields[field].known && snapshot.fields[field].value === true) return true;
  return snapshot.fields.playMode.known && snapshot.fields.playMode.value === "transitioning";
}
function safeRecord(value: unknown, parentKey?: string): Record<string, AnyJson> | undefined {
  if (!value || typeof value !== "object" || Array.isArray(value)) return undefined;
  const result: Record<string, AnyJson> = {};
  for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
    if (isSensitiveKey(key, parentKey)) continue;
    if (Array.isArray(child)) result[key] = child.map((item) => safeValue(item, key));
    else result[key] = safeValue(child, key);
  }
  return result;
}
function isSensitiveKey(key: string, parentKey: string | undefined): boolean {
  const normalized = key.replace(/[^a-z0-9]/gi, "").toLowerCase();
  return SENSITIVE_KEYS.has(normalized) || (parentKey?.replace(/[^a-z0-9]/gi, "").toLowerCase() === "listener" && normalized === "id");
}
function safeValue(value: unknown, parentKey?: string): AnyJson {
  if (value === null || typeof value === "string" || typeof value === "boolean") return value;
  if (typeof value === "number") return Number.isFinite(value) ? value : null;
  if (Array.isArray(value)) return value.map((item) => safeValue(item, parentKey));
  return safeRecord(value, parentKey) ?? null;
}
const PROGRESS_SOURCES = new Set(["unity", "registry"]);
function sanitizeProgress(value: unknown): EditorStateProgress[] {
  return Array.isArray(value) ? value.flatMap((item): EditorStateProgress[] => {
    const record = safeRecord(item);
    if (!record || typeof record.kind !== "string" || record.kind.length === 0 || typeof record.title !== "string"
      || typeof record.canWait !== "boolean" || !PROGRESS_SOURCES.has(record.source as string)
      || (record.value !== undefined && record.value !== null && (typeof record.value !== "number" || record.value < 0 || record.value > 1))) return [];
    return [{ kind: record.kind, title: record.title, ...(Object.prototype.hasOwnProperty.call(record, "value") ? { value: record.value as number | null } : {}), canWait: record.canWait, source: record.source as EditorStateProgress["source"] }];
  }) : [];
}
