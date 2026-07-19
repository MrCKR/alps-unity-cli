import { execFile } from "node:child_process";
import { platform as currentPlatform } from "node:os";
import { basename } from "node:path";
import { resolveProjectIdentityAsync } from "./project-identity.js";

export const LAUNCH_DIALOG_RULES = [
  {
    titleFragments: ["Safe Mode", "Compiler Errors", "Compile Errors", "Scripts have compiler errors", "Hold On"],
    buttonLabels: ["Ignore"],
  },
  {
    titleFragments: ["Unity is running as administrator."],
    buttonLabels: ["I wish to continue at my own risk"],
  },
] as const;

export const LAUNCH_DIALOG_TITLE_FRAGMENTS = LAUNCH_DIALOG_RULES.flatMap((rule) => [...rule.titleFragments]);
export const LAUNCH_DIALOG_BUTTON_LABELS = LAUNCH_DIALOG_RULES.flatMap((rule) => [...rule.buttonLabels]);
export const DISMISS_BUTTON_LABEL = LAUNCH_DIALOG_BUTTON_LABELS[0];

export type LaunchDialogPlatform = NodeJS.Platform | string;

export type LaunchDialogDismissAttempt =
  | { kind: "found"; button: string; title?: string; processId?: number }
  | { kind: "dismissed"; button: string; title?: string; processId?: number }
  | { kind: "not-found" }
  | { kind: "unproven"; message: string }
  | { kind: "error"; message: string };

export interface LaunchDialogDismissOptions {
  timeoutMs?: number;
  intervalMs?: number;
  processId?: number;
  platform?: LaunchDialogPlatform;
}

export interface UnityProjectProcess {
  processId: number;
  executablePath: string;
  argv: string[];
  projectPath: string;
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
    const attempt = await tryWindowsLaunchDialog(processId, false, Math.min(5000, Math.max(1, deadline - Date.now())));
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
    if (attempt.kind === "error" || attempt.kind === "unproven") {
      lastError = attempt.message;
      break;
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

/** 解析结构化 Unity process 枚举；只接受可验证的 Unity.exe 主进程 argv。 */
export function parseUnityProcessDiscoveryOutput(stdout: string): UnityProjectProcess[] {
  const text = stdout.trim();
  if (!text) return [];
  const parsed = JSON.parse(text) as unknown;
  const items = Array.isArray(parsed) ? parsed : [parsed];
  const result: UnityProjectProcess[] = [];
  for (const item of items) {
    if (!item || typeof item !== "object" || Array.isArray(item)) continue;
    const record = item as Record<string, unknown>;
    if (!Number.isInteger(record.processId) || Number(record.processId) <= 0 || typeof record.executablePath !== "string" || basename(record.executablePath).toLowerCase() !== "unity.exe") continue;
    if (!Array.isArray(record.argv) || record.argv.some((value) => typeof value !== "string")) continue;
    const argv = record.argv as string[];
    if (argv.some((value) => value.toLowerCase() === "-batchmode")) continue;
    const projectIndex = argv.findIndex((value) => value.toLowerCase() === "-projectpath");
    if (projectIndex < 0 || !argv[projectIndex + 1]) continue;
    result.push({ processId: Number(record.processId), executablePath: record.executablePath, argv, projectPath: argv[projectIndex + 1] });
  }
  return result;
}

/** 枚举 Unity.exe argv 后以 CanonicalPath v1 过滤到一个 project identity；不扫描其他 PID 的窗口。 */
export async function findUnityProjectProcesses(instanceId: string, canonicalProjectPath: string, timeoutMs = 3000, platform: LaunchDialogPlatform = currentPlatform()): Promise<UnityProjectProcess[]> {
  if (platform !== "win32" || timeoutMs < 1) return [];
  const startedAt = Date.now();
  const stdout = await runPowerShellDiscovery(WINDOWS_UNITY_PROCESS_DISCOVERY_SCRIPT, timeoutMs);
  const matches: UnityProjectProcess[] = [];
  for (const candidate of parseUnityProcessDiscoveryOutput(stdout)) {
    const remainingMs = Math.max(0, timeoutMs - (Date.now() - startedAt));
    if (remainingMs < 1) break;
    try {
      const identity = await resolveProjectIdentityAsync(candidate.projectPath, { timeoutMs: remainingMs });
      if (identity.instanceId === instanceId) matches.push(candidate);
    } catch (error) {
      const textualMatch = candidate.projectPath.replaceAll("/", "\\").replace(/[\\]+$/, "").toLowerCase()
        === canonicalProjectPath.replaceAll("/", "\\").replace(/[\\]+$/, "").toLowerCase();
      if (textualMatch) throw error;
    }
  }
  return matches.sort((left, right) => left.processId - right.processId);
}

/** 只读检查指定 PID 是否存在 title/button 成对命中的启动 blocker。 */
export function probeUnityLaunchDialog(processId: number, timeoutMs = 5000): Promise<LaunchDialogDismissAttempt> {
  return tryWindowsLaunchDialog(normalizeProcessId(processId), true, Math.min(5000, Math.max(1, timeoutMs)));
}

/** 构造 Windows PowerShell 脚本；只允许扫描一个已归因到 canonical project 的 Unity PID。 */
export function buildWindowsDismissScript(processId?: number, probeOnly = false): string {
  const pidLiteral = processId && Number.isInteger(processId) && processId > 0 ? `@(${processId})` : "@()";
  return WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE
    .replace("__UNITY_CLI_TARGET_PIDS__", pidLiteral)
    .replace("__UNITY_CLI_PROBE_ONLY__", probeOnly ? "$true" : "$false");
}

/** 解析平台脚本输出的最后一个契约 token，忽略 PowerShell 早期噪声。 */
export function parseDismissOutput(stdout: string): LaunchDialogDismissAttempt {
  const lines = stdout.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
  if (lines.length === 0) return { kind: "not-found" };
  const last = lines[lines.length - 1];
  if (last === "not-found") return { kind: "not-found" };
  if (last.startsWith("error:")) return { kind: "error", message: last.substring("error:".length) };
  if (last.startsWith("unproven:")) return { kind: "unproven", message: decodeBase64(last.substring("unproven:".length)) || "The launch dialog proof was incomplete." };
  if (last.startsWith("found:") || last.startsWith("dismissed:")) {
    const [kind, pidText, buttonBase64 = "", titleBase64 = ""] = last.split(":");
    const processId = Number(pidText);
    return {
      kind: kind === "found" ? "found" : "dismissed",
      button: decodeBase64(buttonBase64) || DISMISS_BUTTON_LABEL,
      title: decodeBase64(titleBase64) || undefined,
      processId: Number.isInteger(processId) && processId > 0 ? processId : undefined
    };
  }
  return { kind: "not-found" };
}

export const WINDOWS_UNITY_PROCESS_DISCOVERY_SCRIPT = `
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class UnityCliProjectProcessArgv {
  [DllImport("shell32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);
  [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr value);
  public static string[] Parse(string commandLine) {
    int argc; var memory = CommandLineToArgvW(commandLine, out argc); if (memory == IntPtr.Zero) return null;
    try { var result = new string[argc]; for (var i = 0; i < argc; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, i * IntPtr.Size)); return result; }
    finally { LocalFree(memory); }
  }
}
"@
$items = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop | ForEach-Object {
  $argv = [UnityCliProjectProcessArgv]::Parse($_.CommandLine)
  if ($argv) { @{ processId=[int]$_.ProcessId; executablePath=$_.ExecutablePath; argv=@($argv) } }
})
ConvertTo-Json -InputObject $items -Compress -Depth 5
`;

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
    sealed class DialogRule {
      public readonly string[] TitleFragments;
      public readonly string[] ButtonLabels;
      public DialogRule(string[] titleFragments, string[] buttonLabels) { TitleFragments = titleFragments; ButtonLabels = buttonLabels; }
    }
    static readonly DialogRule[] Rules = new[] { ${LAUNCH_DIALOG_RULES.map((rule) => `new DialogRule(new[] { ${rule.titleFragments.map((fragment) => JSON.stringify(fragment)).join(", ")} }, new[] { ${rule.buttonLabels.flatMap((label) => [label, `&${label}`]).map((label) => JSON.stringify(label)).join(", ")} })`).join(", ")} };

    public static string TryDismiss(int[] unityPids, bool probeOnly) {
      var unityPidSet = new HashSet<uint>();
      for (int i = 0; i < unityPids.Length; i++) unityPidSet.Add((uint)unityPids[i]);
      if (unityPidSet.Count == 0) return "not-found";

      IntPtr matchedDialog = IntPtr.Zero;
      uint matchedPid = 0;
      string matchedTitle = "";
      DialogRule matchedRule = null;
      var sb = new StringBuilder(512);

      EnumWindows((hWnd, lParam) => {
        if (!IsWindowVisible(hWnd)) return true;
        uint procId;
        GetWindowThreadProcessId(hWnd, out procId);
        if (!unityPidSet.Contains(procId)) return true;
        sb.Length = 0;
        GetWindowTextW(hWnd, sb, sb.Capacity);
        var title = sb.ToString();
        foreach (var rule in Rules) {
          foreach (var fragment in rule.TitleFragments) {
            if (title.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0) continue;
            matchedDialog = hWnd;
            matchedPid = procId;
            matchedTitle = title;
            matchedRule = rule;
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
        foreach (var label in matchedRule.ButtonLabels) {
          if (string.Equals(text, label, StringComparison.OrdinalIgnoreCase)) {
            matchedButton = hWnd;
            matchedButtonText = text;
            return false;
          }
        }
        return true;
      }, IntPtr.Zero);

      if (matchedButton == IntPtr.Zero) return "unproven:" + Encode("The recognized Unity launch dialog has no title-matched allowlisted action button.");
      if (probeOnly) return "found:" + matchedPid.ToString() + ":" + Encode(matchedButtonText) + ":" + Encode(matchedTitle);
      SendMessageW(matchedButton, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
      bool targetStillVisible = false;
      bool matchedButtonStillPresent = false;
      // Unity 异步关闭原生对话框；只轮询同一 PID/handle 的消失证明，绝不重放点击。
      for (var proofAttempt = 0; proofAttempt < 20; proofAttempt++) {
        targetStillVisible = false;
        matchedButtonStillPresent = false;
        EnumWindows((hWnd, lParam) => {
          if (!IsWindowVisible(hWnd)) return true;
          uint procId;
          GetWindowThreadProcessId(hWnd, out procId);
          if (procId != matchedPid || hWnd != matchedDialog) return true;
          targetStillVisible = true;
          EnumChildWindows(hWnd, (child, _) => {
            if (child == matchedButton) matchedButtonStillPresent = true;
            return true;
          }, IntPtr.Zero);
          return false;
        }, IntPtr.Zero);
        if (!targetStillVisible) break;
        System.Threading.Thread.Sleep(100);
      }
      if (targetStillVisible) return "unproven:" + Encode(matchedButtonStillPresent
        ? "The clicked Unity launch dialog and allowlisted action button are still visible after BM_CLICK."
        : "The clicked Unity launch dialog is still visible after BM_CLICK.");
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
  $unityPids = @($targetPids | ForEach-Object {
    $process = Get-Process -Id $_ -ErrorAction SilentlyContinue
    if ($process -and $process.ProcessName -eq 'Unity') { [int]$process.Id }
  })
  if ($unityPids.Count -eq 0) { Write-Output 'not-found'; return }
  $probeOnly = __UNITY_CLI_PROBE_ONLY__
  Write-Output ([UnityCli.LaunchDialog.Dismisser]::TryDismiss([int[]]$unityPids, [bool]$probeOnly))
} catch {
  Write-Output ('error:' + $_.Exception.Message)
}
`;

/** 执行只读 process discovery PowerShell，并保留明确 timeout/exit failure。 */
async function runPowerShellDiscovery(script: string, timeoutMs: number): Promise<string> {
  return new Promise((resolve, reject) => {
    execFile(
      "powershell.exe",
      ["-NoProfile", "-NonInteractive", "-Command", script],
      { timeout: timeoutMs, windowsHide: true },
      (error, stdout) => error ? reject(error) : resolve(stdout)
    );
  });
}

/** 执行一次 PID-scoped probe 或 click；probeOnly 绝不发送 BM_CLICK。 */
async function tryWindowsLaunchDialog(processId: number | undefined, probeOnly: boolean, timeoutMs: number): Promise<LaunchDialogDismissAttempt> {
  return new Promise((resolve) => {
    execFile(
      "powershell.exe",
      ["-NoProfile", "-NonInteractive", "-Command", buildWindowsDismissScript(processId, probeOnly)],
      { timeout: timeoutMs, windowsHide: true },
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
