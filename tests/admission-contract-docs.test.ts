import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const contractRoot = "contracts/stable-session";
const json = async (name: string) => JSON.parse(await readFile(`${contractRoot}/${name}`, "utf8")) as Record<string, any>;

test("admission and Runtime sidecar schemas are strict current contracts", async () => {
  const [acquire, response, revoke, transport, runtime] = await Promise.all([
    json("admission-acquire-v1.schema.json"),
    json("admission-response-v1.schema.json"),
    json("admission-revoke-v1.schema.json"),
    json("admission-transport-v1.json"),
    json("runtime-config-v2.schema.json"),
  ]);
  for (const schema of [acquire, response, revoke, runtime]) assert.equal(schema.additionalProperties, false);
  assert.equal(acquire.properties.protocolVersion.const, 1);
  assert.deepEqual(acquire.required, ["protocolVersion", "requestId", "instanceId", "target", "command", "rootFingerprint"]);
  assert.equal(response.properties.data.properties.protocolVersion.const, 1);
  assert.equal(response.properties.data.properties.grantExpiresInMs.maximum, 2000);
  assert.equal(response.properties.data.properties.retryAfterMs.const, 100);
  assert.equal(transport.rules.tokenInJsonBody, false);
  assert.equal(transport.rules.cliMayRevokeOnlyUnconsumedGrant, true);
  assert.equal(runtime.properties.version.const, 2);
  assert.equal(runtime.properties.taskPoolMaxActive.minimum, 2);
  assert.equal(runtime.properties.taskPoolReopenAt.minimum, 1);
});

test("direct-call contract defines two budgets, 5/3 hysteresis, and effect-before busy", async () => {
  const contract = await json("direct-call-semantics.json");
  assert.equal(contract.version, 2);
  assert.equal(contract.admission.capacity.defaultMaxActive, 5);
  assert.equal(contract.admission.capacity.defaultReopenAt, 3);
  assert.equal(contract.admission.timeoutError.code, "E_UNITY_BUSY");
  assert.equal(contract.admission.timeoutError.businessRequestSent, false);
  assert.match(contract.request.executionDeadline, /finalGrantObservedAt/);
  assert.equal(contract.host.minimumWaitMs, "2 * T + 5000");
  assert.equal(contract.request.admissionTokenInBody, false);
});

test("immutable Bridge request contract remains admission-free", async () => {
  const bridge = await json("bridge-request-v2.schema.json");
  const text = JSON.stringify(bridge);
  assert.doesNotMatch(text, /admission|token/i);
  assert.deepEqual(bridge.required, ["protocolVersion", "requestId", "instanceId", "command", "input", "execution"]);
});

test("public docs and installed Skill explain behavior without exposing transport controls", async () => {
  const paths = ["README.md", "docs/tool-execution.md", "docs/error-codes.md", "skills/unity_cli/SKILL.md", "skills/unity_cli/references/operation-protocol.md"];
  const texts = await Promise.all(paths.map((path) => readFile(path, "utf8")));
  assert.match(texts[0]!, /2 \* T \+ 5000/);
  assert.match(texts[1]!, /准入等待/);
  assert.match(texts[2]!, /no business request was sent/);
  assert.match(texts[3]!, /不得手工每100毫秒循环/);
  assert.match(texts[4]!, /effect未开始/);
  for (const text of texts) {
    assert.doesNotMatch(text, /\/bridge\/admission/);
    assert.doesNotMatch(text, /x-unity-cli-admission/);
  }
});
