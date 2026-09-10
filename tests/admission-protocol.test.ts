import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { rootAdmissionFingerprint, type RootAdmissionRequest } from "../src/bridge/admission.js";
import { freezeRequest } from "../src/bridge/session-supervisor.js";

const instanceA = `unity-${"a".repeat(64)}`;
const instanceB = `unity-${"b".repeat(64)}`;

function request(overrides: Partial<RootAdmissionRequest> = {}): RootAdmissionRequest {
  return {
    requestId: "req_00000000-0000-4000-8000-000000000001",
    command: "scene.hierarchy",
    input: { query: "Camera", options: { depth: 2, enabled: true } },
    dryRun: false,
    target: "editor",
    completionContractIdentity: "completion-contract-a",
    timeoutMs: 30_000,
    admissionAbsoluteDeadlineMs: 31_000,
    ...overrides,
  };
}

test("root admission fingerprint binds business, instance, target, dry-run, and completion identity", () => {
  const base = request();
  const fingerprint = rootAdmissionFingerprint(base, instanceA);
  assert.match(fingerprint, /^[a-f0-9]{64}$/);
  assert.notEqual(rootAdmissionFingerprint({ ...base, command: "scene.active" }, instanceA), fingerprint);
  assert.notEqual(rootAdmissionFingerprint({ ...base, input: { query: "Light" } }, instanceA), fingerprint);
  assert.notEqual(rootAdmissionFingerprint({ ...base, dryRun: true }, instanceA), fingerprint);
  assert.notEqual(rootAdmissionFingerprint({ ...base, target: "runtime" }, instanceA), fingerprint);
  assert.notEqual(rootAdmissionFingerprint({ ...base, completionContractIdentity: "completion-contract-b" }, instanceA), fingerprint);
  assert.notEqual(rootAdmissionFingerprint({ ...base, captureDiscoveryProvenance: true }, instanceA), fingerprint);
  assert.notEqual(rootAdmissionFingerprint({ ...base, expectedDiscoveryProvenance: {
    target: "editor", instanceId: instanceA, sessionId: `session-${"c".repeat(64)}`,
    editorSessionId: `session-${"c".repeat(64)}`, listenerId: `listener-${"d".repeat(32)}`, listenerGeneration: 2,
  } }, instanceA), fingerprint);
  assert.notEqual(rootAdmissionFingerprint(base, instanceB), fingerprint);
});

test("root admission fingerprint is canonical and excludes both deadlines", () => {
  const left = request({ input: { z: 1, nested: { b: true, a: 2 }, a: "first" }, timeoutMs: 1, admissionAbsoluteDeadlineMs: 2 });
  const right = request({ input: { a: "first", nested: { a: 2, b: true }, z: 1 }, timeoutMs: 999_999, admissionAbsoluteDeadlineMs: 888_888 });
  assert.equal(rootAdmissionFingerprint(left, instanceA), rootAdmissionFingerprint(right, instanceA));
});

test("immutable Bridge request and digest contain no admission token", async () => {
  const frozen = freezeRequest({ requestId: request().requestId, command: "catalog.editor.run", input: { name: "scene.hierarchy", input: {} }, timeoutMs: 30_000 }, instanceA, 50_000);
  assert.doesNotMatch(frozen.body, /admission|token/i);
  assert.equal(Object.keys(frozen.payload.execution).sort().join(","), "absoluteDeadlineUtc,completionContractVersion,requestDigestVersion");
  const source = await readFile("src/bridge/session-supervisor.ts", "utf8");
  assert.match(source, /x-unity-cli-admission-token/);
  assert.match(source, /\/bridge\/admission\/(?:acquire|revoke)/);
});
