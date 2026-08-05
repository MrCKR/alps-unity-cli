import assert from "node:assert/strict";
import test from "node:test";
import { WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE, buildWindowsEditorStateScript, parseWindowsEditorStateOutput } from "../src/windows-editor-state.js";

test("Windows editor state parser requires dialog body and button hwnd/enabled state", () => {
  const probe = parseWindowsEditorStateOutput(JSON.stringify({ hwnd: "0x1", ownerHwnd: null, className: "#32770", title: "Dialog", message: "Body", visible: true, enabled: true, modal: true, buttons: [{ label: "Continue", hwnd: "0x2", enabled: false }] }));
  assert.equal(probe.windows[0]?.message, "Body");
  assert.deepEqual(probe.windows[0]?.buttons[0], { label: "Continue", hwnd: "0x2", enabled: false });
  assert.equal(parseWindowsEditorStateOutput("[]").outcome, "observed-empty");
  assert.equal(parseWindowsEditorStateOutput("not-json").outcome, "unproven");
});

test("Windows editor state parser accepts non-modal windows with explicit empty buttons", () => {
  const probe = parseWindowsEditorStateOutput(JSON.stringify({ hwnd: "0x1", ownerHwnd: null, className: "UnityContainerWndClass", title: "Unity", message: "", visible: true, enabled: true, modal: false, buttons: [] }));
  assert.equal(probe.outcome, "observed-empty");
  assert.deepEqual(probe.windows[0]?.buttons, []);
});

test("Windows editor state probe is PID-scoped and read-only", () => {
  assert.match(buildWindowsEditorStateScript(44960), /targetProcessId=\[uint32\]44960/);
  assert.match(WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE, /EnumChildWindows/);
  assert.match(WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE, /public static string Message/);
  assert.match(WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE, /Add-Type -AssemblyName UIAutomationClient/);
  assert.match(WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE, /AutomationElement\]::FromHandle/);
  assert.match(WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE, /\$buttons=\[object\[\]\]@\(\)/);
  assert.match(WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE, /buttons=\$buttons/);
  assert.match(WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE, /enabled=IsWindowEnabled/);
  for (const forbidden of ["SendMessage", "BM_CLICK", "mouse_event", "keybd_event"]) assert.doesNotMatch(WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE, new RegExp(forbidden));
});
