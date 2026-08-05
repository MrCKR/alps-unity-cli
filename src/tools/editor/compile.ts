import { createHash } from "node:crypto";
import { executeBridgePayload } from "../../bridge/index.js";
import { withTransitionBinding } from "../../bridge/types.js";
import type { EditorStateResult } from "../../contexts/editor/state.js";
import { defineTool, type ToolContext } from "../define.js";
import { waitForEditorTransitionEnd } from "./readiness.js";
import { blockerNextActions, blockerTimeoutMessage, blockersFromDetails, StructuredToolError } from "../errors.js";
import type { ToolError } from "../../protocol/envelope.js";

export interface EditorCompileRequest {
  refresh?: boolean;
  /**
   * Unity 资产路径不能为空。
   * @minLength 1
   */
  path?: string;
  forceUpdate?: boolean;
  importRecursive?: boolean;
  includeWarnings?: boolean;
}

export interface EditorCompileDiagnostic {
  assembly: string;
  severity: "error" | "warning" | "info";
  code: string | null;
  message: string;
  file: string;
  line: number;
  column: number;
}

export interface EditorCompileSummary {
  completed: boolean;
  refresh: boolean;
  path: string | null;
  startedAt: string;
  finishedAt: string;
  durationMs: number;
  assemblyCount: number;
  errorCount: number;
  warningCount: number;
  status: "clean" | "warnings";
}

export interface EditorCompileEditorState {
  status: "ready";
  compile: { known: true; value: false };
  import: { known: true; value: false };
  playMode: { known: true; value: "play" | "edit" };
}

export interface EditorCompileTerminalResult {
  compile: EditorCompileSummary;
  diagnostics: EditorCompileDiagnostic[];
  editorState: EditorCompileEditorState;
  plannedEffects: EditorCompilePlannedEffect[];
}

export interface EditorCompileDryRunResult {
  dryRun: true;
  plannedEffects: EditorCompilePlannedEffect[];
}

export interface EditorCompilePlannedEffect {
  type: "asset-refresh" | "asset-import" | "script-compile" | "domain-reload";
  path?: string;
}

export type EditorCompileResult = EditorCompileTerminalResult | EditorCompileDryRunResult;

type RecordValue = Record<string, unknown>;
type RootEnvelope = { ok: boolean; requestId: string; data?: RecordValue; error?: ToolError<RecordValue> };

export default defineTool<EditorCompileRequest, EditorCompileResult>({
  name: "editor.compile",
  summary: "Refresh and compile the live Unity Editor, returning terminal diagnostics.",
  description: "Starts Unity's durable compile coordinator and polls its private terminal status under one immutable deadline. This editor-only operation may refresh assets, compile scripts, and reload the domain.",
  group: "editor",
  target: "editor",
  destructiveHint: false,
  supportsDryRun: true,
  timeoutMs: 120_000,
  playModeSupport: "editor-only",
  sideEffects: ["asset-refresh", "script-compile", "domain-reload"],
  handler: "hybrid",
  supportStatus: "live-bridge",
  examples: [{ description: "Refresh and compile the current Editor.", argv: ["tools", "run", "editor.compile"] }],
  async execute(request, context) {
    if (request.path !== undefined && request.path.length === 0) throw new StructuredToolError("E_SCHEMA_INVALID", "editor.compile path must be a non-empty Unity asset path.", ["Omit path to refresh the project, or pass an existing Assets-relative path."]);
    const plannedEffects = compilePlannedEffects(request);
    if (context.execution.dryRun) return { dryRun: true, plannedEffects };

    const absoluteDeadlineMs = context.absoluteDeadlineMs ?? Date.now() + (context.execution.timeoutMs ?? 30_000);
    if (Date.now() >= absoluteDeadlineMs) throw compileTimeout();
    let state: EditorStateResult;
    try {
      state = await waitForEditorTransitionEnd(context, compileChildRequestId(context.requestId, "_compile_state"), absoluteDeadlineMs);
    } catch (error) {
      if (Date.now() >= absoluteDeadlineMs) throw compileTimeout();
      throw error;
    }
    if (Date.now() >= absoluteDeadlineMs) throw compileTimeout();
    if (state.status === "blocked") throw new StructuredToolError("E_UNITY_BLOCKED", "Unity Editor is blocked and cannot start compilation.", ["Resolve the Editor blocker, then retry."], { blockers: state.blockers });
    if (state.status === "conflict") throw new StructuredToolError("E_INSTANCE_CONFLICT", "Multiple Unity Editor sessions claim the selected project.", ["Close every duplicate Editor process except the intended session."], { status: state.status });
    if (state.status === "offline") throw new StructuredToolError("E_INSTANCE_NOT_RUNNING", "The selected Unity project has no live Editor session.", ["Open the selected Unity project and retry."], { status: state.status });
    if (isPlaying(state)) throw new StructuredToolError("E_UNITY_COMPILE_REQUIRES_EDIT_MODE", "editor.compile requires Edit Mode.", ["Run unity_cli tools run editor.stop, then retry editor.compile."], { playMode: state.state.playMode.value }, false, 0);
    if (!state.session.known || !state.session.editorSessionId) throw new StructuredToolError("E_EDITOR_SESSION_REPLACED", "editor.compile could not bind a verified Editor session.", ["Inspect editor.state and retry after the session identity is known."]);

    const boundContext: ToolContext = { ...context, flags: { ...(context.flags ?? {}), instance: state.identity.instanceId } };
    const startInvocation = withTransitionBinding({ mode: "execute", mutation: true, absoluteDeadlineMs });
    const statusInvocation = withTransitionBinding({ mode: "read", mutation: false, absoluteDeadlineMs });
    const input = { refresh: request.refresh ?? true, ...(request.path ? { path: request.path } : {}), ...(request.forceUpdate !== undefined ? { forceUpdate: request.forceUpdate } : {}), ...(request.importRecursive !== undefined ? { importRecursive: request.importRecursive } : {}), includeWarnings: request.includeWarnings ?? true, expectedEditorSessionId: state.session.editorSessionId };
    let start: { data?: unknown };
    try {
      start = await bridge(context.requestId, "internal.editor.compile.start", input, boundContext, startInvocation);
    } catch (error) {
      if (error instanceof StructuredToolError && error.details && containsPrivateCompileField(error.details)) throw new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", "Compile root error exposed private operation data.", ["Inspect UnityCliBridge.log before submitting a new compile request."]);
      throw error;
    }
    const startData = record(start.data);
    if (startData.terminal === true) {
      const directTerminal = rootEnvelope(startData.rootEnvelope, context.requestId);
      if (!directTerminal) throw new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", "Compile start returned an invalid recovered root terminal.", ["Inspect UnityCliBridge.log before submitting a new compile request."]);
      return terminalResult(directTerminal);
    }
    if (startData.terminal !== false || startData.accepted !== true) throw new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", "Compile start returned neither acceptance nor a root terminal.", ["Inspect UnityCliBridge.log before submitting a new compile request."]);

    const statusId = compileChildRequestId(context.requestId, "_compile_status");
    for (;;) {
      if (Date.now() >= absoluteDeadlineMs) throw compileTimeout();
      await delay(Math.min(100, Math.max(1, absoluteDeadlineMs - Date.now())));
      let status: { data?: unknown };
      try {
        status = await bridge(statusId, "internal.editor.compile.status", { rootRequestId: context.requestId }, boundContext, statusInvocation);
      } catch (error) {
        const cause = error instanceof StructuredToolError ? error : undefined;
        throw compileTimeout(cause?.code, cause?.details);
      }
      const data = record(status.data);
      if (data.terminal !== true) continue;
      const envelope = rootEnvelope(data.rootEnvelope, context.requestId);
      if (!envelope) throw new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", "Compile status returned an invalid root terminal envelope.", ["Inspect UnityCliBridge.log and retry after the selected session is stable."]);
      return terminalResult(envelope);
    }
  },
});

async function bridge(requestId: string, command: string, input: RecordValue, context: ToolContext, invocation: ReturnType<typeof withTransitionBinding>): Promise<{ data?: unknown }> {
  const result = await executeBridgePayload(requestId, command, input, context.flags ?? {}, { dataDir: context.dataDir, bridgeExecute: context.bridgeExecute }, undefined, invocation);
  if (!result.ok) throw StructuredToolError.from(result.error ?? { code: "BRIDGE_CONNECTION_FAILED", message: "Bridge command failed." });
  return { data: result.data };
}

function terminalResult(envelope: RootEnvelope): EditorCompileTerminalResult {
  if (!envelope.ok) {
    const error = envelope.error;
    if ((error?.details && containsPrivateCompileField(error.details)) || containsPrivateCompileText(error?.message) || error?.nextActions?.some(containsPrivateCompileText)) throw new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", "Compile terminal error exposed private operation data.", ["Inspect UnityCliBridge.log before submitting a new compile request."]);
    throw new StructuredToolError(error?.code ?? "E_TOOL_COMPLETION_PROOF_INVALID", error?.message ?? "Compile failed without a structured root error.", error?.nextActions ?? [], error?.details, error?.transient, error?.retryAfterMs);
  }
  const data = record(envelope.data);
  const compile = record(data.compile);
  const errorCount = nonnegativeInteger(compile.errorCount);
  const warningCount = nonnegativeInteger(compile.warningCount);
  const durationMs = nonnegativeInteger(compile.durationMs);
  const assemblyCount = nonnegativeInteger(compile.assemblyCount);
  const status = compile.status;
  if (compile.completed !== true || errorCount !== 0 || warningCount === undefined || durationMs === undefined || assemblyCount === undefined
    || (status !== "clean" && status !== "warnings") || (status === "clean" && warningCount !== 0) || (status === "warnings" && warningCount === 0)) {
    throw new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", "Compile terminal did not prove a clean or warnings-only completion.", ["Inspect returned compiler diagnostics and fix errors before retrying."]);
  }
  const diagnostics = normalizeDiagnostics(data.diagnostics);
  const editorState = normalizeEditorState(record(data.editorState));
  const startedAt = timestamp(compile.startedAt);
  const finishedAt = timestamp(compile.finishedAt);
  if (!diagnostics || !editorState || typeof compile.refresh !== "boolean" || (compile.path !== null && typeof compile.path !== "string") || !startedAt || !finishedAt || Date.parse(finishedAt) < Date.parse(startedAt)) {
    throw new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", "Compile terminal has an invalid business proof shape.", ["Inspect UnityCliBridge.log and retry after the selected session is stable."]);
  }
  const plannedEffects = normalizePlannedEffects(data.plannedEffects, compile.path, compile.refresh);
  if (!plannedEffects) throw new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", "Compile terminal planned effects do not match its completed operation.", ["Inspect UnityCliBridge.log before submitting a new compile request."]);
  return { compile: { completed: true, refresh: compile.refresh, path: compile.path, startedAt, finishedAt, durationMs, assemblyCount, errorCount, warningCount, status }, diagnostics, editorState, plannedEffects };
}

function rootEnvelope(value: unknown, requestId: string): RootEnvelope | undefined {
  const candidate = record(value);
  if (candidate.requestId !== requestId || typeof candidate.ok !== "boolean") return undefined;
  if (candidate.ok) return { ok: true, requestId, data: record(candidate.data) };
  const error = record(candidate.error);
  if (typeof error.code !== "string" || typeof error.message !== "string") return undefined;
  const details = error.details && typeof error.details === "object" && !Array.isArray(error.details) ? record(error.details) : undefined;
  return { ok: false, requestId, error: { code: error.code, message: error.message, nextActions: Array.isArray(error.nextActions) ? error.nextActions.filter((item): item is string => typeof item === "string") : [], ...(details ? { details } : {}), transient: error.transient === true, retryAfterMs: nonnegativeInteger(error.retryAfterMs) ?? 0 } };
}

function normalizeDiagnostics(value: unknown): EditorCompileDiagnostic[] | undefined {
  if (!Array.isArray(value)) return undefined;
  const result: EditorCompileDiagnostic[] = [];
  for (const raw of value) {
    const item = record(raw);
    const line = nonnegativeInteger(item.line);
    const column = nonnegativeInteger(item.column);
    if (typeof item.assembly !== "string" || item.assembly.length === 0 || (item.severity !== "error" && item.severity !== "warning" && item.severity !== "info")
      || (item.code !== null && typeof item.code !== "string") || typeof item.message !== "string" || item.message.length === 0 || typeof item.file !== "string"
      || line === undefined || column === undefined) return undefined;
    result.push({ assembly: item.assembly, severity: item.severity, code: item.code, message: item.message, file: item.file, line, column });
  }
  return result;
}
function normalizeEditorState(value: RecordValue): EditorCompileEditorState | undefined {
  if (value.status !== "ready") return undefined;
  const compile = record(value.compile);
  const imported = record(value.import);
  const playMode = record(value.playMode);
  if (compile.known !== true || compile.value !== false || imported.known !== true || imported.value !== false
    || playMode.known !== true || (playMode.value !== "play" && playMode.value !== "edit")) return undefined;
  return { status: "ready", compile: { known: true, value: false }, import: { known: true, value: false }, playMode: { known: true, value: playMode.value } };
}
function normalizePlannedEffects(value: unknown, path: unknown, refresh: boolean): EditorCompilePlannedEffect[] | undefined {
  if (!Array.isArray(value)) return undefined;
  const expected: EditorCompilePlannedEffect[] = [];
  if (typeof path === "string" && path.length > 0) expected.push({ type: "asset-import", path });
  else if (refresh) expected.push({ type: "asset-refresh" });
  expected.push({ type: "script-compile" }, { type: "domain-reload" });
  if (value.length !== expected.length) return undefined;
  for (let index = 0; index < expected.length; index += 1) {
    const actual = record(value[index]);
    const wanted = expected[index];
    const keys = Object.keys(actual).sort();
    const expectedKeys = Object.keys(wanted).sort();
    if (JSON.stringify(keys) !== JSON.stringify(expectedKeys) || actual.type !== wanted.type || actual.path !== wanted.path) return undefined;
  }
  return expected;
}
function isPlaying(state: EditorStateResult): boolean { const value = state.state.playMode.value; return value === "play" || value === "playing" || value === "transitioning" || (record(value).isPlaying === true) || (record(value).isPlayingOrWillChangePlaymode === true); }
function compilePlannedEffects(request: EditorCompileRequest): EditorCompilePlannedEffect[] {
  const effects: EditorCompilePlannedEffect[] = [];
  if (request.path) effects.push({ type: "asset-import", path: request.path });
  else if (request.refresh !== false) effects.push({ type: "asset-refresh" });
  effects.push({ type: "script-compile" }, { type: "domain-reload" });
  return effects;
}
/** 长 root 使用完整内容 hash 派生 child ID，避免共享前缀的并发请求碰撞。 */
export function compileChildRequestId(root: string, suffix: string): string { if (root.length + suffix.length <= 200) return `${root}${suffix}`; const digest = createHash("sha256").update(root, "utf8").digest("hex").slice(0, 24); return `${root.slice(0, 160)}_${digest}${suffix}`; }
function compileTimeout(causeCode?: string, details?: RecordValue): StructuredToolError {
  const blockers = blockersFromDetails(details);
  return new StructuredToolError("E_EXECUTION_TIMEOUT", blockerTimeoutMessage("editor.compile", blockers), blockers ? blockerNextActions() : ["Inspect editor.state and the Unity compiler state before deciding whether to submit a new request."], { ...(causeCode ? { causeCode } : {}), ...(blockers ? { blockers } : {}) }, false, 0);
}
function containsPrivateCompileField(value: unknown): boolean {
  if (!value || typeof value !== "object") return false;
  if (Array.isArray(value)) return value.some(containsPrivateCompileField);
  const forbidden = new Set(["accepted", "compileoperation", "endpoint", "generation", "journal", "lease", "listenergeneration", "listenerid", "phase", "port", "requestdigest", "rootenvelope", "runid", "sequence", "terminalenvelope"]);
  return Object.entries(value as RecordValue).some(([key, child]) => forbidden.has(key.replace(/[^a-z0-9]/gi, "").toLowerCase()) || containsPrivateCompileField(child));
}
function containsPrivateCompileText(value: unknown): boolean {
  return typeof value === "string" && /(?:compileOperation|listener(?:Id|Generation)|requestDigest|rootEnvelope|terminalEnvelope|\bjournal\b|\brunId\b|\blease\b|\bendpoint\b|\bport\b|127\.0\.0\.1|[A-Za-z]:\\|\/(?:Users|home|tmp|var|private)\/)/i.test(value);
}
function record(value: unknown): RecordValue { return value && typeof value === "object" && !Array.isArray(value) ? value as RecordValue : {}; }
function nonnegativeInteger(value: unknown): number | undefined { return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 ? value : undefined; }
function timestamp(value: unknown): string | undefined { return typeof value === "string" && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{3,7})?(?:Z|[+-]\d{2}:\d{2})$/.test(value) && Number.isFinite(Date.parse(value)) ? value : undefined; }
function delay(ms: number): Promise<void> { return new Promise((resolve) => setTimeout(resolve, ms)); }
