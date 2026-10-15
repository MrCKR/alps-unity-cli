import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import type { BridgeRequest } from "../src/bridge/types.js";
import { execute } from "../src/cli.js";
import { validateSchema } from "../src/schema/validator.js";
import { unverifiableNextActions } from "../src/instances.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";
const contract = JSON.parse(await readFile("contracts/instances-contract.json", "utf8")) as any;
const schema = JSON.parse(await readFile("schemas/instances.schema.json", "utf8")) as any;
function assertValid(value: unknown): void { assert.deepEqual(validateSchema(schema, value), [], JSON.stringify(value)); }

test("instances contract freezes status, options, writer and retention", () => {
  assert.equal(contract.version, 1);
  assert.equal(contract.topLevel, "instances");
  assert.equal(contract.schema, "schemas/instances.schema.json");
  assert.deepEqual(contract.registry.statuses, ["ready", "transitioning", "blocked", "unresponsive", "unverifiable", "offline", "conflict"]);
  assert.deepEqual(contract.subcommands.list.options, ["all"]);
  assert.equal(contract.subcommands.list.readOnly, true);
  assert.deepEqual(contract.subcommands.prune.options, ["instance", "timeout-ms"]);
  assert.equal(contract.subcommands.prune.privateCommand, "internal.instances.prune");
  assert.deepEqual(contract.subcommands.prune.retention, { offlineMax: 100, damagedMax: 20, maxAgeDays: 30, scanLimit: 2000, deleteLimit: 20 });
  assert.equal(schema.oneOf.length, 3);
});

test("actual instances list and prune envelopes pass the closed schema", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-instances-contract-"));
  try {
    const list = await execute(["instances", "list", "--all"], { dataDir } as never);
    assert.equal(list.ok, true, JSON.stringify(list));
    assertValid(list);

    const prune = await execute(["instances", "prune"], { dataDir, bridgeExecute: async (payload: BridgeRequest) => ({
      ok: true as const,
      requestId: payload.requestId,
      data: { removed: 2, remaining: 3, deferred: 1, scanned: 5, truncated: false, retention: { offlineMax: 100, damagedMax: 20, maxAgeDays: 30, scanLimit: 2000, deleteLimit: 20 } },
    }) } as never);
    assert.equal(prune.ok, true, JSON.stringify(prune));
    assertValid(prune);

    const extra = JSON.parse(JSON.stringify(prune));
    extra.data.internalPath = "forbidden";
    assert.ok(validateSchema(schema, extra).length > 0);
    const wrongRetention = JSON.parse(JSON.stringify(prune));
    wrongRetention.data.retention.offlineMax = 101;
    assert.ok(validateSchema(schema, wrongRetention).length > 0);
  } finally {
    await rm(dataDir, { recursive: true, force: true });
  }
});

test("unverifiable recovery separates host permission denial from other observation failures", () => {
  const denied = unverifiableNextActions("access-denied").join("\n");
  assert.match(denied, /host denied OS process inspection/);
  assert.match(denied, /Do not retry a prior mutation whose outcome is unknown/);
  assert.doesNotMatch(denied, /observe the current identity again/);

  for (const reason of ["timeout", "unavailable", "mismatch", undefined] as const) {
    const actions = unverifiableNextActions(reason).join("\n");
    assert.match(actions, /observe the current identity again/, String(reason));
    assert.doesNotMatch(actions, /host denied OS process inspection/, String(reason));
    assert.match(actions, /Do not retry a prior mutation whose outcome is unknown/, String(reason));
  }
});

test("actual instances prune busy and input errors pass the error schema", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-instances-errors-"));
  try {
    const busy = await execute(["instances", "prune"], { dataDir, bridgeExecute: async (payload: BridgeRequest) => ({
      ok: false as const,
      requestId: payload.requestId,
      error: { code: "E_UNITY_BUSY", message: "cleanup busy", nextActions: ["Retry later."], transient: true, retryAfterMs: 100 },
    }) } as never);
    const invalid = await execute(["instances", "prune", "--timeout-ms", "0"], { dataDir } as never);
    for (const result of [busy, invalid]) { assert.equal(result.ok, false, JSON.stringify(result)); assertValid(result); }
    const leaked = JSON.parse(JSON.stringify(busy));
    leaked.error.registryPath = "forbidden";
    assert.ok(validateSchema(schema, leaked).length > 0);
  } finally {
    await rm(dataDir, { recursive: true, force: true });
  }
});
