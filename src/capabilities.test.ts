/**
 * Unit tests for capabilities CLI command.
 */

import assert from "node:assert/strict";
import test from "node:test";
import { handleCapabilitiesCommand } from "./capabilities.js";
import type { BridgeRuntime } from "./bridge/index.js";

const requestId = "test_req_caps_001";

test("capabilities without bridge returns static-only result", async () => {
  const runtime: BridgeRuntime = {};
  const result = await handleCapabilitiesCommand({}, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.mode, "offline");
  assert.equal(data.requiresUnity, false);

  const staticData = data.static as Record<string, unknown>;
  assert.ok(typeof staticData.toolCount === "number");
  assert.ok(staticData.toolCount > 0);
  assert.ok(typeof staticData.groups === "object");

  const groups = staticData.groups as Record<string, number>;
  assert.ok(groups.scene > 0, "scene group should have tools");

  assert.ok(Array.isArray(staticData.bridgeTools));
  assert.ok(Array.isArray(staticData.offlineTools));
  assert.ok(Array.isArray(staticData.readOnlyTools));
  assert.ok(Array.isArray(staticData.destructiveTools));

  // No runtime section since bridge not reachable
  assert.equal(data.runtime, undefined);
});

test("capabilities --json flag works", async () => {
  const runtime: BridgeRuntime = {};
  const result = await handleCapabilitiesCommand({ json: true }, runtime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.mode, "offline");
});

test("capabilities with bridge returns hybrid result", async () => {
  const runtime: BridgeRuntime = {
    bridgeExecute: async () => ({ ok: true, data: { status: "ok", bridgeVersion: "1.0" } })
  };
  const result = await handleCapabilitiesCommand({}, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.mode, "hybrid");
  assert.ok(data.runtime !== undefined);
});

test("capabilities with bridge error falls back to offline", async () => {
  const runtime: BridgeRuntime = {
    bridgeExecute: async () => ({ ok: false, error: { code: "BRIDGE_ERROR", message: "not available" } })
  };
  const result = await handleCapabilitiesCommand({}, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  // Should still succeed with static-only data
  assert.ok(data.static !== undefined);
});

test("capabilities static data includes destructive tools list", async () => {
  const runtime: BridgeRuntime = {};
  const result = await handleCapabilitiesCommand({}, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  const staticData = data.static as Record<string, unknown>;
  const destructiveTools = staticData.destructiveTools as string[];

  assert.ok(destructiveTools.length > 0);
  assert.ok(destructiveTools.includes("scene.save"), "scene.save should be in destructive list");
  assert.ok(destructiveTools.includes("go.delete"), "go.delete should be in destructive list");
});

test("capabilities static data includes schemaFeatures", async () => {
  const runtime: BridgeRuntime = {};
  const result = await handleCapabilitiesCommand({}, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  const staticData = data.static as Record<string, unknown>;
  const schemaFeatures = staticData.schemaFeatures as Record<string, boolean>;

  assert.equal(schemaFeatures.toolMetadata, true);
  assert.equal(schemaFeatures.requestEnvelope, true);
  assert.equal(schemaFeatures.responseEnvelope, true);
  assert.equal(schemaFeatures.errorCode, true);
});