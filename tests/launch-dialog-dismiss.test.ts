import assert from "node:assert/strict";
import test from "node:test";
import { buildWindowsDismissScript, DISMISS_BUTTON_LABEL, dismissUnityLaunchDialog, LAUNCH_DIALOG_TITLE_FRAGMENTS, parseDismissOutput, WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE } from "../src/launch-dialog-dismiss.js";

test("parseDismissOutput handles empty and not-found output", () => {
  assert.deepEqual(parseDismissOutput(""), { kind: "not-found" });
  assert.deepEqual(parseDismissOutput("not-found\r\n"), { kind: "not-found" });
});

test("parseDismissOutput reads the last non-empty contract line", () => {
  const button = Buffer.from("Ignore", "utf8").toString("base64");
  const title = Buffer.from("Enter Safe Mode?", "utf8").toString("base64");
  assert.deepEqual(parseDismissOutput(`WARNING\r\ndismissed:44960:${button}:${title}\r\n`), {
    kind: "dismissed",
    processId: 44960,
    button: "Ignore",
    title: "Enter Safe Mode?"
  });
});

test("parseDismissOutput handles platform errors", () => {
  assert.deepEqual(parseDismissOutput("error:Access denied"), { kind: "error", message: "Access denied" });
  assert.deepEqual(parseDismissOutput("noise"), { kind: "not-found" });
});

test("launch dialog fragments include current Unity Safe Mode wording", () => {
  const realTitle = "Enter Safe Mode?";
  assert.ok(LAUNCH_DIALOG_TITLE_FRAGMENTS.some((fragment) => realTitle.toLowerCase().includes(fragment.toLowerCase())));
  assert.ok(LAUNCH_DIALOG_TITLE_FRAGMENTS.map((fragment) => fragment.toLowerCase()).includes("compiler errors"));
});

test("Windows dismiss script uses Unity PID scoping and BM_CLICK", () => {
  assert.match(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, /Get-Process -Name 'Unity'/);
  assert.match(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, /GetWindowThreadProcessId/);
  assert.match(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, /0x00F5/);
  assert.match(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, new RegExp(DISMISS_BUTTON_LABEL));
  for (const fragment of LAUNCH_DIALOG_TITLE_FRAGMENTS) {
    assert.match(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, new RegExp(fragment.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
});

test("buildWindowsDismissScript can limit scanning to one process id", () => {
  const script = buildWindowsDismissScript(44960);
  assert.match(script, /\$targetPids = @\(44960\)/);
  assert.doesNotMatch(script, /__UNITY_CLI_TARGET_PIDS__/);
});

test("dismissUnityLaunchDialog timeoutMs 0 is a no-op probe on Windows", async () => {
  const result = await dismissUnityLaunchDialog({ platform: "win32", timeoutMs: 0, processId: 44960 });
  assert.equal(result.supported, true);
  assert.equal(result.dismissed, false);
  assert.equal(result.status, "not_found");
  assert.equal(result.attempts, 0);
  assert.equal(result.processId, 44960);
});

test("dismissUnityLaunchDialog reports unsupported non-Windows platforms", async () => {
  const result = await dismissUnityLaunchDialog({ platform: "linux", timeoutMs: 0 });
  assert.equal(result.supported, false);
  assert.equal(result.status, "unsupported");
  assert.equal(result.dismissed, false);
});
