import { createHash } from "node:crypto";
import { executeBridgePayload } from "../../bridge/index.js";
import { withTransitionBinding, type BridgeFlags } from "../../bridge/types.js";
import { defineTool } from "../define.js";
import { readEditorState, rejectUnavailableEditorState } from "../editor/state.js";
import { StructuredToolError } from "../errors.js";

export interface TestCancelRequest {}
export interface TestCancelPlannedEffect { type: "test-run-cancel"; rootRequestId: string; runId: string; }
export interface TestCancelResult {
  cancelled: boolean;
  dryRun: boolean;
  rootRequestId: string;
  runId: string;
  platform: "EditMode" | "PlayMode";
  activeRunIds: string[];
  editorSessionId: string;
  plannedEffects: TestCancelPlannedEffect[];
}

export default defineTool<TestCancelRequest, TestCancelResult>({
  name: "test.cancel",
  summary: "Cancel only the exact active test.run operation and prove its Unity Test Runner GUID is inactive.",
  description: "Targets only the test.run coordinator-owned operation in the selected Editor session. Before cancellation, the coordinator requires one running TestJobData GUID that exactly matches the tracked root. It invokes OnRunCancel only on that TestJobData editModeRunner, then returns after the same GUID disappears from active Unity Test Framework runs. It never sends Escape, exits arbitrary Play Mode, or controls an unowned Test Runner operation.",
  group: "test",
  target: "editor",
  destructiveHint: false,
  supportsDryRun: true,
  timeoutMs: 60_000,
  playModeSupport: "supported",
  sideEffects: ["test-run-cancel"],
  handler: "hybrid",
  supportStatus: "live-bridge",
  examples: [
    { description: "Cancel the exact tracked test.run operation.", argv: ["tools", "run", "test.cancel"] },
    { description: "Preview the exact run identity without cancelling it.", argv: ["tools", "run", "test.cancel", "--dry-run"] },
  ],
  async execute(_request, context) {
    const deadline = context.absoluteDeadlineMs ?? Date.now() + (context.execution.timeoutMs ?? 60_000);
    const state = await readEditorState({ requestId: childId(context.requestId, "_cancel_state"), flags: context.flags, dataDir: context.dataDir, rootAdmission: context.rootAdmission, bridgeExecute: context.bridgeExecute, absoluteDeadlineMs: deadline } as never);
    if (state.status === "blocked") throw new StructuredToolError("E_UNITY_BLOCKED", "Unity Editor is blocked and cannot cancel tests through the main thread.", ["Resolve the Editor blocker, then retry."], { blockers: state.blockers });
    rejectUnavailableEditorState(state, "test.cancel");
    if (!state.session.known || !state.session.editorSessionId) throw new StructuredToolError("E_EDITOR_SESSION_REPLACED", "test.cancel could not bind one verified Editor session.", ["Inspect editor.state before retrying."]);
    const flags: BridgeFlags = { ...(context.flags ?? {}), instance: state.identity.instanceId };
    const observe = withTransitionBinding({ mode: "read", mutation: false, absoluteDeadlineMs: deadline });
    const execute = withTransitionBinding({ mode: "execute", mutation: true, absoluteDeadlineMs: deadline });
    if (context.execution.dryRun) {
      const response = await call(context.requestId, "internal.test.run.cancel.plan", { expectedEditorSessionId: state.session.editorSessionId }, flags, context, observe);
      return result(record(response.data), true);
    }

    let accepted = false;
    try {
      const started = record((await call(context.requestId, "internal.test.run.cancel", { expectedEditorSessionId: state.session.editorSessionId }, flags, context, execute)).data);
      if (started.terminal === true) return result(record(started.result), false);
      const rootRequestDigest = string(started.rootRequestDigest);
      if (started.terminal !== false || started.accepted !== true || !string(started.runId) || !string(started.rootRequestId) || !rootRequestDigest || !sha256(rootRequestDigest)) throw invalid();
      accepted = true;
      for (;;) {
        if (Date.now() >= deadline) throw timeout("E_EXECUTION_TIMEOUT");
        const status = record((await call(childId(context.requestId, "_cancel_status"), "internal.test.run.cancel.status", { cancelRequestId: context.requestId, rootRequestDigest }, flags, context, observe)).data);
        if (status.terminal === true) return result(record(status.result), false);
        if (status.terminal !== false || status.accepted !== true || status.runId !== started.runId || status.rootRequestId !== started.rootRequestId) throw invalid();
        await delay(100);
      }
    } catch (error) {
      if (!accepted || error instanceof StructuredToolError && error.code === "E_EXECUTION_TIMEOUT") throw error;
      throw timeout(error instanceof StructuredToolError ? error.code : "BRIDGE_CONNECTION_FAILED");
    }
  },
});

async function call(requestId: string, command: string, input: Record<string, unknown>, flags: BridgeFlags, context: any, invocation: any) {
  const response = await executeBridgePayload(requestId, command, input, flags, { dataDir: context.dataDir, rootAdmission: context.rootAdmission, bridgeExecute: context.bridgeExecute }, undefined, invocation);
  if (!response.ok) throw StructuredToolError.from(response.error ?? { code: "BRIDGE_CONNECTION_FAILED", message: "Live test cancellation bridge failed." });
  return response;
}
function result(value: Record<string, unknown>, dryRun: boolean): TestCancelResult {
  const runId = string(value.runId);
  const rootRequestId = string(value.rootRequestId);
  const session = string(value.editorSessionId);
  const active = Array.isArray(value.activeRunIds) ? value.activeRunIds.filter((item): item is string => typeof item === "string") : undefined;
  const effects = Array.isArray(value.plannedEffects) ? value.plannedEffects : undefined;
  const platform = value.platform;
  if (!runId || !guid(runId) || !rootRequestId || !session || !active || active.length !== (value.activeRunIds as unknown[]).length || !effects || effects.length !== 1 || platform !== "EditMode" && platform !== "PlayMode" || value.dryRun !== dryRun || value.cancelled !== !dryRun) throw invalid();
  const effect = record(effects[0]);
  if (effect.type !== "test-run-cancel" || effect.runId !== runId || effect.rootRequestId !== rootRequestId || !dryRun && active.includes(runId)) throw invalid();
  return value as unknown as TestCancelResult;
}
function invalid(): StructuredToolError { return new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", "test.cancel did not return an exact inactive Test Runner GUID proof.", ["Inspect UnityCliBridge.log before retrying."]); }
function timeout(causeCode: string): StructuredToolError { return new StructuredToolError("E_EXECUTION_TIMEOUT", "test.cancel did not reach a proven terminal before the deadline.", ["Inspect test.run and editor.state before deciding whether to retry."], { causeCode }, false, 0); }
function record(value: unknown): Record<string, unknown> { return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : {}; }
function string(value: unknown): string | undefined { return typeof value === "string" && value.length > 0 ? value : undefined; }
function guid(value: string): boolean { return /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value); }
function sha256(value: string): boolean { return /^[0-9a-f]{64}$/.test(value); }
function childId(root: string, suffix: string): string { return root.length + suffix.length <= 200 ? root + suffix : `${root.slice(0, 160)}_${createHash("sha256").update(root).digest("hex").slice(0, 24)}${suffix}`; }
function delay(ms: number): Promise<void> { return new Promise(resolve => setTimeout(resolve, ms)); }
