import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import type { BridgeRequest } from "../src/bridge/index.js";
import {
  discoverProjectContexts,
  rawDescriptorToContextDescriptor
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
    examples: [{ description: `Read ${source} sample.`, input: {}, argv: ["context", "read", source === "extension" ? "probuilder.scene" : `${source}.sample`, "--input", "{}"] }]
  };
}

test("raw descriptor mapping preserves built-in, extension, and project source", () => {
  for (const source of ["built-in", "extension", "project"] as const) {
    const mapped = rawDescriptorToContextDescriptor(descriptor(source));
    assert.ok(mapped);
    assert.equal(mapped.source, source);
  }
});

test("raw descriptor mapping validates source shape without using source for ownership routing", () => {
  assert.equal(rawDescriptorToContextDescriptor({ ...descriptor("built-in"), source: "unknown" }), undefined);
  assert.equal(rawDescriptorToContextDescriptor({ ...descriptor("built-in"), inputSchema: undefined }), undefined);
  assert.equal(rawDescriptorToContextDescriptor({ ...descriptor("built-in"), outputSchema: undefined }), undefined);

  const informationalSource = rawDescriptorToContextDescriptor({ ...descriptor("extension"), source: "built-in" });
  assert.ok(informationalSource);
  assert.equal(informationalSource.source, "built-in");
  assert.equal(informationalSource.extensionId, "probuilder");
});

test("live Context availability reasons match read routing errors", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/ProjectContexts/ProjectContextRegistry.cs", import.meta.url), "utf8");
  assert.match(source, /E_RUNTIME_NOT_READY: Enter Play Mode before reading this Context/);
  assert.match(source, /E_CONTEXT_UNAVAILABLE: Exit Play Mode before reading this editor-only Context/);
  assert.doesNotMatch(source, /E_UNITY_PLAY_MODE: Exit Play Mode before reading this editor-only Context/);
});

test("project context discovery delegates to the strict plural catalog", async () => {
  const requests: BridgeRequest[] = [];
  const contexts = [descriptor("built-in"), descriptor("extension"), descriptor("project")];
  const result = await discoverProjectContexts({}, {
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
  const result = await discoverProjectContexts({}, {
    bridgeExecute: async (payload) => ({
      ok: true,
      requestId: payload.requestId,
      data: { contexts: [{ ...descriptor("project"), inputSchema: undefined }] }
    })
  }, "context_discovery_invalid");
  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_CONTEXT_REGISTRY_UNHEALTHY");
});
