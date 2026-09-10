import assert from "node:assert/strict";
import test from "node:test";
import { EditorBlockerObserver, sanitizeEditorBlockers } from "../src/editor-blocker-observer.js";

const identity = { instanceId: `unity-${"a".repeat(64)}`, canonicalProjectPath: "c:\\repo\\project", displayProjectPath: "C:\\repo\\project" };
const processStartedAt = "2026-07-19T00:00:00.000Z";

for (const verification of ["verified", "dead", "unverifiable"] as const) {
  test(`observer verifies the registry process when projectPath claims are absent: ${verification}`, async () => {
    let probes = 0;
    const session = { processId: 101, processStartedAt };
    const observer = new EditorBlockerObserver({
      now: () => 1_000,
      inspect: async () => ({ verification: "verified", processIds: [], processClaims: [] }),
      verifySession: async (actualIdentity, actualSession, budget) => {
        assert.deepEqual(actualIdentity, identity);
        assert.deepEqual(actualSession, session);
        assert.equal(budget, 1_000);
        return verification;
      },
      probe: async (processId, budget) => {
        probes += 1;
        assert.equal(processId, session.processId);
        assert.equal(budget, 1_000);
        return { supported: true, outcome: "observed-empty", windows: [] };
      },
    });
    const result = await observer.observe(identity, 2_000, session);
    assert.equal(result.outcome, verification === "verified" ? "observed-empty" : verification === "dead" ? "offline" : "unproven");
    assert.equal(probes, verification === "verified" ? 1 : 0);
  });
}

test("observer stops at the original deadline after process verification", async () => {
  let now = 1_000;
  const observer = new EditorBlockerObserver({
    now: () => now,
    inspect: async () => ({ verification: "verified", processIds: [] }),
    verifySession: async () => { now = 2_000; return "verified"; },
    probe: async () => { throw new Error("expired observation must not probe UI"); },
  });
  assert.deepEqual(await observer.observe(identity, 2_000, { processId: 101, processStartedAt }), {
    outcome: "unproven", blockers: [], reason: "deadline",
  });
});

test("observer does not replace contradictory claims with a registry process", async () => {
  const cases = [
    { claims: { verification: "verified" as const, processIds: [101, 102] }, outcome: "conflict" },
    { claims: { verification: "unverifiable" as const, processIds: [] }, outcome: "unproven" },
    { claims: { verification: "verified" as const, processIds: [102], processClaims: [{ processId: 102, processStartedAt }] }, outcome: "unproven" },
    { claims: { verification: "verified" as const, processIds: [101], processClaims: [{ processId: 101, processStartedAt: "2026-08-01T00:00:00.000Z" }] }, outcome: "unproven" },
  ];
  for (const entry of cases) {
    const observer = new EditorBlockerObserver({
      now: () => 1_000,
      inspect: async () => entry.claims,
      verifySession: async () => { throw new Error("contradictory claims must not be overridden"); },
      probe: async () => { throw new Error("unproven identity must not probe UI"); },
    });
    assert.equal((await observer.observe(identity, 2_000, { processId: 101, processStartedAt })).outcome, entry.outcome);
  }
});

test("observer without a registry session cannot infer an unclaimed process", async () => {
  const observer = new EditorBlockerObserver({
    now: () => 1_000,
    inspect: async () => ({ verification: "verified", processIds: [] }),
    verifySession: async () => { throw new Error("no registry identity to verify"); },
    probe: async () => { throw new Error("no process to probe"); },
  });
  assert.equal((await observer.observe(identity, 2_000)).outcome, "offline");
});

test("blocker sanitizer projects only the dialog DTO", () => {
  assert.deepEqual(sanitizeEditorBlockers([{ type: "dialog", dialogId: "dialog_test", title: "Dialog", message: "Body", buttons: ["OK"], requiresUserAction: true, source: "windows-ui", confidence: "exact", hwnd: "0x1" }]), [{ type: "dialog", dialogId: "dialog_test", title: "Dialog", message: "Body", buttons: ["OK"], requiresUserAction: true, source: "windows-ui", confidence: "exact" }]);
});

for (const hasProjectPath of [true, false]) {
  test(`observer binds dialog blockers to the unique PID and start identity: projectPath=${hasProjectPath}`, async () => {
    const observer = new EditorBlockerObserver({
      now: () => 1_000,
      inspect: async () => hasProjectPath
        ? { verification: "verified", processIds: [101], processClaims: [{ processId: 101, processStartedAt }] }
        : { verification: "verified", processIds: [] },
      verifySession: async () => "verified",
      probe: async () => ({ supported: true, outcome: "observed-blocked", windows: [{ hwnd: "0x1", ownerHwnd: null, className: "#32770", title: "Dialog", message: "Body", visible: true, enabled: true, modal: true, buttons: [{ label: "&OK", hwnd: "0x2", enabled: true }] }] }),
    });
    const result = await observer.observe(identity, 2_000, { processId: 101, processStartedAt });
    assert.equal(result.outcome, "observed-blocked");
    assert.deepEqual(result.blockers[0]?.buttons, ["OK"]);
    assert.match(result.blockers[0]?.dialogId ?? "", /^dialog_/);
  });
}
