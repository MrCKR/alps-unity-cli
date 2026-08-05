import assert from "node:assert/strict";
import test from "node:test";
import { EditorBlockerObserver, sanitizeEditorBlockers } from "../src/editor-blocker-observer.js";

const identity = { instanceId: `unity-${"a".repeat(64)}`, canonicalProjectPath: "c:\\repo\\project", displayProjectPath: "C:\\repo\\project" };
const processStartedAt = "2026-07-19T00:00:00.000Z";

test("blocker sanitizer projects only the dialog DTO", () => {
  assert.deepEqual(sanitizeEditorBlockers([{ type: "dialog", dialogId: "dialog_test", title: "Dialog", message: "Body", buttons: ["OK"], requiresUserAction: true, source: "windows-ui", confidence: "exact", hwnd: "0x1" }]), [{ type: "dialog", dialogId: "dialog_test", title: "Dialog", message: "Body", buttons: ["OK"], requiresUserAction: true, source: "windows-ui", confidence: "exact" }]);
});

test("observer binds dialog blockers to the unique PID and start identity", async () => {
  const observer = new EditorBlockerObserver({ now: () => 1_000, inspect: async () => ({ verification: "verified", processIds: [101], processClaims: [{ processId: 101, processStartedAt }] }), probe: async () => ({ supported: true, outcome: "observed-blocked", windows: [{ hwnd: "0x1", ownerHwnd: null, className: "#32770", title: "Dialog", message: "Body", visible: true, enabled: true, modal: true, buttons: [{ label: "&OK", hwnd: "0x2", enabled: true }] }] }) });
  const result = await observer.observe(identity, 2_000, { processId: 101, processStartedAt });
  assert.equal(result.outcome, "observed-blocked");
  assert.deepEqual(result.blockers[0]?.buttons, ["OK"]);
  assert.match(result.blockers[0]?.dialogId ?? "", /^dialog_/);
});
