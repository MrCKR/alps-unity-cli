import { getBoolean, getNumber } from "../args.js";
import { dismissUnityLaunchDialog, DISMISS_BUTTON_LABEL, LAUNCH_DIALOG_TITLE_FRAGMENTS } from "../launch-dialog-dismiss.js";
import { success } from "../output.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";
import { startUnityJob } from "./unity-job.js";

export function handleEditor(command: string | undefined, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> | undefined {
  if (command === "refresh") {
    return startUnityJob("refresh", flags, runtime, requestId, refreshArgs(flags), { action: "refresh", compile: getBoolean(flags, "compile") });
  }

  if (command === "launch-dialog.dismiss") {
    return handleLaunchDialogDismiss(flags, requestId);
  }

  return undefined;
}

/** 处理 Unity 启动错误/Safe Mode 桌面弹窗 dismiss；不依赖 live Bridge。 */
async function handleLaunchDialogDismiss(flags: Record<string, string | boolean>, requestId: string): Promise<CliResult> {
  const processId = getNumber(flags, "process-id") ?? getNumber(flags, "processId") ?? getNumber(flags, "pid");
  if (getBoolean(flags, "dry-run")) {
    return success(requestId, {
      command: "editor.launch-dialog.dismiss",
      dryRun: true,
      status: "dry_run",
      dismissed: false,
      processId,
      plannedEffects: [{ type: "desktop-dialog-click", process: "Unity", titleFragments: LAUNCH_DIALOG_TITLE_FRAGMENTS, button: DISMISS_BUTTON_LABEL }],
      nextActions: ["Rerun without --dry-run to poll the Windows desktop and click Ignore when a matching Unity launch dialog is visible."]
    });
  }

  return success(requestId, await dismissUnityLaunchDialog({
    timeoutMs: getNumber(flags, "timeout-ms") ?? getNumber(flags, "timeoutMs"),
    intervalMs: getNumber(flags, "interval-ms") ?? getNumber(flags, "intervalMs"),
    processId
  }));
}

function refreshArgs(flags: Record<string, string | boolean>): string[] {
  return ["-executeMethod", getBoolean(flags, "compile") ? "UnityCli.UnityCliBatch.RefreshAndCompile" : "UnityCli.UnityCliBatch.Refresh"];
}
