import { DISMISS_BUTTON_LABEL, LAUNCH_DIALOG_TITLE_FRAGMENTS, dismissUnityLaunchDialog } from "../../launch-dialog-dismiss.js";
import { defineTool } from "../define.js";

export interface EditorLaunchDialogDismissRequest {
  timeoutMs?: number;
  intervalMs?: number;
  processId?: number;
  pid?: number;
  dryRun?: boolean;
}

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

function optionalInteger(value: unknown): number | undefined {
  const parsed = typeof value === "number" ? value : typeof value === "string" && value.trim() !== "" ? Number(value) : NaN;
  return Number.isInteger(parsed) && parsed >= 0 ? parsed : undefined;
}

export default defineTool<EditorLaunchDialogDismissRequest, EditorLaunchDialogDismissResult>({
  name: "editor.launch-dialog.dismiss",
  summary: "Dismiss a visible Unity launch-errors or Safe Mode desktop dialog by clicking Ignore.",
  description: "Dismiss a visible Unity launch-errors or Safe Mode desktop dialog by clicking the Ignore button. This is a Windows desktop helper for Unity startup blocks such as Enter Safe Mode? or compiler-errors-at-launch dialogs; it does not open Unity and does not modify project files. Pass processId/pid when multiple Unity Editors are running so only that Unity.exe process is scanned. If no matching dialog is visible before timeoutMs, the tool still succeeds with dismissed=false and status=not_found. Use --dry-run to preview the planned desktop click without scanning or clicking.",
  group: "editor",
  destructiveHint: false,
  supportsDryRun: true,
  playModeSupport: "editor-only",
  sideEffects: ["desktop-dialog-click"],
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "Dismiss any visible Unity launch error dialog", argv: ["tools", "run", "editor.launch-dialog.dismiss", "--input", "{\"timeoutMs\":5000}"] },
    { description: "Limit dismiss to one Unity process", argv: ["tools", "run", "editor.launch-dialog.dismiss", "--input", "{\"processId\":44960,\"timeoutMs\":5000}"] },
    { description: "Preview the desktop click", argv: ["tools", "run", "editor.launch-dialog.dismiss", "--dry-run", "--input", "{\"pid\":44960}"] }
  ],
  /** 处理 Unity 启动阻塞弹窗；dry-run 只返回计划，不扫描或点击桌面。 */
  async execute(request) {
    const processId = optionalInteger(request.processId ?? request.pid);
    const timeoutMs = optionalInteger(request.timeoutMs);
    const intervalMs = optionalInteger(request.intervalMs);
    if (request.dryRun) {
      return {
        command: "editor.launch-dialog.dismiss",
        dryRun: true,
        status: "dry_run",
        dismissed: false,
        processId,
        plannedEffects: [{ type: "desktop-dialog-click", process: "Unity", titleFragments: [...LAUNCH_DIALOG_TITLE_FRAGMENTS], button: DISMISS_BUTTON_LABEL }],
        nextActions: ["Rerun without --dry-run to poll the Windows desktop and click Ignore when a matching Unity launch dialog is visible."]
      };
    }

    const result = await dismissUnityLaunchDialog({
      timeoutMs,
      intervalMs,
      processId
    });
    return {
      ...result,
      titleFragments: result.titleFragments ? [...result.titleFragments] : undefined
    };
  }
});
