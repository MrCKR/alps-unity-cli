import { execFile } from "node:child_process";
import { platform as currentPlatform } from "node:os";

export interface WindowsEditorButton {
  label: string;
}

export interface WindowsEditorWindow {
  hwnd: string;
  ownerHwnd: string | null;
  className: string;
  title: string;
  visible: boolean;
  enabled: boolean;
  buttons: WindowsEditorButton[];
  modal: boolean;
  kind?: "launch-errors" | "launch-warning" | "safe-mode" | "unknown-modal";
}

export interface WindowsEditorStateProbe {
  supported: boolean;
  windows: WindowsEditorWindow[];
}

export interface WindowsEditorStateOptions {
  platform?: NodeJS.Platform | string;
  timeoutMs?: number;
}

/** 仅枚举指定 Unity PID 的顶层与 owned Windows，不发送任何窗口消息。 */
export async function probeWindowsEditorState(processId: number, options: WindowsEditorStateOptions = {}): Promise<WindowsEditorStateProbe> {
  if ((options.platform ?? currentPlatform()) !== "win32" || !Number.isInteger(processId) || processId < 1) {
    return { supported: false, windows: [] };
  }
  const output = await runPowerShell(buildWindowsEditorStateScript(processId), Math.max(1, options.timeoutMs ?? 300));
  return parseWindowsEditorStateOutput(output);
}

/** P/Invoke 仅调用窗口读取 API；此探测器不得产生桌面副作用。 */
export function buildWindowsEditorStateScript(processId: number): string {
  return WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE.replace("__UNITY_CLI_PROCESS_ID__", String(processId));
}

export function parseWindowsEditorStateOutput(output: string): WindowsEditorStateProbe {
  const line = output.split(/\r?\n/).map((value) => value.trim()).filter(Boolean).at(-1);
  if (!line) return { supported: true, windows: [] };
  try {
    const raw = JSON.parse(line) as unknown;
    const values = Array.isArray(raw) ? raw : [raw];
    return {
      supported: true,
      windows: values.flatMap((value) => normalizeWindow(value)),
    };
  } catch {
    return { supported: true, windows: [] };
  }
}

function normalizeWindow(value: unknown): WindowsEditorWindow[] {
  if (!value || typeof value !== "object" || Array.isArray(value)) return [];
  const raw = value as Record<string, unknown>;
  if (typeof raw.hwnd !== "string" || typeof raw.className !== "string" || typeof raw.title !== "string"
    || typeof raw.visible !== "boolean" || typeof raw.enabled !== "boolean" || typeof raw.modal !== "boolean") return [];
  const buttons = Array.isArray(raw.buttons)
    ? raw.buttons.flatMap((button) => button && typeof button === "object" && !Array.isArray(button) && typeof (button as Record<string, unknown>).label === "string"
      ? [{ label: (button as Record<string, unknown>).label as string }]
      : [])
    : [];
  const kind = raw.kind === "launch-errors" || raw.kind === "launch-warning" || raw.kind === "safe-mode" || raw.kind === "unknown-modal" ? raw.kind : undefined;
  return [{ hwnd: raw.hwnd, ownerHwnd: typeof raw.ownerHwnd === "string" ? raw.ownerHwnd : null, className: raw.className, title: raw.title, visible: raw.visible, enabled: raw.enabled, buttons, modal: raw.modal, ...(kind ? { kind } : {}) }];
}

export const WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE = `
$ErrorActionPreference = 'Stop'
try {
  Add-Type -AssemblyName UIAutomationClient
  Add-Type -AssemblyName UIAutomationTypes
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $processCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, __UNITY_CLI_PROCESS_ID__)
  $windowCondition = New-Object System.Windows.Automation.AndCondition($processCondition, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)))
  $buttonCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $candidates = New-Object System.Collections.ArrayList
  foreach ($element in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $processCondition)) { [void]$candidates.Add($element) }
  $topLevelCount = $candidates.Count
  for ($index = 0; $index -lt $topLevelCount; $index++) {
    foreach ($child in $candidates[$index].FindAll([System.Windows.Automation.TreeScope]::Children, $windowCondition)) { [void]$candidates.Add($child) }
  }
  $items = @()
  foreach ($element in $candidates) {
    try {
      $pattern = $null
      $modal = $false
      if ($element.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$pattern)) { $modal = [bool]$pattern.Current.IsModal }
      $title = [string]$element.Current.Name
      $buttons = @()
      if ($modal) {
        $buttonElements = $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
        foreach ($button in $buttonElements) { $buttons += @{ label = [string]$button.Current.Name } }
      }
      $kind = $null
      if ($modal -and $title.IndexOf('Safe Mode', [StringComparison]::OrdinalIgnoreCase) -ge 0) { $kind = 'safe-mode' }
      elseif ($modal -and ($title.IndexOf('Compiler Errors', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $title.IndexOf('Compile Errors', [StringComparison]::OrdinalIgnoreCase) -ge 0)) { $kind = 'launch-errors' }
      elseif ($modal -and ($title.IndexOf('Unity is running as administrator.', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $title.IndexOf('Hold On', [StringComparison]::OrdinalIgnoreCase) -ge 0)) { $kind = 'launch-warning' }
      elseif ($modal) { $kind = 'unknown-modal' }
      $items += @{
        hwnd = '0x' + ([int64]$element.Current.NativeWindowHandle).ToString('X')
        ownerHwnd = $null
        className = [string]$element.Current.ClassName
        title = $title
        visible = -not [bool]$element.Current.IsOffscreen
        enabled = [bool]$element.Current.IsEnabled
        buttons = $buttons
        modal = $modal
        kind = $kind
      }
    } catch { }
  }
  ConvertTo-Json -InputObject @($items) -Compress -Depth 5
} catch { Write-Output '[]' }
`;

function runPowerShell(script: string, timeout: number): Promise<string> {
  return new Promise((resolve) => execFile("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], { timeout, windowsHide: true }, (_error, stdout) => resolve(stdout)));
}
