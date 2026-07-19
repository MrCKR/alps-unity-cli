import assert from "node:assert/strict";
import test from "node:test";
import { WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE, buildWindowsEditorStateScript, parseWindowsEditorStateOutput } from "../src/windows-editor-state.js";

test("Windows editor state probe parses unknown owned modals without an action channel", () => {
  const probe = parseWindowsEditorStateOutput(JSON.stringify([{
    hwnd: "0x1", ownerHwnd: "0x2", className: "#32770", title: "Localized dialog", visible: true, enabled: true,
    buttons: [{ label: "Continue" }], modal: true, kind: "unknown-modal"
  }]));
  assert.equal(probe.windows[0]?.kind, "unknown-modal");
  assert.equal(probe.windows[0]?.buttons[0]?.label, "Continue");
});

test("Windows editor state parser accepts a singleton object and exact launch warnings", () => {
  const probe = parseWindowsEditorStateOutput(JSON.stringify({
    hwnd: "0x1", ownerHwnd: null, className: "UnityWndClass", title: "Unity", visible: true, enabled: true,
    buttons: [], modal: false,
  }));
  assert.equal(probe.windows.length, 1);
  const warning = parseWindowsEditorStateOutput(JSON.stringify({
    hwnd: "0x2", ownerHwnd: null, className: "#32770", title: "Unity is running as administrator.", visible: true, enabled: true,
    buttons: [{ label: "I wish to continue at my own risk" }], modal: true, kind: "launch-warning",
  }));
  assert.equal(warning.windows[0]?.kind, "launch-warning");
  assert.match(WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE, /UIAutomationClient/);
  assert.match(WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE, /WindowPattern/);
  assert.match(WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE, /topLevelCount[\s\S]*TreeScope\]::Children, \$windowCondition/);
  assert.match(WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE, /ConvertTo-Json -InputObject @\(\$items\)/);
});

test("Windows editor state probe is PID-scoped and read-only", () => {
  assert.match(buildWindowsEditorStateScript(44960), /ProcessIdProperty, 44960/);
  for (const forbidden of ["SendMessage", "BM_CLICK", "mouse_event", "keybd_event"]) assert.doesNotMatch(WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE, new RegExp(forbidden));
});
