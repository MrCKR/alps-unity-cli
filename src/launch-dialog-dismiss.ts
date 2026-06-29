import { execFile } from "node:child_process";
import { platform as currentPlatform } from "node:os";

export const LAUNCH_DIALOG_TITLE_FRAGMENTS = [
  "Safe Mode",
  "Compiler Errors",
  "Compile Errors",
  "Scripts have compiler errors",
  "Hold On"
] as const;

export const DISMISS_BUTTON_LABEL = "Ignore";

export type LaunchDialogPlatform = NodeJS.Platform | string;

export type LaunchDialogDismissAttempt =
  | { kind: "dismissed"; button: string; title?: string; processId?: number }
  | { kind: "not-found" }
  | { kind: "error"; message: string };

export interface LaunchDialogDismissOptions {
  timeoutMs?: number;
  intervalMs?: number;
  processId?: number;
  platform?: LaunchDialogPlatform;
}

export interface LaunchDialogDismissResult {
  command: "editor.launch-dialog.dismiss";
  platform: string;
  supported: boolean;
  status: "dismissed" | "not_found" | "unsupported" | "error";
  dismissed: boolean;
  attempts: number;
  timeoutMs: number;
  intervalMs: number;
  processId?: number;
  title?: string;
  button?: string;
  lastError?: string;
  titleFragments: readonly string[];
  durationMs: number;
  nextActions: string[];
}

const DEFAULT_TIMEOUT_MS = 5000;
const DEFAULT_INTERVAL_MS = 250;

/** 轮询 Unity 启动错误/Safe Mode 桌面弹窗，并在找到时点击 Ignore。 */
export async function dismissUnityLaunchDialog(options: LaunchDialogDismissOptions = {}): Promise<LaunchDialogDismissResult> {
  const startedAt = Date.now();
  const platform = options.platform ?? currentPlatform();
  const timeoutMs = normalizeNonNegativeInteger(options.timeoutMs, DEFAULT_TIMEOUT_MS);
  const intervalMs = Math.max(50, normalizeNonNegativeInteger(options.intervalMs, DEFAULT_INTERVAL_MS));
  const processId = normalizeProcessId(options.processId);

  if (platform !== "win32") {
    return baseResult({
      platform,
      supported: false,
      status: "unsupported",
      attempts: 0,
      timeoutMs,
      intervalMs,
      processId,
      durationMs: Date.now() - startedAt,
      nextActions: ["Run this tool on Windows, or dismiss the Unity launch dialog manually on this platform."]
    });
  }

  if (timeoutMs === 0) {
    return baseResult({
      platform,
      supported: true,
      status: "not_found",
      attempts: 0,
      timeoutMs,
      intervalMs,
      processId,
      durationMs: Date.now() - startedAt,
      nextActions: ["Rerun with timeoutMs greater than 0 to poll and dismiss a visible Unity launch dialog."]
    });
  }

  let attempts = 0;
  let lastError: string | undefined;
  const deadline = startedAt + timeoutMs;
  do {
    attempts += 1;
    const attempt = await tryDismissWindowsLaunchDialog(processId);
    if (attempt.kind === "dismissed") {
      return baseResult({
        platform,
        supported: true,
        status: "dismissed",
        attempts,
        timeoutMs,
        intervalMs,
        processId: attempt.processId ?? processId,
        title: attempt.title,
        button: attempt.button,
        durationMs: Date.now() - startedAt,
        nextActions: ["Wait for Unity to finish importing/compiling, then inspect editor.state or console diagnostics."]
      });
    }
    if (attempt.kind === "error") {
      lastError = attempt.message;
    }
    if (Date.now() >= deadline) break;
    await sleep(Math.min(intervalMs, Math.max(0, deadline - Date.now())));
  } while (Date.now() <= deadline);

  return baseResult({
    platform,
    supported: true,
    status: lastError ? "error" : "not_found",
    attempts,
    timeoutMs,
    intervalMs,
    processId,
    lastError,
    durationMs: Date.now() - startedAt,
    nextActions: lastError
      ? ["Inspect lastError and verify PowerShell can access the Windows desktop session."]
      : ["No matching Unity launch dialog was visible. If Unity is still blocked, pass processId for the target Unity.exe and rerun."]
  });
}

/** 构造 Windows PowerShell 脚本；processId 存在时只扫描该 Unity 进程。 */
export function buildWindowsDismissScript(processId?: number): string {
  const pidLiteral = processId && Number.isInteger(processId) && processId > 0 ? `@(${processId})` : "@()";
  return WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE.replace("__UNITY_CLI_TARGET_PIDS__", pidLiteral);
}

/** 解析平台脚本输出的最后一个契约 token，忽略 PowerShell 早期噪声。 */
export function parseDismissOutput(stdout: string): LaunchDialogDismissAttempt {
  const lines = stdout.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
  if (lines.length === 0) return { kind: "not-found" };
  const last = lines[lines.length - 1];
  if (last === "not-found") return { kind: "not-found" };
  if (last.startsWith("error:")) return { kind: "error", message: last.substring("error:".length) };
  if (last.startsWith("dismissed:")) {
    const [, pidText, buttonBase64 = "", titleBase64 = ""] = last.split(":");
    const processId = Number(pidText);
    return {
      kind: "dismissed",
      button: decodeBase64(buttonBase64) || DISMISS_BUTTON_LABEL,
      title: decodeBase64(titleBase64) || undefined,
      processId: Number.isInteger(processId) && processId > 0 ? processId : undefined
    };
  }
  return { kind: "not-found" };
}

export const WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE = `
$ErrorActionPreference = 'Stop'
try {
  if (-not ([System.Management.Automation.PSTypeName]'UnityCli.LaunchDialog.Dismisser').Type) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace UnityCli.LaunchDialog {
  public static class Dismisser {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    const uint BM_CLICK = 0x00F5;
    static readonly string[] TitleFragments = new[] { ${LAUNCH_DIALOG_TITLE_FRAGMENTS.map((fragment) => JSON.stringify(fragment)).join(", ")} };
    static readonly string[] ButtonLabels = new[] { "${DISMISS_BUTTON_LABEL}", "&${DISMISS_BUTTON_LABEL}" };

    public static string TryDismiss(int[] unityPids) {
      var unityPidSet = new HashSet<uint>();
      for (int i = 0; i < unityPids.Length; i++) unityPidSet.Add((uint)unityPids[i]);
      if (unityPidSet.Count == 0) return "not-found";

      IntPtr matchedDialog = IntPtr.Zero;
      uint matchedPid = 0;
      string matchedTitle = "";
      var sb = new StringBuilder(512);

      EnumWindows((hWnd, lParam) => {
        if (!IsWindowVisible(hWnd)) return true;
        uint procId;
        GetWindowThreadProcessId(hWnd, out procId);
        if (!unityPidSet.Contains(procId)) return true;
        sb.Length = 0;
        GetWindowTextW(hWnd, sb, sb.Capacity);
        var title = sb.ToString();
        foreach (var fragment in TitleFragments) {
          if (title.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) {
            matchedDialog = hWnd;
            matchedPid = procId;
            matchedTitle = title;
            return false;
          }
        }
        return true;
      }, IntPtr.Zero);

      if (matchedDialog == IntPtr.Zero) return "not-found";

      IntPtr matchedButton = IntPtr.Zero;
      string matchedButtonText = "";
      EnumChildWindows(matchedDialog, (hWnd, lParam) => {
        sb.Length = 0;
        GetClassNameW(hWnd, sb, sb.Capacity);
        if (sb.ToString() != "Button") return true;
        sb.Length = 0;
        GetWindowTextW(hWnd, sb, sb.Capacity);
        var text = sb.ToString();
        foreach (var label in ButtonLabels) {
          if (string.Equals(text, label, StringComparison.OrdinalIgnoreCase)) {
            matchedButton = hWnd;
            matchedButtonText = text;
            return false;
          }
        }
        return true;
      }, IntPtr.Zero);

      if (matchedButton == IntPtr.Zero) return "not-found";
      SendMessageW(matchedButton, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
      return "dismissed:" + matchedPid.ToString() + ":" + Encode(matchedButtonText) + ":" + Encode(matchedTitle);
    }

    static string Encode(string value) {
      return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
    }
  }
}
"@
  }
  $targetPids = __UNITY_CLI_TARGET_PIDS__
  if ($targetPids.Count -gt 0) {
    $unityPids = @($targetPids | ForEach-Object {
      $process = Get-Process -Id $_ -ErrorAction SilentlyContinue
      if ($process -and $process.ProcessName -eq 'Unity') { [int]$process.Id }
    })
  } else {
    $unityPids = @(Get-Process -Name 'Unity' -ErrorAction SilentlyContinue | ForEach-Object { [int]$_.Id })
  }
  if ($unityPids.Count -eq 0) { Write-Output 'not-found'; return }
  Write-Output ([UnityCli.LaunchDialog.Dismisser]::TryDismiss([int[]]$unityPids))
} catch {
  Write-Output ('error:' + $_.Exception.Message)
}
`;

/** 执行一次 Windows 桌面扫描和点击动作。 */
async function tryDismissWindowsLaunchDialog(processId?: number): Promise<LaunchDialogDismissAttempt> {
  return new Promise((resolve) => {
    execFile(
      "powershell.exe",
      ["-NoProfile", "-NonInteractive", "-Command", buildWindowsDismissScript(processId)],
      { timeout: 5000, windowsHide: true },
      (error, stdout) => {
        if (error) {
          resolve({ kind: "error", message: error.message });
          return;
        }
        resolve(parseDismissOutput(stdout));
      }
    );
  });
}

function baseResult(input: Omit<LaunchDialogDismissResult, "command" | "dismissed" | "titleFragments">): LaunchDialogDismissResult {
  return {
    command: "editor.launch-dialog.dismiss",
    dismissed: input.status === "dismissed",
    titleFragments: LAUNCH_DIALOG_TITLE_FRAGMENTS,
    ...input
  };
}

function normalizeNonNegativeInteger(value: number | undefined, fallback: number): number {
  if (value === undefined || !Number.isFinite(value)) return fallback;
  return Math.max(0, Math.floor(value));
}

function normalizeProcessId(value: number | undefined): number | undefined {
  if (value === undefined || !Number.isFinite(value)) return undefined;
  const parsed = Math.floor(value);
  return parsed > 0 ? parsed : undefined;
}

function decodeBase64(value: string): string {
  if (!value) return "";
  try {
    return Buffer.from(value, "base64").toString("utf8");
  } catch {
    return "";
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
