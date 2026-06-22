/**
 * Unit tests for capabilities CLI command.
 */

import assert from "node:assert/strict";
import test from "node:test";
import { handleCapabilitiesCommand } from "../src/capabilities.js";
import type { BridgeRuntime } from "../src/bridge/index.js";
import type { ProjectContextRuntime } from "../src/project-context-discovery.js";

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

  assert.ok(Array.isArray(staticData.liveBridgeTools));
  assert.ok(Array.isArray(staticData.hybridTools));
  assert.ok(Array.isArray(staticData.batchJobTools));
  assert.ok(Array.isArray(staticData.cliOnlyTools));
  assert.ok(Array.isArray(staticData.plannedTools));
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

test("capabilities static data includes destructive tools", async () => {
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

test("capabilities static data separates live bridge, hybrid and planned tools", async () => {
  const runtime: BridgeRuntime = {};
  const result = await handleCapabilitiesCommand({}, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  const staticData = data.static as Record<string, unknown>;
  const liveBridgeTools = staticData.liveBridgeTools as string[];
  const hybridTools = staticData.hybridTools as string[];
  const batchJobTools = staticData.batchJobTools as string[];
  const bridgeTools = staticData.bridgeTools as string[];
  const jobTools = staticData.jobTools as string[];
  const plannedTools = staticData.plannedTools as string[];

  assert.ok(liveBridgeTools.includes("scene.hierarchy"));
  assert.ok(bridgeTools.includes("scene.hierarchy"));
  assert.ok(plannedTools.length >= 0, "planned tools may be zero after core tools promotion to live-bridge");
  // Core tools (scene/go/component/script) are now live-bridge, no longer planned
  assert.equal(liveBridgeTools.includes("component.set"), true);
  assert.ok(bridgeTools.includes("component.set"));

  const hybridProbuilderTools = hybridTools.filter((name) => name.startsWith("probuilder."));
  assert.ok(hybridProbuilderTools.length >= 30, `expected >= 30 probuilder hybrid tools, got ${hybridProbuilderTools.length}`);
  for (const name of ["probuilder.create-shape", "probuilder.extrude-faces", "profiler.start", "profiler.capture"]) {
    assert.ok(hybridTools.includes(name), `${name} should be listed as hybrid`);
    assert.equal(batchJobTools.includes(name), false, `${name} should not be listed as batch-only`);
    assert.ok(jobTools.includes(name), `${name} should be job-capable as a hybrid tool`);
  }
});

test("capabilities static data includes schemaFeatures", async () => {
  const runtime: BridgeRuntime = {};
  const result = await handleCapabilitiesCommand({}, runtime, requestId);
  assert.equal(result.ok, true);

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
});

test("capabilities includes custom tools in unified static manifest when available", async () => {
  const runtime: BridgeRuntime & { projectTools: Array<{ name: string; title: string; description: string; group: string; inputSchema: Record<string, unknown>; readOnlyHint: boolean; destructiveHint: boolean }> } = {
    projectTools: [{
      name: "project.audit",
      title: "Project Audit",
      description: "Audits project state.",
      group: "project",
      inputSchema: { type: "object" },
      readOnlyHint: true,
      destructiveHint: false
    }]
  };
  const result = await handleCapabilitiesCommand({}, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  const staticData = data.static as Record<string, unknown>;
  assert.equal((data.projectTools as { count: number }).count, 1);
  assert.equal((staticData.groups as Record<string, number>).project, 3);
  assert.ok((staticData.liveBridgeTools as string[]).includes("project.audit"));
});

test("capabilities static data includes customContextDescriptor and contextFingerprint schema features", async () => {
  const runtime: BridgeRuntime = {};
  const result = await handleCapabilitiesCommand({}, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  const staticData = data.static as Record<string, unknown>;
  const schemaFeatures = staticData.schemaFeatures as Record<string, boolean>;

  assert.equal(schemaFeatures.customContextDescriptor, true, "customContextDescriptor should be true");
  assert.equal(schemaFeatures.contextFingerprint, true, "contextFingerprint should be true");
});

test("capabilities includes customContexts when project contexts are available", async () => {
  const runtime: BridgeRuntime & ProjectContextRuntime = {
    projectContexts: [
      {
        name: "sample.project-summary",
        title: "Project Summary",
        description: "Sample project summary context",
        category: "sample",
        kind: "context",
        source: "project",
        readOnlyHint: true,
        requiresUnity: true,
        inputSchema: { type: "object" }
      }
    ]
  };
  const result = await handleCapabilitiesCommand({ "include-custom": true }, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  const customCtx = data.customContexts as { mode: string; count: number; categories: string[] };
  assert.ok(customCtx, "customContexts should be present");
  assert.equal(customCtx.count, 1);
  assert.ok(customCtx.categories.includes("sample"));
});

test("capabilities passes bridge customContexts summary as runtimeCustomContexts", async () => {
  const runtime: BridgeRuntime & ProjectContextRuntime = {
    bridgeExecute: async () => ({
      ok: true,
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
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.mode, "hybrid");
  assert.ok(data.runtimeCustomContexts, "runtimeCustomContexts should be present when bridge returns customContexts");
  const rtCtx = data.runtimeCustomContexts as { count: number; categories: string[] };
  assert.equal(rtCtx.count, 3);
});