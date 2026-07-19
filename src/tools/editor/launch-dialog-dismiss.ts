import { LAUNCH_DIALOG_BUTTON_LABELS, LAUNCH_DIALOG_TITLE_FRAGMENTS, dismissUnityLaunchDialog, findUnityProjectProcesses, probeUnityLaunchDialog } from "../../launch-dialog-dismiss.js";
import { InstanceRegistryError, currentInstanceProjectPath, selectObservationInstanceForFlags } from "../../instances.js";
import { resolveProjectIdentityAsync } from "../../project-identity.js";
import { defineTool, type ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface EditorLaunchDialogDismissRequest {}

export interface EditorLaunchDialogPlannedEffect {
  type: string;
  process: string;
  titleFragments: string[];
  button: string;
}

export interface EditorLaunchDialogDismissResult {
  command: "editor.launch-dialog.dismiss";
  platform?: string;
  supported?: boolean;
  status: "dismissed" | "not_found" | "unsupported" | "error" | "dry_run";
  dismissed: boolean;
  attempts?: number;
  timeoutMs?: number;
  intervalMs?: number;
  processId?: number;
  title?: string;
  button?: string;
  lastError?: string;
  titleFragments?: string[];
  durationMs?: number;
  dryRun?: boolean;
  plannedEffects?: EditorLaunchDialogPlannedEffect[];
  nextActions: string[];
}

export default defineTool<EditorLaunchDialogDismissRequest, EditorLaunchDialogDismissResult>({
  name: "editor.launch-dialog.dismiss",
  summary: "Dismiss a visible allowlisted Unity launch blocker for the selected project.",
  description: "Dismiss an exact allowlisted Unity launch-errors, Safe Mode, Hold On, or administrator warning dialog for the uniquely selected Unity project. When the Bridge is offline, the Tool resolves only non-batch Unity.exe processes whose -projectPath matches the selected CanonicalPath v1 identity, then scans windows only for that PID. Unknown owned modals are never clicked.",
  group: "editor",
  destructiveHint: false,
  supportsDryRun: true,
  // 这是独立于 Editor 场景会话的 Windows 桌面操作，PlayMode 不会限制其执行。
  playModeSupport: "supported",
  sideEffects: ["desktop-dialog-click"],
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "Dismiss the selected project's visible launch error dialog", argv: ["tools", "run", "editor.launch-dialog.dismiss", "--instance", "my-project", "--timeout-ms", "5000"] },
    { description: "Preview the selected desktop click", argv: ["tools", "run", "editor.launch-dialog.dismiss", "--instance", "my-project", "--dry-run"] }
  ],
  /** 处理 Unity 启动阻塞弹窗；dry-run 可只读解析 PID，但绝不扫描或点击窗口。 */
  async execute(_request, context: ToolContext) {
    const flags = context.flags ?? {};
    const dataDir = context.dataDir ?? "";
    const executionTimeoutMs = context.execution.timeoutMs ?? 5000;
    let instanceId: string | undefined;
    let canonicalProjectPath: string | undefined;
    let processId: number | undefined;
    try {
      const selected = await selectObservationInstanceForFlags(flags, dataDir, Math.min(1000, executionTimeoutMs));
      if (selected.instance.status === "conflict") {
        throw new InstanceRegistryError("E_INSTANCE_CONFLICT", "Multiple Unity Editor sessions claim the selected project identity.", ["Close duplicate Unity Editor processes before dismissing a launch dialog."], { instanceId: selected.instance.instanceId });
      }
      instanceId = selected.instance.instanceId;
      canonicalProjectPath = selected.instance.canonicalProjectPath;
      if (selected.instance.activeSession?.claimVerification === "verified") processId = selected.instance.activeSession.processId;
    } catch (error) {
      if (!(error instanceof InstanceRegistryError) || !["E_INSTANCE_NOT_FOUND", "E_INSTANCE_NOT_RUNNING"].includes(error.code)) {
        if (error instanceof InstanceRegistryError) throw new StructuredToolError(error.code, error.message, error.nextActions, error.details);
        throw error;
      }
      const projectPath = await currentInstanceProjectPath(flags, dataDir);
      if (!projectPath) throw new StructuredToolError("E_INSTANCE_REQUIRED", "A canonical Unity project identity is required to dismiss a launch dialog.", ["Pass --instance <projectPath|instanceId|alias> or run from the Unity project directory."]);
      const identity = await resolveProjectIdentityAsync(projectPath, { timeoutMs: Math.min(1000, executionTimeoutMs) });
      instanceId = identity.instanceId;
      canonicalProjectPath = identity.canonicalProjectPath;
    }

    if (processId === undefined) {
      const processes = await findUnityProjectProcesses(instanceId!, canonicalProjectPath!, Math.min(5000, executionTimeoutMs));
      if (processes.length > 1) throw new StructuredToolError("E_INSTANCE_CONFLICT", "Multiple non-batch Unity processes match the selected canonical project identity.", ["Close duplicate Unity Editor processes before dismissing a launch dialog."], { instanceId, processIds: processes.map((process) => process.processId) });
      if (processes.length === 0) throw new StructuredToolError("E_INSTANCE_NOT_RUNNING", "The selected Unity project has no attributable Unity process.", ["Open this exact Unity project in Unity Editor."], { instanceId, canonicalProjectPath });
      processId = processes[0].processId;
    }
    if (context.execution.dryRun) {
      return {
        command: "editor.launch-dialog.dismiss",
        dryRun: true,
        status: "dry_run",
        dismissed: false,
        processId,
        plannedEffects: [{ type: "desktop-dialog-click", process: "Unity", titleFragments: [...LAUNCH_DIALOG_TITLE_FRAGMENTS], button: LAUNCH_DIALOG_BUTTON_LABELS.join(" | ") }],
        nextActions: ["Rerun without --dry-run to poll the selected PID and click its exact allowlisted launch-dialog action button."]
      };
    }

    const remainingMs = () => Math.max(0, (context.absoluteDeadlineMs ?? Date.now() + executionTimeoutMs) - Date.now());
    const probe = await probeUnityLaunchDialog(processId, remainingMs());
    if (probe.kind === "not-found") {
      throw new StructuredToolError("E_LAUNCH_DIALOG_NOT_FOUND", "No exact allowlisted Unity launch dialog is visible for the selected project process.", ["Inspect editor.state for unknown or user-owned blockers; do not broaden the dialog allowlist."], { processId });
    }
    if (probe.kind !== "found") {
      throw new StructuredToolError("E_LAUNCH_DIALOG_PROBE_UNPROVEN", "The selected Unity launch dialog could not be proven safe to dismiss.", ["Inspect the visible dialog title and buttons before taking manual action."], { processId, reason: probe.kind === "error" || probe.kind === "unproven" ? probe.message : probe.kind });
    }

    // title/button exact match 已完成；barrier 紧邻唯一 BM_CLICK effect，后续只做同 handle 消失证明。
    await context.commitEffect?.();
    const timeoutMs = remainingMs();
    if (timeoutMs < 1) throw new StructuredToolError("E_LAUNCH_DIALOG_DISMISS_UNPROVEN", "The immutable deadline elapsed after the dismissal barrier.", ["Inspect editor.state before deciding whether the dialog needs manual action."], { processId });
    const intervalMs = undefined;
    const result = await dismissUnityLaunchDialog({
      timeoutMs,
      intervalMs,
      processId
    });
    if (result.status !== "dismissed") {
      throw new StructuredToolError("E_LAUNCH_DIALOG_DISMISS_UNPROVEN", "Unity launch dialog dismissal did not produce an observed closed target window.", result.nextActions, {
        status: result.status,
        processId,
        attempts: result.attempts,
        lastError: result.lastError,
      });
    }
    return {
      ...result,
      titleFragments: result.titleFragments ? [...result.titleFragments] : undefined
    };
  }
});
