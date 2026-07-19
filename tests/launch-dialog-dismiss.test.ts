import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import test from "node:test";
import { buildWindowsDismissScript, DISMISS_BUTTON_LABEL, dismissUnityLaunchDialog, LAUNCH_DIALOG_BUTTON_LABELS, LAUNCH_DIALOG_RULES, LAUNCH_DIALOG_TITLE_FRAGMENTS, parseDismissOutput, parseUnityProcessDiscoveryOutput, WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, WINDOWS_UNITY_PROCESS_DISCOVERY_SCRIPT } from "../src/launch-dialog-dismiss.js";

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

test("parseDismissOutput distinguishes a read-only allowlist probe", () => {
  const button = Buffer.from("I wish to continue at my own risk", "utf8").toString("base64");
  const title = Buffer.from("Unity is running as administrator.", "utf8").toString("base64");
  assert.deepEqual(parseDismissOutput(`found:90096:${button}:${title}`), {
    kind: "found",
    processId: 90096,
    button: "I wish to continue at my own risk",
    title: "Unity is running as administrator."
  });
});

test("parseDismissOutput handles platform errors", () => {
  assert.deepEqual(parseDismissOutput("error:Access denied"), { kind: "error", message: "Access denied" });
  assert.deepEqual(parseDismissOutput("noise"), { kind: "not-found" });
});

test("parseDismissOutput preserves an unproven click when the target dialog remains visible", () => {
  const message = Buffer.from("The clicked Unity launch dialog is still visible after BM_CLICK.", "utf8").toString("base64");
  assert.deepEqual(parseDismissOutput(`unproven:${message}`), {
    kind: "unproven",
    message: "The clicked Unity launch dialog is still visible after BM_CLICK."
  });
});

test("launch dialog fragments and buttons include current Unity blockers", () => {
  const realTitle = "Enter Safe Mode?";
  assert.ok(LAUNCH_DIALOG_TITLE_FRAGMENTS.some((fragment) => realTitle.toLowerCase().includes(fragment.toLowerCase())));
  assert.ok(LAUNCH_DIALOG_TITLE_FRAGMENTS.map((fragment) => fragment.toLowerCase()).includes("compiler errors"));
  assert.ok(LAUNCH_DIALOG_TITLE_FRAGMENTS.includes("Unity is running as administrator."));
  assert.ok(LAUNCH_DIALOG_BUTTON_LABELS.includes("I wish to continue at my own risk"));
  assert.deepEqual(LAUNCH_DIALOG_RULES[1], {
    titleFragments: ["Unity is running as administrator."],
    buttonLabels: ["I wish to continue at my own risk"]
  });
});

test("Unity process discovery keeps only non-batch main processes with structured project argv", () => {
  const output = JSON.stringify([
    { processId: 101, executablePath: "C:\\Unity\\Unity.exe", argv: ["Unity.exe", "-projectPath", "D:/Project"] },
    { processId: 102, executablePath: "C:\\Unity\\Unity.exe", argv: ["Unity.exe", "-batchMode", "-projectPath", "D:/Project"] },
    { processId: 103, executablePath: "C:\\Unity\\Unity.exe", argv: ["Unity.exe", "-projectPath"] },
    { processId: 104, executablePath: "C:\\Unity\\Other.exe", argv: ["Other.exe", "-projectPath", "D:/Project"] }
  ]);
  assert.deepEqual(parseUnityProcessDiscoveryOutput(output), [
    { processId: 101, executablePath: "C:\\Unity\\Unity.exe", argv: ["Unity.exe", "-projectPath", "D:/Project"], projectPath: "D:/Project" }
  ]);
  assert.match(WINDOWS_UNITY_PROCESS_DISCOVERY_SCRIPT, /CommandLineToArgvW/);
  assert.match(WINDOWS_UNITY_PROCESS_DISCOVERY_SCRIPT, /Name='Unity\.exe'/);
});

test("offline launch recovery binds canonical identity before PID-scoped dismissal", async () => {
  const [tool, dispatcher] = await Promise.all([
    readFile(resolve("src/tools/editor/launch-dialog-dismiss.ts"), "utf8"),
    readFile(resolve("src/tools/dispatcher.ts"), "utf8")
  ]);
  assert.match(tool, /selectObservationInstanceForFlags/);
  assert.match(tool, /findUnityProjectProcesses/);
  assert.match(tool, /probeUnityLaunchDialog[\s\S]*commitEffect[\s\S]*dismissUnityLaunchDialog/);
  assert.doesNotMatch(tool, /commitEffect[\s\S]*probeUnityLaunchDialog/);
  assert.doesNotMatch(tool, /selectInstanceForFlags/);
  assert.match(dispatcher, /tool\.name === "editor\.launch-dialog\.dismiss"[\s\S]*currentInstanceProjectPath/);
});

test("Windows dismiss script uses one supplied Unity PID and BM_CLICK", () => {
  assert.match(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, /Get-Process -Id \$_/);
  assert.doesNotMatch(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, /Get-Process -Name/);
  assert.match(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, /GetWindowThreadProcessId/);
  assert.match(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, /TryDismiss\(int\[\] unityPids, bool probeOnly\)/);
  assert.match(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, /if \(probeOnly\) return "found:/);
  assert.match(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, /0x00F5/);
  assert.match(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, /targetStillVisible/);
  assert.match(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, /matchedButtonStillPresent/);
  assert.match(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, /proofAttempt < 20/);
  assert.match(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, /Thread\.Sleep\(100\)/);
  assert.match(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, /hWnd != matchedDialog/);
  assert.match(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, new RegExp(DISMISS_BUTTON_LABEL));
  for (const label of LAUNCH_DIALOG_BUTTON_LABELS) assert.match(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, new RegExp(label));
  for (const fragment of LAUNCH_DIALOG_TITLE_FRAGMENTS) {
    assert.match(WINDOWS_DISMISS_PS_SCRIPT_TEMPLATE, new RegExp(fragment.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
});

test("buildWindowsDismissScript can limit scanning to one process id and probe without clicking", () => {
  const script = buildWindowsDismissScript(44960, true);
  assert.match(script, /\$targetPids = @\(44960\)/);
  assert.match(script, /\$probeOnly = \$true/);
  assert.doesNotMatch(script, /__UNITY_CLI_TARGET_PIDS__|__UNITY_CLI_PROBE_ONLY__/);
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
