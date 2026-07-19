import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";

const fixtureRoot = path.join(process.cwd(), "contracts", "stable-session");

/** 读取并解析稳定会话契约 fixture。 */
function readFixture(name: string): Record<string, unknown> {
  return JSON.parse(fs.readFileSync(path.join(fixtureRoot, name), "utf8")) as Record<string, unknown>;
}

test("稳定会话 schema fixture 固定 identity、session、listener 与 absolute deadline", () => {
  const registry = JSON.stringify(readFixture("instance-registry-v2.schema.json"));
  const health = JSON.stringify(readFixture("health-v2.schema.json"));
  const request = JSON.stringify(readFixture("bridge-request-v2.schema.json"));

  for (const field of ["canonicalizationVersion", "canonicalProjectPath", "editorSessionId", "listenerId", "actualPort"]) {
    assert.match(registry, new RegExp(field));
    assert.match(health, new RegExp(field));
  }
  assert.match(request, /absoluteDeadlineUtc/);
  assert.match(request, /requestDigestVersion/);
  assert.doesNotMatch(request, /retryCount|bridgeEndpoint|port/);
});

test("request journal schema 固定 durability barrier 且不持久化原始 input", () => {
  const journal = JSON.stringify(readFixture("request-journal-v1.schema.json"));
  const digest = readFixture("request-digest-v1-golden.json") as { algorithm: string; sha256: string };
  for (const phase of ["received", "accepted_before_effect", "effect_started", "effect_proven", "result_persisted"]) assert.match(journal, new RegExp(phase));
  for (const field of ["requestDigest", "completionDriverId", "provenEnvelope", "terminalEnvelope"]) assert.match(journal, new RegExp(field));
  assert.doesNotMatch(journal, /sourceText|securityProof|requestBody|password|token/);
  assert.match(digest.algorithm, /exactRequestBodyBytes/);
  assert.match(digest.sha256, /^[0-9a-f]{64}$/);
});

test("editor.state 公开 schema 不泄漏 transport generation", () => {
  const state = JSON.stringify(readFixture("editor-state.schema.json"));
  for (const field of ["ready", "transitioning", "blocked", "offline", "conflict", "freshness", "unknownFields"]) {
    assert.match(state, new RegExp(field));
  }
  assert.doesNotMatch(state, /endpoint|listenerId|actualPort|preferredPort|journalPhase/);
});

test("editor.state progress and blocker kinds remain extensible non-empty strings", () => {
  const schema = readFixture("editor-state.schema.json") as { $defs: { progress: { properties: { kind: unknown } }; blocker: { properties: { kind: unknown } } } };
  assert.deepEqual(schema.$defs.progress.properties.kind, { type: "string", minLength: 1 });
  assert.deepEqual(schema.$defs.blocker.properties.kind, { type: "string", minLength: 1 });
});

test("direct-call semantics 固定 immutable request、单 generation read 与 observeNow", () => {
  const semantics = readFixture("direct-call-semantics.json") as {
    request: { digest: string; redelivery: string };
    contextRead: { ordinary: { journal: boolean; completion: string }; "editor.state": { budgetMs: number; bridgeProbeBudgetMs: number; journal: boolean } };
    mutationRecovery: { disconnect: string; uncertainEffect: string };
    deferredDelivery: { commands: string[] };
  };
  assert.match(semantics.request.digest, /exactRequestBodyBytes/);
  assert.match(semantics.request.redelivery, /byte-identical/);
  assert.equal(semantics.contextRead.ordinary.journal, false);
  assert.match(semantics.contextRead.ordinary.completion, /one validated listener generation/);
  assert.equal(semantics.contextRead["editor.state"].budgetMs, 1000);
  assert.equal(semantics.contextRead["editor.state"].bridgeProbeBudgetMs, 300);
  assert.equal(semantics.contextRead["editor.state"].journal, false);
  assert.match(semantics.mutationRecovery.disconnect, /does not cancel/);
  assert.match(semantics.mutationRecovery.uncertainEffect, /E_REQUEST_OUTCOME_UNKNOWN/);
  assert.deepEqual(semantics.deferredDelivery.commands, ["jobs", "--job", "batch"]);
});

test("removal map 覆盖冻结的旧 selector、state 与 compile 双轨", () => {
  const map = readFixture("removal-map.json") as { entries: Array<{ legacy: string }> };
  const legacy = map.entries.map((entry) => entry.legacy).join("\n");
  for (const required of ["TargetConfig.activeTarget", "--target", "--bridge-endpoint", "public editor.state Tool", "editor.compile-diagnostics", "editor.compile.waitReady"]) {
    assert.match(legacy, new RegExp(required.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
});
