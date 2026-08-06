import assert from "node:assert/strict";
import test from "node:test";
import type { BridgeRuntime } from "../src/bridge/index.js";
import { handleCapabilitiesCommand } from "../src/capabilities.js";
import type { ContextDescriptor } from "../src/contexts/types.js";
import { probuilderPingTool, projectAuditTool, sceneHierarchyTool } from "./helpers/live-tool-fixtures.js";
import { builtinToolMetadata } from "../src/tools/metadata/index.js";

const requestId = "test_req_caps_001";

/** 注入实例目录，避免单元测试依赖本机 Unity registry。 */
function liveRuntime(overrides: Record<string, unknown> = {}): BridgeRuntime & Record<string, unknown> {
  return { projectTools: [], runtimeTools: [], projectContexts: [], ...overrides } as BridgeRuntime & Record<string, unknown>;
}

test("capabilities requires a connected instance catalog", async () => {
  const result = await handleCapabilitiesCommand({}, {}, requestId);
  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_INSTANCE_REQUIRED");
});

test("capabilities accept only a closed empty business input", async () => {
  await import("../src/tools/diagnose/capabilities.js");
  const descriptor = builtinToolMetadata().find((tool) => tool.name === "diagnose.capabilities");
  assert.ok(descriptor);
  assert.deepEqual(descriptor.inputSchema.properties, {});
  assert.equal(descriptor.inputSchema.additionalProperties, false);
});

test("valid live preflight adds only Bridge health counts", async () => {
  const runtime = liveRuntime({
    bridgeExecute: async (payload: { requestId: string; command: string }) => ({
      ok: true as const,
      requestId: payload.requestId,
      data: payload.command === "diagnose.preflight"
        ? { bridge: { ready: true, toolCount: 327, contextCount: 22, endpoint: "private", listenerId: "private" }, command: "private" }
        : {},
    }),
  });
  const result = await handleCapabilitiesCommand({}, runtime as never, requestId);
  assert.ok(result.ok);
  const data = result.data as Record<string, unknown>;
  assert.deepEqual(data.runtime, { bridge: { ready: true, toolCount: 327, contextCount: 22 } });
  assert.doesNotMatch(JSON.stringify(data), /endpoint|listenerId|fingerprint|command/);
});

test("malformed or failed live preflight does not invalidate a valid instance catalog", async () => {
  for (const bridgeExecute of [
    async (payload: { requestId: string }) => ({ ok: true as const, requestId: payload.requestId, data: { status: "accepted" } }),
    async (payload: { requestId: string }) => ({ ok: false as const, requestId: payload.requestId, error: { code: "BRIDGE_ERROR", message: "not available", nextActions: [], transient: false, retryAfterMs: 0 } }),
  ]) {
    const result = await handleCapabilitiesCommand({}, liveRuntime({ bridgeExecute }) as never, requestId);
    assert.ok(result.ok);
    assert.equal((result.data as Record<string, unknown>).runtime, undefined);
  }
});

test("unified catalog summary includes project Tools without copying names", async () => {
  const projectTools = [sceneHierarchyTool(), probuilderPingTool(), projectAuditTool()];
  const result = await handleCapabilitiesCommand({}, liveRuntime({ projectTools }) as never, requestId);
  assert.ok(result.ok);
  const catalog = (result.data as Record<string, unknown>).catalog as Record<string, unknown>;
  assert.equal(catalog.toolCount, builtinToolMetadata().length + projectTools.length);
  assert.equal(((catalog.sources as { tools: Record<string, number> }).tools).project, 1);
  assert.doesNotMatch(JSON.stringify(catalog), /project\.audit|scene\.hierarchy|probuilder\.ping/);
});

test("unified catalog summary includes project Context categories", async () => {
  const projectContext: ContextDescriptor = {
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
    examples: [{ description: "Read the sample project summary.", argv: ["context", "read", "sample.project-summary", "--input", "{}"] }],
  };
  const result = await handleCapabilitiesCommand({}, liveRuntime({ projectContexts: [projectContext] }) as never, requestId);
  assert.ok(result.ok);
  const catalog = (result.data as Record<string, unknown>).catalog as Record<string, unknown>;
  assert.equal(catalog.contextCount, 3);
  assert.deepEqual(catalog.contextCategories, { editor: 2, sample: 1 });
  assert.equal(((catalog.sources as { contexts: Record<string, number> }).contexts).project, 1);
});
