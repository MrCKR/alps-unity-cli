import assert from "node:assert/strict";
import test from "node:test";
import { handleCapabilitiesCommand } from "../src/capabilities.js";
import type { BridgeRuntime } from "../src/bridge/index.js";
import type { ProjectContextRuntime } from "../src/project-context-discovery.js";
import { projectAuditTool, sceneHierarchyTool, probuilderPingTool } from "./helpers/live-tool-fixtures.js";

const requestId = "test_req_caps_001";

test("capabilities without bridge returns TS-local static capabilities only", async () => {
  const runtime: BridgeRuntime = {};
  const result = await handleCapabilitiesCommand({}, runtime, requestId);
  assert.ok(result.ok);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.mode, "offline");
  assert.equal(data.requiresUnity, false);

  const staticData = data.static as Record<string, unknown>;
  const groups = staticData.groups as Record<string, number>;
  assert.equal(staticData.toolCount, 37);
  assert.ok(groups.docs > 0, "docs group should stay offline-visible");
  assert.ok(groups.tools > 0, "tools group should stay offline-visible");
  assert.equal(groups.scene, undefined, "scene group should require live discovery");

  const destructiveTools = staticData.destructiveTools as string[];
  assert.ok(destructiveTools.includes("code.exec"));
  assert.ok(destructiveTools.includes("package.install"));
  assert.ok(!destructiveTools.includes("scene.save"));

  const hybridTools = staticData.hybridTools as string[];
  const batchJobTools = staticData.batchJobTools as string[];
  const cliOnlyTools = staticData.cliOnlyTools as string[];
  assert.ok(hybridTools.includes("code.exec"));
  assert.ok(batchJobTools.includes("build.run"));
  assert.ok(cliOnlyTools.includes("tools.group"));
  assert.equal(data.runtime, undefined);
});

test("capabilities with bridge returns hybrid result", async () => {
  const runtime: BridgeRuntime = {
    bridgeExecute: async (payload) => ({ ok: true as const, requestId: payload.requestId, data: { status: "ok", bridgeVersion: "1.0" } })
  };
  const result = await handleCapabilitiesCommand({}, runtime, requestId);
  assert.ok(result.ok);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.mode, "hybrid");
  assert.ok(data.runtime !== undefined);
});

test("capabilities with bridge error falls back to offline", async () => {
  const runtime: BridgeRuntime = {
    bridgeExecute: async (payload) => ({ ok: false as const, requestId: payload.requestId, error: { code: "BRIDGE_ERROR", message: "not available", nextActions: [], transient: false, retryAfterMs: 0 } })
  };
  const result = await handleCapabilitiesCommand({}, runtime, requestId);
  assert.ok(result.ok);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.mode, "offline");
  assert.ok(data.static !== undefined);
});

test("capabilities includes live-discovered built-in, extension and project tools when runtime provides them", async () => {
  const runtime: BridgeRuntime & { projectTools: ReturnType<typeof sceneHierarchyTool>[] } = {
    projectTools: [
      sceneHierarchyTool(),
      probuilderPingTool(),
      projectAuditTool()
    ]
  };

  const result = await handleCapabilitiesCommand({}, runtime, requestId);
  assert.ok(result.ok);

  const data = result.data as Record<string, unknown>;
  const staticData = data.static as Record<string, unknown>;
  const groups = staticData.groups as Record<string, number>;
  const liveBridgeTools = staticData.liveBridgeTools as string[];

  assert.equal((data.projectTools as { count: number }).count, 3);
  assert.equal(groups.scene, 1);
  assert.equal(groups.probuilder, 1);
  assert.equal(groups.project, 1);
  assert.ok(liveBridgeTools.includes("scene.hierarchy"));
  assert.ok(liveBridgeTools.includes("probuilder.ping"));
  assert.ok(liveBridgeTools.includes("project.audit"));
});

test("capabilities static data includes schemaFeatures", async () => {
  const result = await handleCapabilitiesCommand({}, {}, requestId);
  assert.ok(result.ok);

  const data = result.data as Record<string, unknown>;
  const staticData = data.static as Record<string, unknown>;
  const schemaFeatures = staticData.schemaFeatures as Record<string, boolean>;

  assert.equal(schemaFeatures.toolMetadata, true);
  assert.equal(schemaFeatures.projectToolDescriptor, true);
  assert.equal(schemaFeatures.unifiedDiscovery, true);
  assert.equal(schemaFeatures.builtinDocs, true);
  assert.equal(schemaFeatures.requestEnvelope, true);
  assert.equal(schemaFeatures.responseEnvelope, true);
  assert.equal(schemaFeatures.errorCode, true);
  assert.equal(schemaFeatures.customContextDescriptor, true);
  assert.equal(schemaFeatures.contextFingerprint, true);
});

test("capabilities includes customContexts when project contexts are available", async () => {
  const runtime: BridgeRuntime & ProjectContextRuntime = {
    projectContexts: [
      {
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
        cliCommand: "context read sample.project-summary"
      }
    ]
  };
  const result = await handleCapabilitiesCommand({ "include-custom": true }, runtime, requestId);
  assert.ok(result.ok);

  const data = result.data as Record<string, unknown>;
  const customCtx = data.customContexts as { mode: string; count: number; categories: string[] };
  assert.ok(customCtx);
  assert.equal(customCtx.count, 1);
  assert.ok(customCtx.categories.includes("sample"));
});

test("capabilities passes bridge customContexts summary as runtimeCustomContexts", async () => {
  const runtime: BridgeRuntime & ProjectContextRuntime = {
    bridgeExecute: async (payload) => ({
      ok: true,
      requestId: payload.requestId,
      data: {
        status: "ok",
        bridgeVersion: "1.0",
        customContexts: {
          mode: "live",
          count: 3,
          categories: ["project", "editor", "sample"],
          fingerprint: { algorithm: "sha256", mode: "pass", contextCount: 3, blockedCount: 0, warnCount: 0 }
        }
      }
    })
  };
  const result = await handleCapabilitiesCommand({}, runtime, requestId);
  assert.ok(result.ok);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.mode, "hybrid");
  const rtCtx = data.runtimeCustomContexts as { count: number; categories: string[] };
  assert.equal(rtCtx.count, 3);
  assert.ok(rtCtx.categories.includes("sample"));
});
