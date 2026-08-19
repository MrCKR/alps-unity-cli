import { createHash } from "node:crypto";
import { executeBridgePayload } from "../../bridge/index.js";
import { withTransitionBinding, type BridgeFlags } from "../../bridge/types.js";
import { defineTool } from "../define.js";
import { waitForEditorTransitionEnd } from "../editor/readiness.js";
import { rejectUnavailableEditorState } from "../editor/state.js";
import { StructuredToolError } from "../errors.js";

export interface AssetRefreshRequest {
  path?: string;
  assetPath?: string;
  target?: string;
  forceUpdate?: boolean;
  importRecursive?: boolean;
}
export interface AssetPlannedEffect { type: "asset.import"; path: string; sideEffects?: string; }
export interface AssetRefreshResult { path: string; options: string; refreshed: boolean; plannedEffects: AssetPlannedEffect[]; }
export interface AssetRefreshDryRunResult { dryRun: true; path: string; options: string; plannedEffects: AssetPlannedEffect[]; }
type RecordValue = Record<string, unknown>;

export default defineTool<AssetRefreshRequest, AssetRefreshResult | AssetRefreshDryRunResult>({
  name: "asset.refresh",
  summary: "Reimport one Assets/ asset with content and AssetDatabase readback proof; supports --dry-run preview.",
  description: "Reimport one Assets/ asset and return only after its continued file digest and AssetDatabase GUID are stable. Whole-database refresh has no attributable business completion identity and is rejected. forceUpdate and importRecursive map to ImportAssetOptions. Supports --dry-run to preview the planned import.",
  group: "asset",
  destructiveHint: false,
  supportsDryRun: true,
  sideEffects: ["asset-import"],
  target: "editor",
  playModeSupport: "editor-only",
  timeoutMs: 30_000,
  handler: "hybrid",
  supportStatus: "live-bridge",
  examples: [{ description: "Preview reimporting one asset", argv: ["tools", "run", "asset.refresh", "--input", "{\"forceUpdate\":true,\"path\":\"Assets/Textures/Icon.png\"}", "--dry-run"] }],
  async execute(request, context) {
    const path = request.path ?? request.assetPath ?? request.target;
    if (!path) throw new StructuredToolError("E_ASSET_REFRESH_PROOF_MISSING", "asset.refresh requires one asset path because whole-database refresh has no attributable completion proof.", ["Pass an Assets/* path to reimport one observable asset."]);
    if (context.execution.dryRun) return { dryRun: true, path, options: options(request), plannedEffects: [{ type: "asset.import", path, sideEffects: options(request) }] };
    const deadline = context.absoluteDeadlineMs ?? Date.now() + (context.execution.timeoutMs ?? 30_000);
    const state = await waitForEditorTransitionEnd(context, childId(context.requestId, "_asset_refresh_state"), deadline);
    if (state.status === "blocked") throw new StructuredToolError("E_UNITY_BLOCKED", "Unity Editor is blocked and cannot refresh assets.", ["Resolve the Editor blocker, then retry."], { blockers: state.blockers });
    rejectUnavailableEditorState(state, "asset.refresh");
    if (!state.session.known || !state.session.editorSessionId) throw new StructuredToolError("E_EDITOR_SESSION_REPLACED", "asset.refresh could not bind one verified Editor session.", ["Inspect editor.state and retry after the session is stable."]);
    if (state.status !== "ready") throw new StructuredToolError("E_UNITY_TRANSITIONING", "asset.refresh requires a stable Editor generation.", ["Wait for editor.state to become ready, then retry."], undefined, true, 250);
    if (state.state.playMode.value !== "edit") throw new StructuredToolError("E_TOOL_PLAYMODE_UNAVAILABLE", "asset.refresh is editor-only and cannot run in Play Mode.", ["Exit Play Mode before running asset.refresh."], { toolName: "asset.refresh", playModeSupport: "editor-only" });
    const flags: BridgeFlags = { ...(context.flags ?? {}), instance: state.identity.instanceId };
    const execute = withTransitionBinding({ mode: "execute", mutation: true, absoluteDeadlineMs: deadline });
    const observe = withTransitionBinding({ mode: "read", mutation: false, absoluteDeadlineMs: deadline });
    let root = record((await bridge(context.requestId, "internal.asset.refresh.start", { ...request }, flags, context, execute)).data);
    if (root.terminal !== true && (root.terminal !== false || root.accepted !== true)) throw invalid();
    const rootRequestDigest = root.terminal === true ? undefined : typeof root.rootRequestDigest === "string" && /^[a-f0-9]{64}$/.test(root.rootRequestDigest) ? root.rootRequestDigest : undefined;
    if (root.terminal !== true && !rootRequestDigest) throw invalid();
    while (root.terminal !== true) {
      if (Date.now() >= deadline) throw timeout();
      await delay(100);
      try { root = record((await bridge(childId(context.requestId, "_asset_refresh_status"), "internal.asset.refresh.status", { rootRequestId: context.requestId, rootRequestDigest }, flags, context, observe)).data); }
      catch { throw timeout(); }
    }
    return terminal(root, context.requestId);
  },
});

async function bridge(requestId: string, command: string, input: RecordValue, flags: BridgeFlags, context: any, invocation: any): Promise<{ data?: unknown }> {
  const result = await executeBridgePayload(requestId, command, input, flags, { dataDir: context.dataDir, bridgeExecute: context.bridgeExecute }, undefined, invocation);
  if (!result.ok) throw StructuredToolError.from(result.error ?? { code: "BRIDGE_CONNECTION_FAILED", message: "Live asset refresh bridge failed." });
  return { data: result.data };
}
function terminal(root: RecordValue, requestId: string): AssetRefreshResult {
  const envelope = record(root.rootEnvelope); if (envelope.requestId !== requestId || envelope.ok !== true) throw terminalError(envelope, requestId);
  const data = record(envelope.data); const effects = effectsOf(data.plannedEffects);
  if (typeof data.path !== "string" || typeof data.options !== "string" || data.refreshed !== true || !effects || effects.length !== 1 || effects[0].path !== data.path || effects[0].sideEffects !== undefined) throw invalid();
  return { path: data.path, options: data.options, refreshed: true, plannedEffects: effects };
}
function terminalError(envelope: RecordValue, requestId: string): never { const error = record(envelope.error); if (envelope.requestId === requestId && envelope.ok === false && typeof error.code === "string" && typeof error.message === "string") throw new StructuredToolError(error.code, error.message, strings(error.nextActions), recordOrUndefined(error.details), error.transient === true, typeof error.retryAfterMs === "number" ? error.retryAfterMs : undefined); throw invalid(); }
function effectsOf(value: unknown): AssetPlannedEffect[] | undefined { if (!Array.isArray(value)) return undefined; const result: AssetPlannedEffect[] = []; for (const raw of value) { const effect = record(raw); if (effect.type !== "asset.import" || typeof effect.path !== "string" || (effect.sideEffects !== undefined && typeof effect.sideEffects !== "string")) return undefined; result.push({ type: "asset.import", path: effect.path, ...(typeof effect.sideEffects === "string" ? { sideEffects: effect.sideEffects } : {}) }); } return result; }
function options(request: AssetRefreshRequest): string { const values: string[] = []; if (request.forceUpdate) values.push("ForceUpdate"); if (request.importRecursive) values.push("ImportRecursive"); return values.length === 0 ? "Default" : values.join(", "); }
function timeout(): StructuredToolError { return new StructuredToolError("E_EXECUTION_TIMEOUT", "asset.refresh did not return a terminal result before the request deadline.", ["Inspect editor.state before deciding whether to submit a new request."]); }
function invalid(): StructuredToolError { return new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", "asset.refresh did not return a stable terminal readback.", ["Inspect UnityCliBridge.log before retrying."]); }
function record(value: unknown): RecordValue { return value && typeof value === "object" && !Array.isArray(value) ? value as RecordValue : {}; }
function recordOrUndefined(value: unknown): RecordValue | undefined { const result = record(value); return Object.keys(result).length > 0 ? result : undefined; }
function strings(value: unknown): string[] { return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : []; }
function childId(root: string, suffix: string): string { return root.length + suffix.length <= 200 ? root + suffix : `${root.slice(0, 160)}_${createHash("sha256").update(root).digest("hex").slice(0, 24)}${suffix}`; }
function delay(ms: number): Promise<void> { return new Promise((resolve) => setTimeout(resolve, ms)); }
