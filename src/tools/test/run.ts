import { createHash } from "node:crypto";
import { executeBridgePayload } from "../../bridge/index.js";
import { withTransitionBinding, type BridgeFlags } from "../../bridge/types.js";
import { defineTool } from "../define.js";
import { waitForEditorTransitionEnd } from "../editor/readiness.js";
import { rejectUnavailableEditorState } from "../editor/state.js";
import { blockerNextActions, blockerTimeoutMessage, blockersFromDetails, StructuredToolError } from "../errors.js";

export interface TestRunRequest { platform?: "EditMode" | "PlayMode"; testNames?: string[]; groupNames?: string[]; categoryNames?: string[]; assemblyNames?: string[]; xml?: boolean; }
export interface TestFailure { fullName: string; message: string; stackTrace: string; duration: number; }
export interface TestXmlArtifact { path: string; kind: "nunit-xml"; hash: string; size: number; }
export interface TestRunSummary { platform: "EditMode" | "PlayMode"; status: "passed" | "failed"; testCount: number; passed: number; failed: number; skipped: number; inconclusive: number; duration: number; startedAt: string; finishedAt: string; }
export interface TestRunResult { summary: TestRunSummary; failures: TestFailure[]; xml?: TestXmlArtifact; }

export default defineTool<TestRunRequest, TestRunResult>({
  name: "test.run", summary: "Run tests in the open Unity Editor and return the final test result.",
  description: "Runs EditMode or PlayMode tests through the open Unity Editor Test Runner. The same requestId resumes a completed callback terminal after a lost response; a running test run is never replayed.",
  group: "test", target: "editor", destructiveHint: false, timeoutMs: 600_000, sideEffects: ["session-affecting"], playModeSupport: "session-affecting", handler: "hybrid", supportStatus: "live-bridge",
  examples: [{ description: "Run EditMode tests in the open Editor.", argv: ["tools", "run", "test.run", "--input", "{\"platform\":\"EditMode\"}"] }],
  async execute(request, context) {
    const deadline = context.absoluteDeadlineMs ?? Date.now() + (context.execution.timeoutMs ?? 30_000);
    const state = await waitForEditorTransitionEnd(context, childId(context.requestId, "_test_state"), deadline);
    if (state.status === "blocked") throw new StructuredToolError("E_UNITY_BLOCKED", "Unity Editor is blocked and cannot start tests.", ["Resolve the Editor blocker, then retry."], { blockers: state.blockers });
    rejectUnavailableEditorState(state, "test.run");
    if (!state.session.known || !state.session.editorSessionId) throw new StructuredToolError("E_EDITOR_SESSION_REPLACED", "test.run could not bind one verified Editor session.", ["Inspect editor.state and retry after the session is stable."]);
    const flags: BridgeFlags = { ...(context.flags ?? {}), instance: state.identity.instanceId };
    const execute = withTransitionBinding({ mode: "execute", mutation: true, absoluteDeadlineMs: deadline });
    const observe = withTransitionBinding({ mode: "read", mutation: false, absoluteDeadlineMs: deadline });
    let accepted = false;
    let terminalReceived = false;
    try {
      let root = record((await call(context.requestId, "internal.test.run.start", { ...request, platform: request.platform ?? "EditMode", expectedEditorSessionId: state.session.editorSessionId }, flags, context, execute)).data);
      accepted = root.accepted === true || root.terminal === true;
      const rootRequestDigest = root.terminal === true ? undefined : string(root.rootRequestDigest);
      if (root.terminal !== true && !sha(rootRequestDigest)) throw invalid();
      while (root.terminal !== true) {
        if (root.terminal !== false || root.accepted !== true) throw invalid();
        if (Date.now() >= deadline) throw executionTimeout("E_EXECUTION_TIMEOUT");
        await delay(100);
        root = record((await call(childId(context.requestId, "_test_status"), "internal.test.run.status", { rootRequestId: context.requestId, rootRequestDigest }, flags, context, observe)).data);
      }
      terminalReceived = true;
      return terminal(root, context.requestId);
    } catch (error) {
      if (terminalReceived || !accepted || (error instanceof StructuredToolError && error.code === "E_EXECUTION_TIMEOUT")) throw error;
      throw executionTimeoutFrom(error);
    }
  }
});
async function call(requestId: string, command: string, input: Record<string, unknown>, flags: BridgeFlags, context: any, invocation: any) { const result = await executeBridgePayload(requestId, command, input, flags, { dataDir: context.dataDir, bridgeExecute: context.bridgeExecute }, undefined, invocation); if (!result.ok) throw StructuredToolError.from(result.error ?? { code: "BRIDGE_CONNECTION_FAILED", message: "Live test bridge failed." }); return result; }
function terminal(root: Record<string, unknown>, requestId: string): TestRunResult {
  const envelope = record(root.rootEnvelope);
  if (envelope.requestId !== requestId || typeof envelope.ok !== "boolean") throw invalid();
  if (envelope.ok !== true) {
    const error = record(envelope.error);
    const details = recordOrUndefined(error.details);
    if ((error.code === "E_TESTS_FAILED" || error.code === "E_TESTS_NOT_FOUND" || error.code === "E_TEST_ARTIFACT_WRITE_FAILED") && !testResult(details, true)) throw invalid();
    throw new StructuredToolError(
      string(error.code) ?? "E_EXECUTION_TIMEOUT",
      string(error.message) ?? "Test run failed without a structured terminal.",
      strings(error.nextActions), details,
      typeof error.transient === "boolean" ? error.transient : undefined,
      integer(error.retryAfterMs) ? error.retryAfterMs as number : undefined,
    );
  }
  const result = testResult(record(envelope.data), false);
  if (!result || result.summary.status !== "passed") throw invalid();
  return result;
}
function testResult(value: Record<string, unknown> | undefined, allowFailed: boolean): TestRunResult | undefined {
  if (!value) return undefined;
  const summary = record(value.summary);
  const failures = Array.isArray(value.failures) ? value.failures : undefined;
  if (!validSummary(summary) || (!allowFailed && summary.status !== "passed") || !failures || !failures.every(validFailure)) return undefined;
  const result: TestRunResult = { summary: summary as unknown as TestRunSummary, failures: failures as TestFailure[] };
  if (value.xml !== undefined) {
    const xml = record(value.xml);
    if (xml.kind !== "nunit-xml" || !string(xml.path) || !sha(xml.hash) || !integer(xml.size) || xml.size === 0) return undefined;
    result.xml = xml as unknown as TestXmlArtifact;
  }
  return result;
}
function validSummary(x: Record<string, unknown>): boolean { return (x.platform === "EditMode" || x.platform === "PlayMode") && (x.status === "passed" || x.status === "failed") && ["testCount", "passed", "failed", "skipped", "inconclusive"].every(k => integer(x[k])) && typeof x.duration === "number" && x.duration >= 0 && timestamp(x.startedAt) && timestamp(x.finishedAt); }
function validFailure(x: unknown): boolean { const r = record(x); return !!string(r.fullName) && typeof r.message === "string" && typeof r.stackTrace === "string" && typeof r.duration === "number" && r.duration >= 0; }
function invalid() { return new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", "test.run did not return a RunFinished terminal result.", ["Inspect UnityCliBridge.log before retrying."]); }
function executionTimeout(causeCode: string, details?: Record<string, unknown>) {
  const blockers = blockersFromDetails(details);
  return new StructuredToolError("E_EXECUTION_TIMEOUT", blockerTimeoutMessage("test.run", blockers), blockers ? blockerNextActions() : ["Inspect editor.state before retrying."], { causeCode, ...(blockers ? { blockers } : {}) }, false, 0);
}
function executionTimeoutFrom(error: unknown) { const cause = error instanceof StructuredToolError ? error : undefined; return executionTimeout(cause?.code ?? "BRIDGE_CONNECTION_FAILED", cause?.details); }
function record(value: unknown): Record<string, unknown> { return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : {}; }
function recordOrUndefined(value: unknown): Record<string, unknown> | undefined { const r = record(value); return Object.keys(r).length ? r : undefined; }
function string(value: unknown): string | undefined { return typeof value === "string" ? value : undefined; }
function strings(value: unknown): string[] { return Array.isArray(value) ? value.filter((x): x is string => typeof x === "string") : []; }
function integer(value: unknown): value is number { return typeof value === "number" && Number.isSafeInteger(value) && value >= 0; }
function sha(value: unknown): value is string { return typeof value === "string" && /^[a-f0-9]{64}$/i.test(value); }
function timestamp(value: unknown): boolean { return typeof value === "string" && Number.isFinite(Date.parse(value)); }
function childId(root: string, suffix: string) { return root.length + suffix.length <= 200 ? root + suffix : `${root.slice(0, 160)}_${createHash("sha256").update(root).digest("hex").slice(0, 24)}${suffix}`; }
function delay(ms: number) { return new Promise<void>(resolve => setTimeout(resolve, ms)); }
