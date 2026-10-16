import { createHash } from "node:crypto";
import { executeBridgePayload } from "../../bridge/index.js";
import { withTransitionBinding, type BridgeFlags } from "../../bridge/types.js";
import { defineTool } from "../define.js";
import { waitForEditorTransitionEnd } from "./readiness.js";
import { rejectUnavailableEditorState } from "./state.js";
import { blockedEditorStateError, blockersFromDetails, StructuredToolError } from "../errors.js";

export interface EditorRefreshRequest { compile?: boolean; }
export interface EditorRefreshResult {
  changed: boolean;
  importObserved: boolean;
  compilationTriggered: boolean;
  generation: {
    dependencyBefore: number;
    dependencyAfter: number;
    processedBefore: number;
    processedAfter: number;
  };
  editorState: { status: "ready"; compile: false; import: false; };
}
export interface EditorRefreshDryRunResult {
  dryRun: true;
  plannedEffects: Array<{ type: "asset-refresh" } | { type: "script-compile" }>;
}

type RecordValue = Record<string, unknown>;
type BridgeData = { data?: unknown };

export default defineTool<EditorRefreshRequest, EditorRefreshResult | EditorRefreshDryRunResult>({
  name: "editor.refresh",
  summary: "Refresh the open Unity Editor and return stable import and compile readback.",
  description: "Refreshes the selected Editor once and proves completion with Unity-owned global artifact dependency/processed generations; it never recursively reads Assets, hashes file bytes, or calls per-file dependency hashing. changed reports a generation change, importObserved reports Unity import activity or generation movement, and unchanged/no-import remains an explicit successful result. compile=true observes and reports a compilation cycle when Unity decides refreshed script changes require one; it never forces a second compile. The same execution deadline and cancellation govern dispatch, import/compile stability, reload recovery and terminal readback. Editor-only; may trigger asset refresh, compilation and domain reload.",
  group: "editor",
  target: "editor",
  destructiveHint: false,
  supportsDryRun: true,
  timeoutMs: 120_000,
  playModeSupport: "editor-only",
  sideEffects: ["asset-refresh", "script-compile", "domain-reload"],
  handler: "hybrid",
  supportStatus: "live-bridge",
  examples: [
    { description: "Refresh the current open Editor.", argv: ["tools", "run", "editor.refresh"] },
    { description: "Refresh and observe any Unity-triggered script compilation.", argv: ["tools", "run", "editor.refresh", "--input", "{\"compile\":true}"] },
  ],
  async execute(request, context) {
    if (context.execution.dryRun) {
      return {
        dryRun: true,
        plannedEffects: request.compile
          ? [{ type: "asset-refresh" as const }, { type: "script-compile" as const }]
          : [{ type: "asset-refresh" as const }],
      };
    }

    const deadline = context.absoluteDeadlineMs ?? Date.now() + (context.execution.timeoutMs ?? 30_000);
    const state = await waitForEditorTransitionEnd(context, childId(context.requestId, "_refresh_state"), deadline);
    if (state.status === "blocked") {
      throw blockedEditorStateError("Unity Editor is blocked and cannot refresh assets.", state.blockers);
    }
    rejectUnavailableEditorState(state, "editor.refresh");
    if (!state.session.known || !state.session.editorSessionId) {
      throw new StructuredToolError("E_EDITOR_SESSION_REPLACED", "editor.refresh could not bind one verified Editor session.", ["Inspect editor.state and retry after the session is stable."]);
    }
    if (state.status !== "ready") {
      throw new StructuredToolError("E_UNITY_TRANSITIONING", "editor.refresh requires a stable Editor generation.", ["Wait for editor.state to become ready, then retry."], undefined, true, 250);
    }
    if (state.state.playMode.value !== "edit") {
      throw new StructuredToolError("E_TOOL_PLAYMODE_UNAVAILABLE", "editor.refresh is editor-only and cannot run in Play Mode.", ["Exit Play Mode before running editor.refresh."], { toolName: "editor.refresh", playModeSupport: "editor-only" }, false, 0);
    }

    const flags: BridgeFlags = { ...(context.flags ?? {}), instance: state.identity.instanceId };
    const execute = withTransitionBinding({ mode: "execute", mutation: true, absoluteDeadlineMs: deadline });
    const observe = withTransitionBinding({ mode: "read", mutation: false, absoluteDeadlineMs: deadline });
    let accepted = false;
    let root: RecordValue;

    try {
      root = record((await bridge(context.requestId, "internal.editor.refresh.start", {
        compile: request.compile === true,
        expectedEditorSessionId: state.session.editorSessionId,
      }, flags, context, execute)).data);
      accepted = root.accepted === true || root.terminal === true;
    } catch (error) {
      throw bridgeError(error);
    }

    // 已被 durable root 接受后，任何轮询/transport 失败都不能改写为普通失败。
    if (root.terminal !== true) {
      if (root.terminal !== false || root.accepted !== true) throw invalid();
      const rootRequestDigest = typeof root.rootRequestDigest === "string" && /^[a-f0-9]{64}$/.test(root.rootRequestDigest) ? root.rootRequestDigest : undefined;
      if (!rootRequestDigest) throw invalid();
      accepted = true;
      const statusId = childId(context.requestId, "_refresh_status");
      while (root.terminal !== true) {
        if (Date.now() >= deadline) throw executionTimeout("E_EXECUTION_TIMEOUT");
        await delay(100);
        try {
          root = record((await bridge(statusId, "internal.editor.refresh.status", { rootRequestId: context.requestId, rootRequestDigest }, flags, context, observe, false)).data);
        } catch (error) {
          throw executionTimeoutFrom(error);
        }
      }
    }

    try {
      return terminalResult(root, context.requestId);
    } catch (error) {
      if (accepted && !(error instanceof StructuredToolError && error.code !== "E_TOOL_COMPLETION_PROOF_INVALID")) throw error;
      throw error;
    }
  },
});

async function bridge(requestId: string, command: string, input: RecordValue, flags: BridgeFlags, context: any, invocation: any, includeRootAdmission = true): Promise<BridgeData> {
  // reload 后的只读 status 必须在 replacement listener 上重新绑定，不能复用 start admission。
  const runtime = includeRootAdmission
    ? { dataDir: context.dataDir, rootAdmission: context.rootAdmission, bridgeExecute: context.bridgeExecute }
    : { dataDir: context.dataDir, bridgeExecute: context.bridgeExecute };
  const result = await executeBridgePayload(requestId, command, input, flags, runtime, invocation);
  if (!result.ok) throw StructuredToolError.from(result.error ?? { code: "BRIDGE_CONNECTION_FAILED", message: "Live refresh bridge failed." });
  return { data: result.data };
}

function terminalResult(root: RecordValue, requestId: string): EditorRefreshResult {
  const envelope = record(root.rootEnvelope);
  if (envelope.requestId !== requestId || typeof envelope.ok !== "boolean") throw invalid();
  if (envelope.ok !== true) {
    const error = record(envelope.error);
    throw new StructuredToolError(
      typeof error.code === "string" ? error.code : "E_EXECUTION_TIMEOUT",
      typeof error.message === "string" ? error.message : "Refresh failed without a structured terminal.",
      stringArray(error.nextActions),
      recordOrUndefined(error.details),
      typeof error.transient === "boolean" ? error.transient : undefined,
      typeof error.retryAfterMs === "number" ? error.retryAfterMs : undefined,
    );
  }
  const data = record(envelope.data);
  const editor = record(data.editorState);
  const generation = record(data.generation);
  if (typeof data.changed !== "boolean" || typeof data.importObserved !== "boolean" || typeof data.compilationTriggered !== "boolean"
    || !nonnegativeInteger(generation.dependencyBefore) || !nonnegativeInteger(generation.dependencyAfter)
    || !nonnegativeInteger(generation.processedBefore) || !nonnegativeInteger(generation.processedAfter)
    || editor.status !== "ready" || editor.compile !== false || editor.import !== false) throw invalid();
  return {
    changed: data.changed,
    importObserved: data.importObserved,
    compilationTriggered: data.compilationTriggered,
    generation: {
      dependencyBefore: generation.dependencyBefore,
      dependencyAfter: generation.dependencyAfter,
      processedBefore: generation.processedBefore,
      processedAfter: generation.processedAfter,
    },
    editorState: { status: "ready", compile: false, import: false },
  };
}

function bridgeError(error: unknown): StructuredToolError {
  return error instanceof StructuredToolError
    ? error
    : new StructuredToolError("BRIDGE_CONNECTION_FAILED", "Live refresh bridge failed.");
}

function executionTimeoutFrom(error: unknown): StructuredToolError {
  const cause = bridgeError(error);
  const blockers = blockersFromDetails(cause.details);
  return new StructuredToolError("E_EXECUTION_TIMEOUT", "editor.refresh did not return a terminal result before the request deadline.", ["Inspect editor.state before deciding whether to submit a new request."], {
    causeCode: cause.code,
    ...(blockers ? { blockers } : {}),
  }, false, 0);
}

function executionTimeout(causeCode: string): StructuredToolError {
  return new StructuredToolError("E_EXECUTION_TIMEOUT", "editor.refresh did not return a terminal result before the request deadline.", ["Inspect editor.state before deciding whether to submit a new request."], { causeCode }, false, 0);
}

function invalid(): StructuredToolError {
  return new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", "editor.refresh did not return a stable terminal readback.", ["Inspect UnityCliBridge.log before retrying."]);
}

function record(value: unknown): RecordValue { return value && typeof value === "object" && !Array.isArray(value) ? value as RecordValue : {}; }
function recordOrUndefined(value: unknown): RecordValue | undefined { const result = record(value); return Object.keys(result).length > 0 ? result : undefined; }
function stringArray(value: unknown): string[] { return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : []; }
function nonnegativeInteger(value: unknown): value is number { return typeof value === "number" && Number.isSafeInteger(value) && value >= 0; }
function childId(root: string, suffix: string): string { return root.length + suffix.length <= 200 ? root + suffix : `${root.slice(0, 160)}_${createHash("sha256").update(root).digest("hex").slice(0, 24)}${suffix}`; }
function delay(ms: number): Promise<void> { return new Promise((resolve) => setTimeout(resolve, ms)); }
