import assert from "node:assert/strict";
import test from "node:test";
import type { BridgeRuntime } from "../src/bridge/index.js";
import { handleCapabilitiesCommand } from "../src/capabilities.js";
import type { ProjectContextRuntime } from "../src/project-context-discovery.js";
import { probuilderPingTool, projectAuditTool, sceneHierarchyTool } from "./helpers/live-tool-fixtures.js";
import { builtinToolMetadata } from "../src/tools/metadata/index.js";

const requestId = "test_req_caps_001";

test("offline capabilities return compact full static catalog counts", async () => {
  const result = await handleCapabilitiesCommand({}, {}, requestId);
  assert.ok(result.ok);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.mode, undefined);
  assert.equal(data.requiresUnity, undefined);
  assert.equal(data.runtime, undefined);

  const catalog = data.catalog as Record<string, unknown>;
  assert.equal(catalog.toolCount, 361);
  assert.equal(catalog.contextCount, 26);
  const groups = catalog.groups as Record<string, number>;
  assert.ok(groups.docs > 0);
  assert.ok(groups.scene > 0);
  assert.ok(groups.probuilder > 0);
  assert.equal(Object.hasOwn(catalog, "availableTools"), false);
  assert.equal(Object.hasOwn(catalog, "destructiveTools"), false);
  assert.equal(Object.hasOwn(catalog, "schemaFeatures"), false);
});

test("capabilities accept only a closed empty business input", async () => {
  await import("../src/tools/diagnose/capabilities.js");
  const descriptor = builtinToolMetadata().find((tool) => tool.name === "diagnose.capabilities");
  assert.ok(descriptor);
  assert.deepEqual(descriptor.inputSchema.properties, {});
  assert.equal(descriptor.inputSchema.additionalProperties, false);
});

test("valid live preflight adds only Bridge health counts", async () => {
  const runtime: BridgeRuntime = {
    bridgeExecute: async (payload) => ({
      ok: true as const,
      requestId: payload.requestId,
      data: {
        bridge: { ready: true, toolCount: 327, contextCount: 22, endpoint: "private", listenerId: "private" },
        customContexts: { fingerprint: { algorithm: "private" } },
        command: "private",
      },
    }),
  };
  const result = await handleCapabilitiesCommand({}, runtime, requestId);
  assert.ok(result.ok);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.mode, undefined);
  assert.equal(data.requiresUnity, undefined);
  assert.deepEqual(data.runtime, { bridge: { ready: true, toolCount: 327, contextCount: 22 } });
  assert.doesNotMatch(JSON.stringify(data), /endpoint|listenerId|fingerprint|command/);
});

test("malformed or failed live preflight stays offline", async () => {
  for (const bridgeExecute of [
    async (payload: { requestId: string }) => ({ ok: true as const, requestId: payload.requestId, data: { status: "accepted" } }),
    async (payload: { requestId: string }) => ({ ok: false as const, requestId: payload.requestId, error: { code: "BRIDGE_ERROR", message: "not available", nextActions: [], transient: false, retryAfterMs: 0 } }),
  ]) {
    const result = await handleCapabilitiesCommand({}, { bridgeExecute } as BridgeRuntime, requestId);
    assert.ok(result.ok);
    assert.equal((result.data as Record<string, unknown>).runtime, undefined);
    assert.equal((result.data as Record<string, unknown>).mode, undefined);
  }
});

test("live discovery adds project counts without copying Tool names", async () => {
  const runtime: BridgeRuntime & { projectTools: ReturnType<typeof sceneHierarchyTool>[] } = {
    projectTools: [sceneHierarchyTool(), probuilderPingTool(), projectAuditTool()],
  };
  const result = await handleCapabilitiesCommand({}, runtime, requestId);
  assert.ok(result.ok);
  const data = result.data as Record<string, unknown>;
  assert.equal((data.projectTools as { count: number }).count, 3);
  const catalog = data.catalog as Record<string, unknown>;
  assert.equal(catalog.toolCount, 362);
  assert.equal((catalog.sources as { projectTools: number }).projectTools, 1);
  assert.equal((catalog.groups as Record<string, number>).project, 5);
  assert.doesNotMatch(JSON.stringify(catalog), /project\.audit|scene\.hierarchy|probuilder\.ping/);
});

test("custom Context summary contains only count and categories", async () => {
  const runtime: BridgeRuntime & ProjectContextRuntime = {
    projectContexts: [{
      name: "sample.project-summary",
      summary: "Read a sample project summary.",
      description: "Read a sample project summary without changing project state.",
      category: "sample",
      source: "project",
      target: "editor",
      availabilityScope: "editorSession",
      playModeSupport: "supported",
      inputSchema: { type: "object", properties: {}, additionalProperties: false },
      outputSchema: { type: "object", properties: {}, additionalProperties: false },
      examples: [{ description: "Read the sample project summary.", argv: ["context", "read", "sample.project-summary", "--input", "{}"], input: {} }],
    }],
  };
  const result = await handleCapabilitiesCommand({ "include-custom": true }, runtime, requestId);
  assert.ok(result.ok);
  assert.deepEqual((result.data as Record<string, unknown>).customContexts, { available: true, count: 1, categories: ["sample"] });
});
