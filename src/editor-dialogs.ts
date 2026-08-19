import { execFile } from "node:child_process";
import { createHash } from "node:crypto";
import { inspectProjectProcessClaims, type RegistryIdentity } from "./instances.js";
import { probeWindowsEditorState, type WindowsEditorWindow } from "./windows-editor-state.js";

export interface EditorDialog { dialogId: string; title: string; message: string; modal: true; buttons: string[]; }
export interface ObservedEditorDialog extends EditorDialog { hwnd: string; ownerHwnd: string | null; processId: number; processStartedAt: string; buttonTargets: Array<{ label: string; hwnd: string; enabled: boolean }>; }
export type DialogObservation = { outcome: "observed"; processId: number; processStartedAt: string; dialogs: ObservedEditorDialog[] } | { outcome: "unproven" | "conflict" | "offline" | "unsupported"; reason?: string };

/** 只在可验证的唯一 canonical project PID 上读取 modal；Bridge 不参与此路径。 */
export async function observeEditorDialogs(identity: RegistryIdentity, timeoutMs: number): Promise<DialogObservation> {
  const deadlineMs = Date.now() + Math.max(0, timeoutMs);
  const remaining = () => Math.max(0, deadlineMs - Date.now());
  const claims = await inspectProjectProcessClaims(identity, remaining());
  if (claims.verification === "unsupported") return { outcome: "unsupported" };
  if (claims.verification !== "verified") return { outcome: "unproven", reason: "process-claims-unverifiable" };
  if (claims.processIds.length === 0) return { outcome: "offline" };
  if (claims.processIds.length !== 1) return { outcome: "conflict", reason: "multiple-process-claims" };
  const processId = claims.processIds[0];
  const claim = claims.processClaims?.find((value) => value.processId === processId);
  if (!claim) return { outcome: "unproven", reason: "process-start-unproven" };
  const probeBudgetMs = remaining();
  if (probeBudgetMs < 1) return { outcome: "unproven", reason: "deadline" };
  const probe = await probeWindowsEditorState(processId, { timeoutMs: probeBudgetMs });
  if (probe.outcome === "unproven") return { outcome: "unproven", reason: probe.reason };
  if (probe.outcome === "unsupported") return { outcome: "unsupported" };
  return { outcome: "observed", processId, processStartedAt: claim.processStartedAt, dialogs: probe.windows.filter((window) => window.modal).map((window) => toObservedDialog(window, processId, claim.processStartedAt)) };
}
export function publicDialog(dialog: ObservedEditorDialog): EditorDialog { return { dialogId: dialog.dialogId, title: dialog.title, message: dialog.message, modal: true, buttons: dialog.buttons }; }
export function dialogId(processId: number, processStartedAt: string, hwnd: string): string { return `dialog_${createHash("sha256").update(`${processId}\0${processStartedAt}\0${hwnd}`).digest("hex")}`; }
export function normalizeDialogButton(label: string): string { return label.replace(/&/g, ""); }
function toObservedDialog(window: WindowsEditorWindow, processId: number, processStartedAt: string): ObservedEditorDialog { return { dialogId: dialogId(processId, processStartedAt, window.hwnd), title: window.title, message: window.message || window.title, modal: true, buttons: window.buttons.map((button) => normalizeDialogButton(button.label)), hwnd: window.hwnd, ownerHwnd: window.ownerHwnd, processId, processStartedAt, buttonTargets: window.buttons.map((button) => ({ ...button, label: normalizeDialogButton(button.label) })) }; }

export const CLICK_DIALOG_MIN_EXECUTION_BUDGET_MS = 10_000;
export type DialogClickOutcome = "clicked" | "stale" | "button-not-found" | "unproven";
export type DialogClickResult = { outcome: DialogClickOutcome; reason?: "timeout" | "exit" | "parse" };
export type DialogClickRunner = (script: string, timeoutMs: number) => Promise<{ stdout: string; reason?: "timeout" | "exit" }>;

/** 在调用者冻结的 deadline 内执行唯一按钮 effect 并证明原 modal 已消失。 */
export async function clickObservedDialog(dialog: ObservedEditorDialog, button: { label: string; hwnd: string }, deadlineMs: number, runner: DialogClickRunner = runClickDialogPowerShell): Promise<DialogClickResult> {
  const timeoutMs = Math.max(0, deadlineMs - Date.now());
  if (timeoutMs < 1) return { outcome: "unproven", reason: "timeout" };
  const execution = await runner(buildClickDialogScript(dialog, button), timeoutMs);
  if (execution.reason) return { outcome: "unproven", reason: execution.reason };
  return parseClickDialogOutput(execution.stdout);
}

export function buildClickDialogScript(dialog: ObservedEditorDialog, button: { label: string; hwnd: string }): string {
  return CLICK_DIALOG_SCRIPT.replace("__PID__", String(dialog.processId)).replace("__START__", JSON.stringify(dialog.processStartedAt)).replace("__DIALOG__", JSON.stringify(dialog.hwnd)).replace("__OWNER__", dialog.ownerHwnd === null ? "$null" : JSON.stringify(dialog.ownerHwnd)).replace("__TITLE__", JSON.stringify(Buffer.from(dialog.title, "utf8").toString("base64"))).replace("__BUTTON_LABEL__", JSON.stringify(Buffer.from(button.label, "utf8").toString("base64"))).replace("__BUTTON__", JSON.stringify(button.hwnd));
}

export function parseClickDialogOutput(stdout: string): DialogClickResult {
  const result = stdout.trim().split(/\r?\n/).at(-1);
  return result === "clicked" || result === "stale" || result === "button-not-found" || result === "unproven" ? { outcome: result } : { outcome: "unproven", reason: "parse" };
}

function runClickDialogPowerShell(script: string, timeoutMs: number): Promise<{ stdout: string; reason?: "timeout" | "exit" }> {
  return new Promise((resolve) => execFile("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], { timeout: timeoutMs, windowsHide: true }, (error, stdout) => {
    if (error) return resolve({ stdout, reason: error.killed ? "timeout" : "exit" });
    resolve({ stdout });
  }));
}
const CLICK_DIALOG_SCRIPT = `$ErrorActionPreference='Stop'
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text; using System.Threading;
public static class UnityCliDialogClick {
[StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
[StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
[StructLayout(LayoutKind.Sequential)] public struct MouseInput { public int dx,dy; public uint mouseData,dwFlags,time; public UIntPtr extraInfo; }
[StructLayout(LayoutKind.Sequential)] public struct Input { public uint type; public MouseInput mouse; }
[DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h); [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h); [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h); [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p); [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h,uint c); [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect r); [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point p); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern uint SendInput(uint count,Input[] inputs,int size); [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h,StringBuilder t,int n); [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h,StringBuilder t,int n);
public static string Text(IntPtr h){var t=new StringBuilder(2048);GetWindowTextW(h,t,t.Capacity);return t.ToString();} public static string ClassName(IntPtr h){var t=new StringBuilder(256);GetClassNameW(h,t,t.Capacity);return t.ToString();} public static bool Modal(IntPtr h){var o=GetWindow(h,4);return IsWindowVisible(h) && ((o!=IntPtr.Zero && !IsWindowEnabled(o)) || string.Equals(ClassName(h),"#32770",StringComparison.Ordinal));}
public static bool Click(IntPtr dialog,IntPtr button){Rect r;Point previous;if(!GetWindowRect(button,out r)||r.Right<=r.Left||r.Bottom<=r.Top||!GetCursorPos(out previous))return false;if(!SetForegroundWindow(dialog))return false;Thread.Sleep(50);if(GetForegroundWindow()!=dialog)return false;var moved=false;try{if(!SetCursorPos(r.Left+(r.Right-r.Left)/2,r.Top+(r.Bottom-r.Top)/2))return false;moved=true;var inputs=new[]{new Input{type=0,mouse=new MouseInput{dwFlags=0x0002}},new Input{type=0,mouse=new MouseInput{dwFlags=0x0004}}};return SendInput((uint)inputs.Length,inputs,Marshal.SizeOf(typeof(Input)))==(uint)inputs.Length;}finally{if(moved)SetCursorPos(previous.X,previous.Y);}}
}
"@
$targetProcessId=[uint32]__PID__; $dialog=[IntPtr][Convert]::ToInt64(__DIALOG__.Substring(2),16); $button=[IntPtr][Convert]::ToInt64(__BUTTON__.Substring(2),16); $ownerExpected=__OWNER__; $title=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(__TITLE__)); $buttonLabel=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(__BUTTON_LABEL__))
$actual=Get-CimInstance Win32_Process -Filter ("ProcessId="+$targetProcessId); if(!$actual -or ([DateTime]$actual.CreationDate).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ',[Globalization.CultureInfo]::InvariantCulture) -ne __START__){'stale';return}
[uint32]$dialogPid=0;[UnityCliDialogClick]::GetWindowThreadProcessId($dialog,[ref]$dialogPid)|Out-Null;$owner=[UnityCliDialogClick]::GetWindow($dialog,4);$ownerActual=if($owner -eq [IntPtr]::Zero){$null}else{'0x'+$owner.ToInt64().ToString('X')};if(![UnityCliDialogClick]::IsWindow($dialog)-or $dialogPid -ne $targetProcessId -or $ownerActual -ne $ownerExpected -or [UnityCliDialogClick]::Text($dialog) -cne $title -or ![UnityCliDialogClick]::Modal($dialog)){'stale';return}
[uint32]$buttonPid=0;[UnityCliDialogClick]::GetWindowThreadProcessId($button,[ref]$buttonPid)|Out-Null;$actualButton=([UnityCliDialogClick]::Text($button)).Replace('&','');if(![UnityCliDialogClick]::IsWindow($button)-or $buttonPid -ne $targetProcessId -or [UnityCliDialogClick]::ClassName($button) -cne 'Button' -or $actualButton -cne $buttonLabel -or ![UnityCliDialogClick]::IsWindowEnabled($button)){'button-not-found';return}
if(![UnityCliDialogClick]::Click($dialog,$button)){'unproven';return};for($i=0;$i -lt 50;$i++){if(![UnityCliDialogClick]::IsWindow($dialog)-or ![UnityCliDialogClick]::IsWindowVisible($dialog)-or ![UnityCliDialogClick]::Modal($dialog)){'clicked';return};Start-Sleep -Milliseconds 100};'unproven'`;
