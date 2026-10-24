import assert from "node:assert/strict";
import test from "node:test";
import { CLICK_DIALOG_MIN_EXECUTION_BUDGET_MS, DIALOG_CLICK_PRE_EFFECT_FAILURES, buildClickDialogScript, clickObservedDialog, dialogId, normalizeDialogButton, parseClickDialogOutput, publicDialog, type ObservedEditorDialog } from "../src/editor-dialogs.js";
import clickDialogTool, { dialogClickFailureError } from "../src/tools/editor/dialog/click.js";
import { parseWindowsEditorStateOutput } from "../src/windows-editor-state.js";

test("dialogId is deterministic and never exposes the native hwnd", () => {
  const id = dialogId(41920, "2026-07-19T00:00:00.000Z", "0x1A2B");
  assert.match(id, /^dialog_[a-f0-9]{64}$/);
  assert.equal(id, dialogId(41920, "2026-07-19T00:00:00.000Z", "0x1A2B"));
  assert.doesNotMatch(id, /1A2B|41920/);
});

const observedDialog: ObservedEditorDialog = {
  dialogId: "dialog_test", title: "Save changes?", message: "Save changes?", modal: true, buttons: ["Save"], hwnd: "0x1", ownerHwnd: null,
  processId: 1, processStartedAt: "2026-07-19T00:00:00.000Z", buttonTargets: [{ label: "Save", hwnd: "0x2", enabled: true }],
};

test("dialog click tool reserves its default budget and describes mechanism-independent proof", () => {
  assert.equal(clickDialogTool.timeoutMs, 30_000);
  assert.match(clickDialogTool.description, /exact enabled button label/);
  assert.match(clickDialogTool.description, /dialog disappeared/);
  assert.match(clickDialogTool.description, /modal/);
  assert.doesNotMatch(clickDialogTool.description, /BM_CLICK|InvokePattern|UI Automation/);
});

test("dialog click allocates the remaining deadline instead of a fixed three-second timeout", async () => {
  let receivedTimeoutMs = 0;
  const result = await clickObservedDialog(observedDialog, observedDialog.buttonTargets[0]!, Date.now() + 12_000, async (script, timeoutMs) => {
    receivedTimeoutMs = timeoutMs;
    assert.match(script, /\$i -lt 50/);
    return { stdout: "clicked\n" };
  });
  assert.deepEqual(result, { outcome: "clicked" });
  assert.ok(receivedTimeoutMs >= CLICK_DIALOG_MIN_EXECUTION_BUDGET_MS);
});

test("dialog click output parser recognizes terminal states and isolates unproven diagnostics", () => {
  assert.deepEqual(parseClickDialogOutput("clicked\n"), { outcome: "clicked" });
  assert.deepEqual(parseClickDialogOutput("stale\n"), { outcome: "stale" });
  assert.deepEqual(parseClickDialogOutput("button-not-found\n"), { outcome: "button-not-found" });
  for (const failure of ["rect-unavailable", "cursor-unavailable", "foreground-refused", "foreground-not-acquired", "cursor-not-moved", "input-rejected", "not-proven"]) {
    assert.deepEqual(parseClickDialogOutput(`failed:${failure}\n`), { outcome: "unproven", failure });
  }
  assert.deepEqual(parseClickDialogOutput("failed:unknown-token\n"), { outcome: "unproven", reason: "parse" });
  assert.deepEqual(parseClickDialogOutput("unproven\n"), { outcome: "unproven", reason: "parse" });
  assert.deepEqual(parseClickDialogOutput("native exception 0x123\n"), { outcome: "unproven", reason: "parse" });
});

test("dialog click failure mapping separates provable no-effect failures from unknown-effect ones", () => {
  for (const failure of ["rect-unavailable", "cursor-unavailable", "foreground-refused", "foreground-not-acquired", "cursor-not-moved"] as const) {
    assert.equal(DIALOG_CLICK_PRE_EFFECT_FAILURES.has(failure), true);
    const error = dialogClickFailureError({ outcome: "unproven", failure }, ["recover"]);
    assert.equal(error.code, "E_DIALOG_CLICK_SUPPRESSED");
    assert.equal(error.transient, true);
    assert.equal(error.retryAfterMs, 250);
    assert.deepEqual(error.details, { reason: failure });
    assert.match(error.nextActions.join(" "), /Replay the same dialogId and button/);
  }
  for (const failure of ["input-rejected", "not-proven"] as const) {
    assert.equal(DIALOG_CLICK_PRE_EFFECT_FAILURES.has(failure), false);
    const error = dialogClickFailureError({ outcome: "unproven", failure }, ["recover"]);
    assert.equal(error.code, "E_DIALOG_CLICK_UNPROVEN");
    assert.notEqual(error.transient, true);
    assert.deepEqual(error.details, { reason: failure });
    assert.deepEqual(error.nextActions, ["recover"]);
  }
  const unparsed = dialogClickFailureError({ outcome: "unproven", reason: "timeout" }, ["recover"]);
  assert.equal(unparsed.code, "E_DIALOG_CLICK_UNPROVEN");
  assert.deepEqual(unparsed.details, { reason: "timeout" });
});

test("dialog click runner failure reasons remain stable and do not expose script inputs", async () => {
  const script = buildClickDialogScript(observedDialog, observedDialog.buttonTargets[0]!);
  assert.doesNotMatch(script, /\$pid\s*=/i);
  assert.match(script, /\$targetProcessId=\[uint32\]1/);
  assert.match(script, /ProcessId="\+\$targetProcessId/);
  assert.match(script, /ToUniversalTime\(\)\.ToString\('yyyy-MM-ddTHH:mm:ss\.fffZ',\[Globalization\.CultureInfo\]::InvariantCulture\)/);
  assert.doesNotMatch(script, /ToString\('o'\)/);
  assert.match(script, /\$dialogPid -ne \$targetProcessId/);
  assert.match(script, /\$buttonPid -ne \$targetProcessId/);
  assert.match(script, /GetWindowRect/);
  assert.match(script, /GetCursorPos/);
  assert.match(script, /SetForegroundWindow/);
  assert.match(script, /SetCursorPos/);
  assert.match(script, /SendInput/);
  assert.match(script, /dwFlags=0x0002/);
  assert.match(script, /dwFlags=0x0004/);
  assert.doesNotMatch(script, /BM_CLICK|0x00F5|SendMessageW|UIAutomationClient|InvokePattern/);
  const timedOut = await clickObservedDialog(observedDialog, observedDialog.buttonTargets[0]!, Date.now() + 12_000, async () => ({ stdout: "", reason: "timeout" }));
  const exited = await clickObservedDialog(observedDialog, observedDialog.buttonTargets[0]!, Date.now() + 12_000, async () => ({ stdout: "", reason: "exit" }));
  assert.deepEqual(timedOut, { outcome: "unproven", reason: "timeout" });
  assert.deepEqual(exited, { outcome: "unproven", reason: "exit" });
});

test("Windows dialog probe preserves standard dialog body and enabled button targets internally", () => {
  const message = "Do you want to save the changes you made in the scenes: MatrixScene Your changes will be lost if you don't save them.";
  const probe = parseWindowsEditorStateOutput(JSON.stringify({ hwnd: "0x1", ownerHwnd: null, className: "#32770", title: "Scene(s) Have Been Modified", message, visible: true, enabled: true, modal: true, buttons: [{ label: "&Save", hwnd: "0x2", enabled: true }, { label: "&Don't Save", hwnd: "0x3", enabled: true }, { label: "Cancel", hwnd: "0x4", enabled: false }] }));
  assert.equal(probe.outcome, "observed-blocked");
  assert.equal(probe.windows[0]?.message, message);
  assert.deepEqual(probe.windows[0]?.buttons, [{ label: "&Save", hwnd: "0x2", enabled: true }, { label: "&Don't Save", hwnd: "0x3", enabled: true }, { label: "Cancel", hwnd: "0x4", enabled: false }]);
  const projected = publicDialog({ dialogId: "dialog_test", title: "Scene(s) Have Been Modified", message: "Save changes?", modal: true, buttons: ["Save", "Cancel"], hwnd: "0x1", ownerHwnd: null, processId: 1, processStartedAt: "2026-07-19T00:00:00.000Z", buttonTargets: [] });
  assert.deepEqual(projected, { dialogId: "dialog_test", title: "Scene(s) Have Been Modified", message: "Save changes?", modal: true, buttons: ["Save", "Cancel"] });
  assert.equal(normalizeDialogButton("&Don't &Save"), "Don't Save");
});
