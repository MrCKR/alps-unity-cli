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
import { defineContext, StructuredContextError, type ContextReadContext } from "../define.js";

export interface EditorStateRequest {}
type Freshness = "live" | "fresh" | "stale" | "unknown";
type ObservedField = { known: boolean; value?: AnyJson; freshness: Freshness };
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
  session: { known: boolean; editorSessionId: string | null; processId: number | null };
  status: "ready" | "transitioning" | "blocked" | "offline" | "conflict";
  state: { compile: ObservedField; import: ObservedField; build: ObservedField; playMode: ObservedField; prefabStage: ObservedField; activeScene: ObservedField; runtime: ObservedField };
  progress: EditorStateProgress[];
  blockers: EditorStateBlocker[];
  observation: { live: boolean; sources: Array<"bridge" | "registry" | "windows-ui" | "process">; windowsUi: "not-observed" | "observed-empty" | "observed-blocked" | "unproven" | "unsupported"; observedAt: string; elapsedMs: number; freshness: Freshness; lastHeartbeatAt: string | null; dataObservedAt: string | null; dataAgeMs: number | null; unknownFields: string[] };
}

const STATE_FIELDS = ["compile", "import", "build", "playMode", "prefabStage", "activeScene", "runtime"] as const;
const FRESH_SNAPSHOT_MS = 10_000;
// Windows 冷启动需在同一上限内完成 Bridge、PID/start 和 canonical project 三重证明。
const OBSERVATION_BUDGET_MS = 10_000;
const SENSITIVE_KEYS = new Set(["endpoint", "endpointurl", "bridgeendpoint", "bridgeendpointurl", "bridgeurl", "port", "actualport", "preferredport", "listenerid", "listenergeneration", "generation", "commandline"]);

export default defineContext<EditorStateRequest, EditorStateResult>({
  name: "editor.state",
  summary: "Read the selected Unity Editor session state, progress, and external blockers without waiting.",
  description: "Reads one selected Unity project identity and its active Editor session immediately. The input must be an empty object. This read-only Context never waits for readiness, refreshes Unity, writes a request journal, or dismisses desktop dialogs. It uses one bounded internal editor snapshot when available, then only a verified versioned registry snapshot and Windows read-only modal observations.",
  category: "editor",
  source: "built-in",
  target: "editor",
  availabilityScope: "always",
  playModeSupport: "supported",
  examples: [{ description: "Read the current selected Editor state.", argv: ["context", "read", "editor.state", "--input", "{}"], input: {} }],
  async read(_request, context) { return readEditorState(context); },
});

/** 聚合路径完全只读，总预算固定；任一 snapshot 只可单独作为状态字段来源。 */
export async function readEditorState(context: ContextReadContext): Promise<EditorStateResult> {
  const startedAt = (context as ContextReadContext & { startedAt?: number }).startedAt ?? Date.now();
  const observationDeadlineMs = Math.min(startedAt + OBSERVATION_BUDGET_MS, context.observationAbsoluteDeadlineMs ?? Number.POSITIVE_INFINITY);
  if (!context.dataDir) throw new StructuredContextError("E_INSTANCE_NOT_FOUND", "Unity CLI data directory is required to resolve the selected Editor instance.", ["Pass --instance <instanceId|projectPath|alias> or run from a Unity project directory."]);
  let selection: InstanceSelection;
  let instance: UnityInstance;
  try {
    selection = await selectObservationInstanceForFlags(context.flags ?? {}, context.dataDir, remaining(observationDeadlineMs));
    instance = selection.instance;
  } catch (error) {
    if (error instanceof InstanceRegistryError || error instanceof ProjectIdentityError) {
      throw new StructuredContextError(error.code, error.message, error instanceof InstanceRegistryError ? error.nextActions : ["Pass --instance <instanceId|canonicalProjectPath|alias> or run from a local Unity project directory."], error.details);
    }
    throw error;
  }
  if (instance.status === "conflict") return makeResult(instance, undefined, "conflict", undefined, [], [], false, ["registry"], startedAt);
  const session = instance.activeSession;
  if (!session) {
    // Bridge 注册前仍以同一 canonical resolver 观察唯一 Unity process；不创建或修复 registry。
    const observed = await new EditorBlockerObserver().observe(identityOf(instance), observationDeadlineMs);
    if (observed.outcome === "observed-blocked") {
      return makeResult(instance, undefined, "blocked", undefined, [], sanitizeEditorBlockers(observed.blockers), false, ["registry", "process", "windows-ui"], startedAt, "observed-blocked");
    }
    if (observed.outcome === "conflict") return makeResult(instance, undefined, "conflict", undefined, [], [], false, ["registry", "process"], startedAt);
    return makeResult(instance, undefined, "offline", undefined, [], [], false, ["registry", ...(observed.outcome === "unsupported" ? [] : ["process" as const]), ...(observed.outcome === "unproven" ? ["windows-ui" as const] : [])], startedAt, observed.outcome === "unproven" ? "unproven" : observed.outcome === "unsupported" ? "unsupported" : "not-observed");
  }

  const fallbackBudgetMs = remaining(observationDeadlineMs);
  if (fallbackBudgetMs <= 0) return makeResult(instance, undefined, "conflict", undefined, [], [], false, ["registry"], startedAt);
  // 正常 Bridge 成功路径不得访问 Windows UI；只有未获证实后才进入一次有界 fallback observer。
  const verificationPromise = verifyRegistrySessionProcess(identityOf(instance), session, fallbackBudgetMs);
  const processClaimsPromise = inspectProjectProcessClaims(identityOf(instance), fallbackBudgetMs);
  const bridge = await probeBridge(context, observationDeadlineMs, session, selection);
  if (bridge && isLiveSnapshot(bridge.snapshot)) {
    const processClaims = await processClaimsPromise;
    const inspected = applyProjectProcessClaimInspection(instance, processClaims);
    if (inspected.status === "conflict") return makeResult(inspected, undefined, "conflict", undefined, [], sanitizeEditorBlockers(inspected.blockers), false, ["registry", "process"], startedAt);
    const progress = sanitizeProgress(bridge.progress);
    const blockers = sanitizeEditorBlockers(bridge.blockers);
    return makeResult(inspected, session, observedStatus(bridge.snapshot, progress, blockers), bridge.snapshot, progress, blockers, true, ["bridge", "registry"], startedAt);
  }

  // Windows UI 读取必须晚于唯一 PID、project path 与 start identity 的完整证明。
  const [verification, processClaims] = await Promise.all([verificationPromise, processClaimsPromise]);
  const inspected = applyProjectProcessClaimInspection(instance, processClaims);
  if (inspected.status === "conflict") return makeResult(inspected, undefined, "conflict", undefined, [], sanitizeEditorBlockers(inspected.blockers), false, ["registry", "process"], startedAt);
  const observed = await observeVerifiedRegistrySession(identityOf(instance), session, observationDeadlineMs, verification, processClaims);
  if (observed.outcome === "conflict") return makeResult(inspected, undefined, "conflict", undefined, [], [], false, ["registry", "process"], startedAt);
  if (observed.outcome === "offline") return makeResult(inspected, undefined, "offline", undefined, [], [], false, ["registry", "process"], startedAt);
  const snapshot = parseSnapshot(session.lastUnitySnapshot, session);
  const progress = sanitizeProgress(session.progress);
  const blockers = [...sanitizeEditorBlockers(session.blockers), ...observed.blockers];
  const windowsSources: EditorStateResult["observation"]["sources"] = observed.outcome === "unsupported" ? [] : ["windows-ui"];
  const windowsUi = observed.outcome === "unsupported" ? "unsupported" : observed.outcome;
  return makeResult(inspected, session, fallbackStatus(session, progress, blockers), snapshot, progress, blockers, false, ["registry", "process", ...windowsSources], startedAt, windowsUi);
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
  if (verification !== "verified" || processClaims.verification !== "verified") return { outcome: "conflict", blockers: [], reason: "process-claims-unverifiable" };
  if (processClaims.processIds.length === 0) return { outcome: "offline", blockers: [] };
  if (processClaims.processIds.length !== 1 || processClaims.processIds[0] !== session.processId) return { outcome: "conflict", blockers: [], reason: "multiple-or-mismatched-process-claims" };
  const claim = processClaims.processClaims?.find((item) => item.processId === session.processId);
  if (!claim || Date.parse(claim.processStartedAt) !== Date.parse(session.processStartedAt)) return { outcome: "conflict", blockers: [], reason: "session-process-mismatch" };
  return observer.observe(identity, deadlineMs, session);
}

/** Bridge observation inherits this read's one absolute deadline; it never restarts a remaining budget. */
async function probeBridge(context: ContextReadContext, observationDeadlineMs: number, session: RegistrySession, selection: InstanceSelection): Promise<{ snapshot: Snapshot; progress: unknown; blockers: unknown } | undefined> {
  if (remaining(observationDeadlineMs) <= 0) return undefined;
  // selector 只在外层执行一次；私有 Symbol 调用字段保留真实 instanceId，不会进入 Bridge body。
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

function makeResult(instance: UnityInstance, session: RegistrySession | undefined, status: EditorStateResult["status"], snapshot: Snapshot | undefined, progress: EditorStateProgress[], blockers: EditorStateBlocker[], live: boolean, sources: EditorStateResult["observation"]["sources"], startedAt: number, windowsUi: EditorStateResult["observation"]["windowsUi"] = "not-observed"): EditorStateResult {
  const now = Date.now();
  const observedAt = snapshot?.observedAt ?? null;
  const observedAtMs = observedAt ? Date.parse(observedAt) : Number.NaN;
  const age = Number.isFinite(observedAtMs) && observedAtMs <= now ? now - observedAtMs : null;
  const freshness: Freshness = live && snapshot && age !== null && age <= FRESH_SNAPSHOT_MS ? "live" : age === null ? "unknown" : age <= FRESH_SNAPSHOT_MS ? "fresh" : "stale";
  const state = Object.fromEntries(STATE_FIELDS.map((field) => {
    const item = snapshot?.fields[field];
    return [field, item?.known ? { known: true, value: safeValue(item.value), freshness } : { known: false, freshness: freshness === "live" ? "unknown" : freshness }];
  })) as EditorStateResult["state"];
  const unknownFields = STATE_FIELDS.filter((field) => !state[field].known);
  return {
    identity: identityOf(instance),
    session: { known: !!session, editorSessionId: session?.editorSessionId ?? null, processId: session?.processId ?? null },
    status,
    state,
    progress,
    blockers,
    observation: { live, sources, windowsUi, observedAt: new Date(now).toISOString(), elapsedMs: now - startedAt, freshness, lastHeartbeatAt: session?.lastHeartbeatAt ?? null, dataObservedAt: observedAt, dataAgeMs: age, unknownFields },
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
  if (session.state === "blocked" || blockers.length > 0) return "blocked";
  // A registry snapshot is historical and cannot independently prove readiness.
  return "transitioning";
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
