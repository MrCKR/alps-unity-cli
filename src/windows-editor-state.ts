import { execFile } from "node:child_process";
import { platform as currentPlatform } from "node:os";
import { POWERSHELL_UTF8_OUTPUT_LINE } from "./powershell-transport.js";

export interface WindowsEditorButton { label: string; hwnd: string; enabled: boolean; }
export interface WindowsEditorWindow {
  hwnd: string;
  ownerHwnd: string | null;
  className: string;
  title: string;
  message: string;
  visible: boolean;
  enabled: boolean;
  buttons: WindowsEditorButton[];
  modal: boolean;
}
export interface WindowsEditorStateProbe {
  supported: boolean;
  outcome: "observed-empty" | "observed-blocked" | "unproven" | "unsupported";
  windows: WindowsEditorWindow[];
  reason?: "timeout" | "exit" | "parse";
}
export interface WindowsEditorStateOptions { platform?: NodeJS.Platform | string; timeoutMs?: number; }

/** 仅枚举指定 Unity PID 的顶层与 owned Windows，不发送任何窗口消息。 */
export async function probeWindowsEditorState(processId: number, options: WindowsEditorStateOptions = {}): Promise<WindowsEditorStateProbe> {
  if ((options.platform ?? currentPlatform()) !== "win32" || !Number.isInteger(processId) || processId < 1) return { supported: false, outcome: "unsupported", windows: [] };
  const execution = await runPowerShell(buildWindowsEditorStateScript(processId), Math.max(1, options.timeoutMs ?? 300));
  if (execution.reason) return { supported: true, outcome: "unproven", windows: [], reason: execution.reason };
  return parseWindowsEditorStateOutput(execution.stdout);
}
export function buildWindowsEditorStateScript(processId: number): string { return WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE.replace("__UNITY_CLI_PROCESS_ID__", String(processId)); }
export function parseWindowsEditorStateOutput(output: string): WindowsEditorStateProbe {
  const line = output.split(/\r?\n/).map((value) => value.trim()).filter(Boolean).at(-1);
  if (!line) return { supported: true, outcome: "unproven", windows: [], reason: "parse" };
  try {
    const values = Array.isArray(JSON.parse(line) as unknown) ? JSON.parse(line) as unknown[] : [JSON.parse(line) as unknown];
    const windows = values.flatMap(normalizeWindow);
    if (windows.length !== values.length) return { supported: true, outcome: "unproven", windows: [], reason: "parse" };
    return { supported: true, outcome: windows.some((window) => window.modal) ? "observed-blocked" : "observed-empty", windows };
  } catch { return { supported: true, outcome: "unproven", windows: [], reason: "parse" }; }
}
function normalizeWindow(value: unknown): WindowsEditorWindow[] {
  if (!value || typeof value !== "object" || Array.isArray(value)) return [];
  const raw = value as Record<string, unknown>;
  if (typeof raw.hwnd !== "string" || typeof raw.className !== "string" || typeof raw.title !== "string" || typeof raw.message !== "string" || typeof raw.visible !== "boolean" || typeof raw.enabled !== "boolean" || typeof raw.modal !== "boolean" || !Array.isArray(raw.buttons)) return [];
  const buttons = raw.buttons.flatMap((button) => button && typeof button === "object" && !Array.isArray(button) && typeof (button as Record<string, unknown>).label === "string" && typeof (button as Record<string, unknown>).hwnd === "string" && typeof (button as Record<string, unknown>).enabled === "boolean" ? [{ label: (button as Record<string, unknown>).label as string, hwnd: (button as Record<string, unknown>).hwnd as string, enabled: (button as Record<string, unknown>).enabled as boolean }] : []);
  if (buttons.length !== raw.buttons.length) return [];
  return [{ hwnd: raw.hwnd, ownerHwnd: typeof raw.ownerHwnd === "string" ? raw.ownerHwnd : null, className: raw.className, title: raw.title, message: raw.message, visible: raw.visible, enabled: raw.enabled, buttons, modal: raw.modal }];
}

export const WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE = `${POWERSHELL_UTF8_OUTPUT_LINE}
$ErrorActionPreference = 'Stop'
try {
  Add-Type -TypeDefinition @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class UnityCliWindowProbeNative {
  private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr state);
  [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr state);
  [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr state);
  [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
  [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr hwnd);
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowEnabled(IntPtr hwnd);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder value, int count);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder value, int count);
  public static long[] TopLevelWindows(uint pid) { var handles=new List<long>(); EnumWindows((h,s)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==pid)handles.Add(h.ToInt64());return true;},IntPtr.Zero); return handles.ToArray(); }
  public static long Owner(IntPtr hwnd) { return GetWindow(hwnd,4).ToInt64(); }
  public static string ClassName(IntPtr hwnd) { var value=new StringBuilder(256);GetClassNameW(hwnd,value,value.Capacity);return value.ToString(); }
  public static string WindowText(IntPtr hwnd) { var value=new StringBuilder(2048);GetWindowTextW(hwnd,value,value.Capacity);return value.ToString(); }
  public static object[] Buttons(IntPtr parent) { var result=new List<object>();EnumChildWindows(parent,(h,s)=>{if(string.Equals(ClassName(h),"Button",StringComparison.Ordinal)){var label=WindowText(h);if(!string.IsNullOrEmpty(label))result.Add(new {label=label,hwnd="0x"+h.ToInt64().ToString("X"),enabled=IsWindowEnabled(h)});}return true;},IntPtr.Zero);return result.ToArray(); }
  public static string Message(IntPtr parent,string title) { var result=new List<string>();EnumChildWindows(parent,(h,s)=>{var className=ClassName(h);if(string.Equals(className,"Button",StringComparison.Ordinal)||string.Equals(className,"Static",StringComparison.Ordinal))return true;var text=WindowText(h).Trim();if(!string.IsNullOrEmpty(text)&&!string.Equals(text,"Static",StringComparison.Ordinal)&&!string.Equals(text,title,StringComparison.Ordinal)&&!result.Contains(text))result.Add(text);return true;},IntPtr.Zero);return string.Join("\\n",result.ToArray()); }
}
"@
  Add-Type -AssemblyName UIAutomationClient
  Add-Type -AssemblyName UIAutomationTypes
  function Get-UnityCliWindowMessage([IntPtr]$handle, [string]$title) {
    try {
      $root=[System.Windows.Automation.AutomationElement]::FromHandle($handle)
      if($null -eq $root){ throw 'UI Automation did not resolve the window handle.' }
      $texts=New-Object 'System.Collections.Generic.List[string]'
      $buttonTexts=@([UnityCliWindowProbeNative]::Buttons($handle) | ForEach-Object { ([string]$_.label).Replace('&','') })
      foreach($element in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)) {
        $current=$element.Current
        if($current.ControlType.ProgrammaticName -eq 'ControlType.Button'){ continue }
        $ancestor=$element; $insideButton=$false
        while($null -ne ($ancestor=[System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($ancestor))) {
          if($ancestor.Current.ControlType.ProgrammaticName -eq 'ControlType.Button'){ $insideButton=$true; break }
        }
        if($insideButton){ continue }
        $candidates=@($current.Name)
        try { $pattern=$element.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern); if($null -ne $pattern){ $candidates += $pattern.DocumentRange.GetText(-1) } } catch {}
        foreach($candidate in $candidates) {
          $text=([string]$candidate).Trim()
          if(-not [string]::IsNullOrEmpty($text) -and $text -ne 'Static' -and $text -ne $title -and -not ($buttonTexts -contains $text) -and -not $texts.Contains($text)){ [void]$texts.Add($text) }
        }
      }
      return [string]::Join([Environment]::NewLine,$texts.ToArray())
    } catch { return [UnityCliWindowProbeNative]::Message($handle,$title) }
  }
  $targetProcessId=[uint32]__UNITY_CLI_PROCESS_ID__; $items=@()
  foreach($rawHandle in [UnityCliWindowProbeNative]::TopLevelWindows($targetProcessId)) { try {
    $handle=[IntPtr]([int64]$rawHandle); $ownerValue=[int64][UnityCliWindowProbeNative]::Owner($handle); $owner=[IntPtr]$ownerValue; $className=[UnityCliWindowProbeNative]::ClassName($handle); $title=[UnityCliWindowProbeNative]::WindowText($handle); $visible=[UnityCliWindowProbeNative]::IsWindowVisible($handle); $enabled=[UnityCliWindowProbeNative]::IsWindowEnabled($handle); $ownerDisabled=$ownerValue -ne 0 -and -not [UnityCliWindowProbeNative]::IsWindowEnabled($owner)
    $buttonLabels=@([UnityCliWindowProbeNative]::Buttons($handle) | ForEach-Object { $_.label }); $normalizedButtonLabels=@($buttonLabels | ForEach-Object { ([string]$_).Replace('&','') }); $unityProgress=($className -eq 'UnityContainerWndClass' -and $title -match '\(busy for [0-9]+s\)\.\.\.$') -or ($normalizedButtonLabels -contains 'Cancel' -and $normalizedButtonLabels -contains 'Skip Transcoding'); $modal=$visible -and -not $unityProgress -and (($ownerValue -ne 0 -and $ownerDisabled) -or $className -eq '#32770')
    $buttons=[object[]]@(); if($modal){ $buttons=[object[]]@([UnityCliWindowProbeNative]::Buttons($handle)) }
    $items += @{hwnd='0x'+([int64]$rawHandle).ToString('X');ownerHwnd=if($ownerValue -eq 0){$null}else{'0x'+$ownerValue.ToString('X')};className=$className;title=$title;message=if($modal){Get-UnityCliWindowMessage $handle $title}else{''};visible=[bool]$visible;enabled=[bool]$enabled;buttons=$buttons;modal=[bool]$modal}
  } catch {} }
  ConvertTo-Json -InputObject @($items) -Compress -Depth 5
} catch { exit 1 }`;
function runPowerShell(script: string, timeout: number): Promise<{ stdout: string; reason?: "timeout" | "exit" }> { return new Promise((resolve) => execFile("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], { timeout, windowsHide: true }, (error, stdout) => resolve(error ? { stdout, reason: (error as Error & { killed?: boolean }).killed ? "timeout" : "exit" } : { stdout }))); }
