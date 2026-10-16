/** 编辑器 blocker 的公开投影；任何 transport 或窗口内部标识都不得越过这里。 */
import { inspectProjectProcessClaims, verifyRegistrySessionProcess, type IdentityEvidenceReason, type ProjectProcessClaimInspection, type RegistryIdentity, type RegistrySession, type SessionProcessVerification } from "./instances.js";
import { probeWindowsEditorState, type WindowsEditorStateProbe } from "./windows-editor-state.js";
import { dialogId, normalizeDialogButton } from "./editor-dialogs.js";

/** 独立观察到的 Windows modal；唯一来源是 Windows UI，requiresUserAction 为 true。 */
export interface EditorDialogBlocker {
  type: "dialog";
  dialogId: string;
  title: string;
  message: string;
  buttons: string[];
  requiresUserAction: true;
  source: "windows-ui";
  confidence: "exact";
}

/**
 * 由当前 live pause publication 证明的故意主线程暂停。
 * 它不是 Windows 观察结果，也不要求用户操作：解除路径是 debug.pause.resume。
 */
export interface EditorDebugPauseBlocker {
  type: "debug-pause";
  pauseId: string;
  threadId: number;
  mainThread: boolean;
  methodIdentity: string;
  targetKey: string;
  position: "prefix" | "postfix";
  sessionId: string;
  timeoutKind: "bounded" | "unbounded";
  remainingMs: number;
  pausedForMs: number;
  resumeTool: "debug.pause.resume";
  infoTool: "debug.pause.list";
  requiresUserAction: false;
  source: "unity-debug-pause";
  confidence: "exact";
}

export type EditorBlocker = EditorDialogBlocker | EditorDebugPauseBlocker;

const DEBUG_PAUSE_POSITIONS = new Set(["prefix", "postfix"]);
const DEBUG_PAUSE_TIMEOUT_KINDS = new Set(["bounded", "unbounded"]);

/** 公开 blocker 的稳定可读标签；只读取两个精确 DTO 的公开字段。 */
export function editorBlockerLabel(blocker: EditorBlocker): string {
  return blocker.type === "dialog" ? blocker.title : blocker.pauseId;
}

/** 独立观察到的 modal 摘要；Windows 观察只会产生这一种 blocker。 */
export function editorDialogBlockers(blockers: readonly EditorBlocker[]): EditorDialogBlocker[] {
  return blockers.filter((blocker): blocker is EditorDialogBlocker => blocker.type === "dialog");
}

/** 唯一 sanitizer：只投影 blocker DTO 的允许字段，递归输入永不直接公开。 */
export function sanitizeEditorBlockers(value: unknown): EditorBlocker[] {
  if (!Array.isArray(value)) return [];
  return value.flatMap((item): EditorBlocker[] => {
    if (!item || typeof item !== "object" || Array.isArray(item)) return [];
    const record = item as Record<string, unknown>;
    if (record.type === "dialog") return sanitizeDialogBlocker(record);
    if (record.type === "debug-pause") return sanitizeDebugPauseBlocker(record);
    return [];
  });
}

/** dialog 分支：内部窗口/传输字段（如 hwnd、endpoint）一律丢弃。 */
function sanitizeDialogBlocker(record: Record<string, unknown>): EditorDialogBlocker[] {
  if (typeof record.dialogId !== "string" || typeof record.title !== "string" || typeof record.message !== "string" || !Array.isArray(record.buttons) || !record.buttons.every((button) => typeof button === "string") || record.requiresUserAction !== true || record.source !== "windows-ui" || record.confidence !== "exact") return [];
  return [{ type: "dialog", dialogId: record.dialogId, title: record.title, message: record.message, buttons: record.buttons as string[], requiresUserAction: true, source: "windows-ui", confidence: "exact" }];
}

/** debug-pause 分支：只接受由 live pause publication 发布的字段，缺失或多一项都拒绝。 */
function sanitizeDebugPauseBlocker(record: Record<string, unknown>): EditorDebugPauseBlocker[] {
  const allowed = ["type", "pauseId", "threadId", "mainThread", "methodIdentity", "targetKey", "position", "sessionId", "timeoutKind", "remainingMs", "pausedForMs", "resumeTool", "infoTool", "requiresUserAction", "source", "confidence"];
  if (Object.keys(record).some((key) => !allowed.includes(key))) return [];
  if (typeof record.pauseId !== "string" || record.pauseId.length === 0) return [];
  if (!Number.isInteger(record.threadId) || (record.threadId as number) < 1) return [];
  if (typeof record.mainThread !== "boolean") return [];
  if (typeof record.methodIdentity !== "string" || typeof record.targetKey !== "string") return [];
  if (typeof record.position !== "string" || !DEBUG_PAUSE_POSITIONS.has(record.position)) return [];
  if (typeof record.sessionId !== "string") return [];
  if (typeof record.timeoutKind !== "string" || !DEBUG_PAUSE_TIMEOUT_KINDS.has(record.timeoutKind)) return [];
  if (!Number.isFinite(record.remainingMs) || (record.remainingMs as number) < 0 || !Number.isFinite(record.pausedForMs) || (record.pausedForMs as number) < 0) return [];
  if (record.resumeTool !== "debug.pause.resume" || record.infoTool !== "debug.pause.list") return [];
  if (record.requiresUserAction !== false) return [];
  if (record.source !== "unity-debug-pause" || record.confidence !== "exact") return [];
  return [{
    type: "debug-pause",
    pauseId: record.pauseId,
    threadId: record.threadId as number,
    mainThread: record.mainThread,
    methodIdentity: record.methodIdentity,
    targetKey: record.targetKey,
    position: record.position as EditorDebugPauseBlocker["position"],
    sessionId: record.sessionId,
    timeoutKind: record.timeoutKind as EditorDebugPauseBlocker["timeoutKind"],
    remainingMs: record.remainingMs as number,
    pausedForMs: record.pausedForMs as number,
    resumeTool: "debug.pause.resume",
    infoTool: "debug.pause.list",
    requiresUserAction: false,
    source: "unity-debug-pause",
    confidence: "exact",
  }];
}
/** Windows observation 只把可见 modal 转换为公开 blocker，绝不泄露 handle/PID。 */
export function windowBlockers(value: Array<{ modal: boolean; hwnd: string; title: string; message: string; buttons: Array<{ label: string }> }>, processId: number, processStartedAt: string): EditorDialogBlocker[] {
  return value.filter((window) => window.modal).map((window) => ({ type: "dialog", dialogId: dialogId(processId, processStartedAt, window.hwnd), title: window.title, message: window.message, buttons: window.buttons.map((button) => normalizeDialogButton(button.label)), requiresUserAction: true, source: "windows-ui", confidence: "exact" }));
}
export type EditorBlockerObservationOutcome = "observed-empty" | "observed-blocked" | "unproven" | "conflict" | "unsupported" | "offline";
export interface EditorBlockerObservation { outcome: EditorBlockerObservationOutcome; blockers: EditorBlocker[]; processId?: number; processStartedAt?: string; reason?: string; evidenceReason?: IdentityEvidenceReason; }
export interface EditorBlockerObserverDependencies {
  inspect(identity: RegistryIdentity, timeoutMs: number): Promise<ProjectProcessClaimInspection>;
  verifySession(identity: RegistryIdentity, session: Pick<RegistrySession, "processId" | "processStartedAt">, timeoutMs: number): Promise<SessionProcessVerification>;
  probe(processId: number, timeoutMs: number): Promise<WindowsEditorStateProbe>;
  now(): number;
}
/** canonical project identity 的唯一有界 blocker observer。 */
export class EditorBlockerObserver {
  private readonly dependencies: EditorBlockerObserverDependencies;
  public constructor(dependencies: Partial<EditorBlockerObserverDependencies> = {}) {
    this.dependencies = {
      inspect: dependencies.inspect ?? inspectProjectProcessClaims,
      verifySession: dependencies.verifySession ?? ((identity, session, timeoutMs) => verifyRegistrySessionProcess(identity, { ...session, target: "editor" }, timeoutMs)),
      probe: dependencies.probe ?? ((processId, timeoutMs) => probeWindowsEditorState(processId, { timeoutMs })),
      now: dependencies.now ?? Date.now,
    };
  }
  /** deadline 由调用者冻结；只在唯一可归因 PID 上读取 Windows UI。 */
  public async observe(identity: RegistryIdentity, deadlineMs: number, session?: Pick<RegistrySession, "processId" | "processStartedAt">): Promise<EditorBlockerObservation> {
    const remaining = Math.max(0, deadlineMs - this.dependencies.now()); if (remaining < 1) return { outcome: "unproven", blockers: [], reason: "deadline", evidenceReason: "timeout" };
    const claims = await this.dependencies.inspect(identity, remaining);
    if (claims.verification === "unsupported") return { outcome: "unsupported", blockers: [] };
    if (claims.verification !== "verified") return { outcome: "unproven", blockers: [], reason: "process-claims-unverifiable", evidenceReason: claims.reason ?? "unavailable" };
    let claim: { processId: number; processStartedAt: string };
    if (claims.processIds.length === 0) {
      if (!session) return { outcome: "offline", blockers: [] };
      // 命令行缺少项目参数不是退出证据；独立证明原 PID/start 后才能观察它的窗口。
      const verificationBudget = deadlineMs - this.dependencies.now();
      if (verificationBudget < 1) return { outcome: "unproven", blockers: [], reason: "deadline", evidenceReason: "timeout" };
      const verification = await this.dependencies.verifySession(identity, session, verificationBudget);
      if (verification === "dead") return { outcome: "offline", blockers: [] };
      if (verification !== "verified") return { outcome: "unproven", blockers: [], reason: "session-process-unverifiable", evidenceReason: "unavailable" };
      claim = session;
    } else {
      if (claims.processIds.length !== 1) return { outcome: "conflict", blockers: [], reason: "multiple-process-claims" };
      const processId = claims.processIds[0];
      const matchingClaim = claims.processClaims?.find((item) => item.processId === processId);
      if (!matchingClaim) return { outcome: "unproven", blockers: [], processId, reason: "process-start-unproven", evidenceReason: "unavailable" };
      if (session && (session.processId !== processId || Date.parse(session.processStartedAt) !== Date.parse(matchingClaim.processStartedAt))) return { outcome: "unproven", blockers: [], reason: "session-process-mismatch", evidenceReason: "mismatch" };
      claim = matchingClaim;
    }
    const processId = claim.processId;
    const probeBudget = deadlineMs - this.dependencies.now();
    if (probeBudget < 1) return { outcome: "unproven", blockers: [], reason: "deadline", evidenceReason: "timeout" };
    const probe = await this.dependencies.probe(processId, probeBudget); if (probe.outcome === "unsupported") return { outcome: "unsupported", blockers: [], processId, processStartedAt: claim.processStartedAt }; if (probe.outcome === "unproven") return { outcome: "unproven", blockers: [], processId, processStartedAt: claim.processStartedAt, evidenceReason: "unavailable", ...(probe.reason ? { reason: probe.reason } : {}) };
    const blockers = probe.outcome === "observed-blocked" ? windowBlockers(probe.windows, processId, claim.processStartedAt) : [];
    return { outcome: blockers.length > 0 ? "observed-blocked" : "observed-empty", blockers, processId, processStartedAt: claim.processStartedAt };
  }
}
