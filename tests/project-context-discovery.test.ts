import assert from "node:assert/strict";
import test from "node:test";
import type { BridgeRequest } from "../src/bridge/index.js";
import {
  discoverProjectContexts,
  rawDescriptorToContextDescriptor,
  shouldDiscoverContexts
} from "../src/project-context-discovery.js";
import type { ContextDescriptor } from "../src/contexts/types.js";

function descriptor(source: ContextDescriptor["source"]): ContextDescriptor {
  return {
    name: source === "extension" ? "probuilder.scene" : `${source}.sample`,
    summary: `Read ${source} sample.`,
    description: `Read a ${source} sample from the live registry.`,
    category: "sample",
    source,
    target: "editor",
    availabilityScope: "editorSession",
    playModeSupport: "supported",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    outputSchema: { type: "object", properties: { value: { type: "string" } }, additionalProperties: false },
    examples: [{ description: `Read ${source} sample.`, input: {}, argv: ["context", "read", source === "extension" ? "probuilder.scene" : `${source}.sample`, "--input", "{}"] }],
    cliCommand: `context read ${source === "extension" ? "probuilder.scene" : `${source}.sample`}`
  };
}

test("raw descriptor mapping preserves built-in, extension, and project source", () => {
  for (const source of ["built-in", "extension", "project"] as const) {
    const mapped = rawDescriptorToContextDescriptor(descriptor(source));
    assert.ok(mapped);
    assert.equal(mapped.source, source);
  }
});

test("raw descriptor mapping rejects unknown source and missing schemas", () => {
  assert.equal(rawDescriptorToContextDescriptor({ ...descriptor("built-in"), source: "unknown" }), undefined);
  assert.equal(rawDescriptorToContextDescriptor({ ...descriptor("built-in"), inputSchema: undefined }), undefined);
  assert.equal(rawDescriptorToContextDescriptor({ ...descriptor("built-in"), outputSchema: undefined }), undefined);
});

test("context discovery ignores removed endpoint and business target selectors", () => {
  assert.equal(shouldDiscoverContexts({}, {}), false);
  assert.equal(shouldDiscoverContexts({ "include-custom": true }, {}), true);
  assert.equal(shouldDiscoverContexts({ "bridge-endpoint": "http://127.0.0.1:1234" }, {}), false);
  assert.equal(shouldDiscoverContexts({ target: "Main Camera" }, {}), false);
  assert.equal(shouldDiscoverContexts({ instance: "C:/Project" }, {}), true);
  assert.equal(shouldDiscoverContexts({}, { projectContexts: [] }), true);
});

test("project context discovery delegates to the strict plural catalog", async () => {
  const requests: BridgeRequest[] = [];
  const contexts = [descriptor("built-in"), descriptor("extension"), descriptor("project")];
  const result = await discoverProjectContexts({ "include-custom": true }, {
    bridgeExecute: async (payload) => {
      requests.push(payload);
      return { ok: true, requestId: payload.requestId, data: { contexts } };
    }
  }, "context_discovery");

  assert.ok(result.ok);
  if (!result.ok) return;
  assert.deepEqual(requests.map((request) => request.command), ["context.list"]);
  assert.equal(result.builtInCount, 1);
  assert.equal(result.projectCustomCount, 1);
  assert.ok(result.contexts.some((context) => context.source === "extension"));
});

test("project context discovery returns structured registry errors", async () => {
  const result = await discoverProjectContexts({ "include-custom": true }, {
    bridgeExecute: async (payload) => ({
      ok: true,
      requestId: payload.requestId,
      data: { contexts: [{ ...descriptor("project"), inputSchema: undefined }] }
    })
  }, "context_discovery_invalid");
  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_CONTEXT_REGISTRY_UNHEALTHY");
});
