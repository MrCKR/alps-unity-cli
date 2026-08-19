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
  const registry = JSON.stringify(readFixture("instance-registry-v3.schema.json"));
  const health = JSON.stringify(readFixture("health-v3.schema.json"));
  const request = JSON.stringify(readFixture("bridge-request-v2.schema.json"));

  for (const field of ["canonicalizationVersion", "canonicalProjectPath", "editorSessionId", "listenerId", "actualPort"]) {
    assert.match(registry, new RegExp(field));
    assert.match(health, new RegExp(field));
  }
  assert.match(request, /absoluteDeadlineUtc/);
  assert.match(request, /requestDigestVersion/);
  assert.doesNotMatch(request, /retryCount|bridgeEndpoint|port/);
});

test("request digest fixture binds the exact direct-call bytes", () => {
  const digest = readFixture("request-digest-v1-golden.json") as { algorithm: string; sha256: string };
  assert.match(digest.algorithm, /exactRequestBodyBytes/);
  assert.match(digest.sha256, /^[0-9a-f]{64}$/);
});

test("editor.state 公开 schema 不泄漏 transport generation", () => {
  const state = JSON.stringify(readFixture("editor-state.schema.json"));
  for (const field of ["ready", "transitioning", "blocked", "unresponsive", "unverifiable", "offline", "conflict", "freshness"]) {
    assert.match(state, new RegExp(field));
  }
  assert.doesNotMatch(state, /endpoint|listenerId|actualPort|preferredPort|journalPhase/);
});

test("editor.state progress kind remains extensible and blockers are dialog DTOs", () => {
  const schema = readFixture("editor-state.schema.json") as { $defs: { progress: { properties: { kind: unknown } }; blocker: { properties: { type: unknown; dialogId: unknown } } } };
  assert.deepEqual(schema.$defs.progress.properties.kind, { type: "string", minLength: 1 });
  assert.deepEqual(schema.$defs.blocker.properties.type, { const: "dialog" });
  assert.deepEqual(schema.$defs.blocker.properties.dialogId, { type: "string", minLength: 1 });
});

test("direct-call semantics 固定 immutable request、单 generation read 与 observeNow", () => {
  const semantics = readFixture("direct-call-semantics.json") as {
    request: { digest: string; redelivery: string };
    contextRead: { ordinary: { journal: boolean; completion: string }; "editor.state": { budgetMs: number; bridgeProbeBudgetMs: number; journal: boolean } };
    directCall: { terminal: string; timeout: string; replay: string; audit: string };
    deferredDelivery?: unknown;
  };
  assert.match(semantics.request.digest, /exactRequestBodyBytes/);
  assert.match(semantics.request.redelivery, /editorSessionId is unchanged/);
  assert.equal(semantics.contextRead.ordinary.journal, false);
  assert.match(semantics.contextRead.ordinary.completion, /one validated listener generation/);
  assert.equal(semantics.contextRead["editor.state"].budgetMs, 4000);
  assert.equal(semantics.contextRead["editor.state"].bridgeProbeBudgetMs, 3500);
  assert.equal(semantics.contextRead["editor.state"].journal, false);
  assert.match(semantics.directCall.terminal, /terminal business result/);
  assert.match(semantics.directCall.timeout, /E_EXECUTION_TIMEOUT/);
  assert.match(semantics.directCall.replay, /cross-session mutation redelivery is forbidden/);
  assert.match(semantics.directCall.audit, /session-level append log/);
  assert.equal(semantics.deferredDelivery, undefined);
  assert.doesNotMatch(JSON.stringify(semantics), /\bjobs\b|--job|\bbatch\b/);
});

test("removal map 覆盖冻结的旧 selector、state 与 compile 双轨", () => {
  const map = readFixture("removal-map.json") as { entries: Array<{ legacy: string }> };
  const legacy = map.entries.map((entry) => entry.legacy).join("\n");
  for (const required of ["TargetConfig.activeTarget", "--target", "--bridge-endpoint", "public editor.state Tool", "editor.compile-diagnostics", "editor.compile.waitReady"]) {
    assert.match(legacy, new RegExp(required.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
});
