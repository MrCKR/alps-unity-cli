import { executeBridgePayload } from "../../bridge/index.js";
import { withTransitionBinding } from "../../bridge/types.js";
import { defineTool } from "../define.js";
import { waitForEditorTransitionEnd } from "../editor/readiness.js";
import { rejectUnavailableEditorState } from "../editor/state.js";
import { blockerNextActions, blockerTimeoutMessage, blockersFromDetails, StructuredToolError } from "../errors.js";

const BUILD_TARGETS = ["StandaloneWindows64", "StandaloneWindows", "StandaloneOSX", "StandaloneLinux64", "WebGL", "iOS", "Android"] as const;
type BuildTarget = typeof BUILD_TARGETS[number];
export interface BuildRunRequest { target?: BuildTarget; output?: string; targets?: BuildTarget[]; options?: string[]; }
export interface BuildArtifact { outputPath: string; exists: boolean; hash: string | null; treeHash: string | null; fileCount: number; size: number; }
export interface BuildRunTargetResult { target: BuildTarget; result: "Succeeded" | "Failed" | "Cancelled" | "Unknown"; outputPath: string; totalSize: number; totalErrors: number; totalWarnings: number; startedAt: string; finishedAt: string; durationMs: number; messages: Array<{ type: string; message: string }>; artifact: BuildArtifact; }
export interface BuildRunResult { targets: BuildRunTargetResult[]; }
export interface BuildRunDryRunResult { dryRun: true; plannedEffects: Array<{ type: "build-player"; target: BuildTarget; outputPath: string }>; }

const windowsBuildExample = { target: "StandaloneWindows64", output: "Builds/App.exe" };

export default defineTool<BuildRunRequest, BuildRunResult | BuildRunDryRunResult>({
  name: "build.run", summary: "Build player targets in the open Unity Editor and return verified artifacts.",
  description: "Builds requested player targets serially in the selected live Unity Editor. Each target returns its BuildReport terminal summary and a fresh output artifact readback; this synchronous Editor operation cannot be cancelled after it starts.",
  group: "build", target: "editor", destructiveHint: true, supportsDryRun: true, timeoutMs: 600_000, sideEffects: ["build-output-write", "asset-import", "compile"], playModeSupport: "editor-only", handler: "hybrid", supportStatus: "live-bridge",
  examples: [{ description: "Build a Windows player in the open Editor.", argv: ["tools", "run", "build.run", "--input", JSON.stringify(windowsBuildExample)], input: windowsBuildExample }],
  async execute(request, context) {
    const targets = normalizeTargets(request);
    const outputs = targets.map((target) => outputForTarget(request.output, target, targets.length > 1));
    if (new Set(outputs.map(pathKey)).size !== outputs.length) {
      throw new StructuredToolError("E_SCHEMA_INVALID", "Build targets resolve to conflicting output paths.", ["Use distinct project-relative outputs, or include {target} for multiple targets."]);
    }
    if (context.execution.dryRun) return { dryRun: true, plannedEffects: targets.map((target, index) => ({ type: "build-player" as const, target, outputPath: outputs[index]! })) };
    const deadline = context.absoluteDeadlineMs ?? Date.now() + (context.execution.timeoutMs ?? 30_000);
    const state = await waitForEditorTransitionEnd(context, `${context.requestId}_build_state`, deadline);
    if (state.status === "blocked") throw new StructuredToolError("E_UNITY_BLOCKED", "Unity Editor is blocked and cannot start a player build.", ["Resolve the Editor blocker, then retry."], { blockers: state.blockers });
    rejectUnavailableEditorState(state, "build.run");
    if (!state.session.known || !state.session.editorSessionId) throw new StructuredToolError("E_EDITOR_SESSION_REPLACED", "build.run could not bind one verified Editor session.", ["Inspect editor.state and retry after the session is stable."]);
    if (state.status !== "ready") throw new StructuredToolError("E_UNITY_TRANSITIONING", "build.run requires a stable Editor generation.", ["Wait for editor.state to become ready, then retry."], undefined, true, 250);
    if (state.state.playMode.value !== "edit") throw new StructuredToolError("E_TOOL_PLAYMODE_UNAVAILABLE", "build.run is editor-only and cannot run in Play Mode.", ["Exit Play Mode before running build.run."], { toolName: "build.run", playModeSupport: "editor-only" }, false, 0);
    let canonicalTerminalReceived = false;
    try {
      const result = await executeBridgePayload(context.requestId, "internal.build.run.start", { targets: targets.map((target, index) => ({ target, outputPath: outputs[index], options: request.options ?? [] })), expectedEditorSessionId: state.session.editorSessionId }, { ...(context.flags ?? {}), instance: state.identity.instanceId }, { dataDir: context.dataDir, rootAdmission: context.rootAdmission, bridgeExecute: context.bridgeExecute }, withTransitionBinding({ mode: "execute", mutation: true, absoluteDeadlineMs: deadline }));
      if (!result.ok) throw StructuredToolError.from(result.error ?? { code: "BRIDGE_CONNECTION_FAILED", message: "Live build bridge failed." });
      const start = record(result.data); if (start.terminal !== true) throw new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", "build.run start did not return a terminal root envelope.", ["Inspect UnityCliBridge.log before retrying."]);
      const envelope = record(start.rootEnvelope); if (envelope.requestId !== context.requestId || typeof envelope.ok !== "boolean") throw new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", "build.run returned an invalid root envelope.", ["Inspect UnityCliBridge.log before retrying."]);
      canonicalTerminalReceived = true;
      if (envelope.ok !== true) { const error = record(envelope.error); const details = error.details && typeof error.details === "object" && !Array.isArray(error.details) ? record(error.details) : undefined; throw new StructuredToolError(typeof error.code === "string" ? error.code : "E_BUILD_FAILED", typeof error.message === "string" ? error.message : "Build failed without a structured terminal.", Array.isArray(error.nextActions) ? error.nextActions.filter((x): x is string => typeof x === "string") : [], details); }
      const data = record(envelope.data); const verified = verifyTargets(data.targets, targets); if (!verified) throw new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", "build.run did not return terminal BuildReport artifact proof.", ["Inspect UnityCliBridge.log before retrying."]);
      return { targets: verified };
    } catch (error) {
      if (canonicalTerminalReceived || (error instanceof StructuredToolError && isPreEffectBuildError(error.code))) throw error;
      throw buildTimeout(error);
    }
  }
});
function normalizeTargets(request: BuildRunRequest): BuildTarget[] {
  if (request.target !== undefined && request.targets !== undefined) throw new StructuredToolError("E_SCHEMA_INVALID", "Use either target or targets, not both.", ["Pass one target or a unique targets array."]);
  const targets = request.targets ?? [request.target ?? "StandaloneWindows64"];
  if (!targets.length || targets.some((target) => !BUILD_TARGETS.includes(target))) throw new StructuredToolError("E_SCHEMA_INVALID", `targets must contain only: ${BUILD_TARGETS.join(", ")}.`, ["Use tools describe build.run for supported targets."]);
  if (new Set(targets).size !== targets.length) throw new StructuredToolError("E_SCHEMA_INVALID", "targets must not contain duplicates.", ["Pass each BuildTarget once."]);
  return targets;
}
function outputForTarget(output: string | undefined, target: BuildTarget, multiple: boolean): string {
  const result = !output ? `Builds/${target}/Player` : !multiple ? output : output.includes("{target}") ? output.replace(/\{target\}/g, target) : `${output.replace(/[\\/]$/, "")}/${target}`;
  if (!relativeProjectPath(result)) throw new StructuredToolError("E_SCHEMA_INVALID", "Build output must be project-relative.", ["Use a path such as Builds/App.exe."]);
  return result.replace(/\\/g, "/");
}
function pathKey(path: string): string { return process.platform === "win32" ? path.toLowerCase() : path; }
function relativeProjectPath(path: string): boolean { return path.length > 0 && !/^(?:[A-Za-z]:[\\/]|\\\\|\/)/.test(path) && !path.split(/[\\/]+/).includes(".."); }
function record(value: unknown): Record<string, unknown> { return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : {}; }
function verifyTargets(value: unknown, expectedTargets: readonly BuildTarget[]): BuildRunTargetResult[] | undefined {
  if (!Array.isArray(value) || value.length !== expectedTargets.length) return undefined;
  const targets: BuildRunTargetResult[] = [];
  for (let index = 0; index < value.length; index += 1) {
    const item = record(value[index]); const artifact = record(item.artifact); const hash = artifact.hash; const treeHash = artifact.treeHash; const fileCount = nonnegativeInteger(artifact.fileCount); const size = nonnegativeInteger(artifact.size); const durationMs = nonnegativeInteger(item.durationMs);
    const validArtifact = typeof artifact.outputPath === "string" && artifact.outputPath === item.outputPath && relativeProjectPath(artifact.outputPath) && typeof item.outputPath === "string" && relativeProjectPath(item.outputPath) && typeof artifact.exists === "boolean" && fileCount !== undefined && size !== undefined && (hash === null || typeof hash === "string") && (treeHash === null || typeof treeHash === "string");
    const validMessages = Array.isArray(item.messages) && item.messages.length <= 100 && item.messages.every((value) => { const message = record(value); return typeof message.type === "string" && typeof message.message === "string" && message.message.length > 0; });
    const successfulProof = item.result !== "Succeeded" || (artifact.exists === true && fileCount !== undefined && fileCount > 0 && size !== undefined && size > 0 && (isSha256(hash) || isSha256(treeHash)));
    if (item.target !== expectedTargets[index] || !["Succeeded", "Failed", "Cancelled", "Unknown"].includes(item.result as string) || nonnegativeInteger(item.totalSize) === undefined || nonnegativeInteger(item.totalErrors) === undefined || nonnegativeInteger(item.totalWarnings) === undefined || !timestamp(item.startedAt) || !timestamp(item.finishedAt) || durationMs === undefined || !validMessages || !validArtifact || !successfulProof) return undefined;
    targets.push(item as unknown as BuildRunTargetResult);
  }
  return targets;
}
function isSha256(value: unknown): value is string { return typeof value === "string" && /^[a-f0-9]{64}$/i.test(value); }
function timestamp(value: unknown): value is string { return typeof value === "string" && Number.isFinite(Date.parse(value)); }
function nonnegativeInteger(value: unknown): number | undefined { return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 ? value : undefined; }
function buildTimeout(error: unknown): StructuredToolError {
  const cause = error instanceof StructuredToolError ? error : undefined;
  const blockers = blockersFromDetails(cause?.details);
  return new StructuredToolError("E_EXECUTION_TIMEOUT", blockerTimeoutMessage("build.run", blockers), blockers ? blockerNextActions() : ["Inspect the build output and editor.state before retrying."], {
    causeCode: cause?.code ?? "BRIDGE_CONNECTION_FAILED",
    ...(blockers ? { blockers } : {}),
  }, false, 0);
}
function isPreEffectBuildError(code: string): boolean { return new Set(["E_SCHEMA_INVALID", "E_EDITOR_SESSION_REPLACED", "E_BUILD_SCENES_EMPTY", "E_TOOL_PLAYMODE_UNAVAILABLE", "E_UNITY_BLOCKED", "E_UNITY_TRANSITIONING", "E_UNITY_UNRESPONSIVE", "E_INSTANCE_NOT_RUNNING", "E_INSTANCE_UNVERIFIABLE", "E_INSTANCE_CONFLICT"]).has(code); }
