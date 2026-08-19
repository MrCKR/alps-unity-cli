import { createHash } from "node:crypto";
import { executeBridgePayload } from "../../bridge/index.js";
import { withTransitionBinding, type BridgeFlags } from "../../bridge/types.js";
import { defineTool } from "../define.js";
import { waitForEditorTransitionEnd } from "../editor/readiness.js";
import { rejectUnavailableEditorState } from "../editor/state.js";
import { StructuredToolError } from "../errors.js";

export interface PackageEmbedRequest { package: string; }
export interface PackageEmbedPlannedEffect { type: "package.embed"; path: string; }
export interface PackageEmbedResult { packageId: string; embeddedPath: string; plannedEffects: PackageEmbedPlannedEffect[]; }
type RecordValue = Record<string, unknown>;

export default defineTool<PackageEmbedRequest, PackageEmbedResult>({
  name: "package.embed",
  summary: "Embed an installed registry package and return its verified local package readback.",
  description: "Use PackageClient.Embed to copy an installed registry package into Packages/<id>, absorb package import/reload, and succeed only when PackageInfo reports source=Embedded and a real resolved directory. The embedded local copy replaces registry resolution until it is removed.",
  group: "package",
  destructiveHint: true,
  supportsDryRun: true,
  sideEffects: ["package-write"],
  target: "editor",
  playModeSupport: "editor-only",
  timeoutMs: 120_000,
  handler: "hybrid",
  supportStatus: "live-bridge",
  examples: [{ description: "Embed the installed ProBuilder package", argv: ["tools", "run", "package.embed", "--input", "{\"package\":\"com.unity.probuilder\"}"] }],
  async execute(request, context) {
    if (!request.package) throw new StructuredToolError("E_PACKAGE_ID_REQUIRED", "package.embed requires --package <id>.", ["Pass a package id like com.unity.x."]);
    if (context.execution.dryRun) return { packageId: request.package, embeddedPath: `Packages/${request.package}`, plannedEffects: [{ type: "package.embed", path: request.package }] };
    const deadline = context.absoluteDeadlineMs ?? Date.now() + (context.execution.timeoutMs ?? 120_000);
    const state = await waitForEditorTransitionEnd(context, childId(context.requestId, "_package_embed_state"), deadline);
    if (state.status === "blocked") throw new StructuredToolError("E_UNITY_BLOCKED", "Unity Editor is blocked and cannot embed packages.", ["Resolve the Editor blocker, then retry."], { blockers: state.blockers });
    rejectUnavailableEditorState(state, "package.embed");
    if (!state.session.known || !state.session.editorSessionId) throw new StructuredToolError("E_EDITOR_SESSION_REPLACED", "package.embed could not bind one verified Editor session.", ["Inspect editor.state and retry after the session is stable."]);
    if (state.status !== "ready") throw new StructuredToolError("E_UNITY_TRANSITIONING", "package.embed requires a stable Editor generation.", ["Wait for editor.state to become ready, then retry."], undefined, true, 250);
    if (state.state.playMode.value !== "edit") throw new StructuredToolError("E_TOOL_PLAYMODE_UNAVAILABLE", "package.embed is editor-only and cannot run in Play Mode.", ["Exit Play Mode before running package.embed."], { toolName: "package.embed", playModeSupport: "editor-only" });
    const flags: BridgeFlags = { ...(context.flags ?? {}), instance: state.identity.instanceId };
    const execute = withTransitionBinding({ mode: "execute", mutation: true, absoluteDeadlineMs: deadline });
    const observe = withTransitionBinding({ mode: "read", mutation: false, absoluteDeadlineMs: deadline });
    let root = record((await bridge(context.requestId, "internal.package.embed.start", { ...request }, flags, context, execute)).data);
    if (root.terminal !== true && (root.terminal !== false || root.accepted !== true)) throw invalid();
    const rootRequestDigest = root.terminal === true ? undefined : typeof root.rootRequestDigest === "string" && /^[a-f0-9]{64}$/.test(root.rootRequestDigest) ? root.rootRequestDigest : undefined;
    if (root.terminal !== true && !rootRequestDigest) throw invalid();
    while (root.terminal !== true) {
      if (Date.now() >= deadline) throw timeout();
      await delay(100);
      try { root = record((await bridge(childId(context.requestId, "_package_embed_status"), "internal.package.embed.status", { rootRequestId: context.requestId, rootRequestDigest }, flags, context, observe)).data); }
      catch { throw timeout(); }
    }
    return terminal(root, context.requestId);
  },
});

async function bridge(requestId: string, command: string, input: RecordValue, flags: BridgeFlags, context: any, invocation: any): Promise<{ data?: unknown }> {
  const result = await executeBridgePayload(requestId, command, input, flags, { dataDir: context.dataDir, bridgeExecute: context.bridgeExecute }, undefined, invocation);
  if (!result.ok) throw StructuredToolError.from(result.error ?? { code: "BRIDGE_CONNECTION_FAILED", message: "Live package embed bridge failed." });
  return { data: result.data };
}
function terminal(root: RecordValue, requestId: string): PackageEmbedResult {
  const envelope = record(root.rootEnvelope); if (envelope.requestId !== requestId || envelope.ok !== true) throw terminalError(envelope, requestId);
  const data = record(envelope.data); const effects = effectsOf(data.plannedEffects);
  if (typeof data.packageId !== "string" || typeof data.embeddedPath !== "string" || data.embeddedPath.length === 0 || !effects || effects.length !== 1 || effects[0].path !== data.packageId) throw invalid();
  return { packageId: data.packageId, embeddedPath: data.embeddedPath, plannedEffects: effects };
}
function terminalError(envelope: RecordValue, requestId: string): never { const error = record(envelope.error); if (envelope.requestId === requestId && envelope.ok === false && typeof error.code === "string" && typeof error.message === "string") throw new StructuredToolError(error.code, error.message, strings(error.nextActions), recordOrUndefined(error.details), error.transient === true, typeof error.retryAfterMs === "number" ? error.retryAfterMs : undefined); throw invalid(); }
function effectsOf(value: unknown): PackageEmbedPlannedEffect[] | undefined { if (!Array.isArray(value)) return undefined; const result: PackageEmbedPlannedEffect[] = []; for (const raw of value) { const effect = record(raw); if (effect.type !== "package.embed" || typeof effect.path !== "string") return undefined; result.push({ type: "package.embed", path: effect.path }); } return result; }
function timeout(): StructuredToolError { return new StructuredToolError("E_EXECUTION_TIMEOUT", "package.embed did not return a terminal result before the request deadline.", ["Inspect editor.state and Package Manager before deciding whether to submit a new request."]); }
function invalid(): StructuredToolError { return new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", "package.embed did not return a stable terminal readback.", ["Inspect UnityCliBridge.log before retrying."]); }
function record(value: unknown): RecordValue { return value && typeof value === "object" && !Array.isArray(value) ? value as RecordValue : {}; }
function recordOrUndefined(value: unknown): RecordValue | undefined { const result = record(value); return Object.keys(result).length > 0 ? result : undefined; }
function strings(value: unknown): string[] { return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : []; }
function childId(root: string, suffix: string): string { return root.length + suffix.length <= 200 ? root + suffix : `${root.slice(0, 160)}_${createHash("sha256").update(root).digest("hex").slice(0, 24)}${suffix}`; }
function delay(ms: number): Promise<void> { return new Promise((resolve) => setTimeout(resolve, ms)); }
