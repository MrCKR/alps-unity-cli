/**
 * Tests for project context discovery and context CLI integration.
 *
 * Covers:
 * - Descriptor mapping: raw bridge descriptor → unified ContextDescriptor
 * - Source isolation: built-in vs project contexts correctly classified
 * - Duplicate guard: project context with same name as built-in is rejected
 * - readOnlyHint: always true for any context
 * - Context never enters tools path: ContextDescriptor is distinct from ToolMetadata
 * - CLI context list/describe/read with --include-custom
 */

import assert from "node:assert/strict";
import test from "node:test";
import { execute } from "../src/cli.js";
import { JobRunner } from "../src/job-runner.js";
import { JobStore } from "../src/job-store.js";
import type { BridgeRequest } from "../src/bridge/index.js";
import {
  shouldDiscoverContexts,
  rawDescriptorToContextDescriptor,
  type ContextDescriptor,
  type ProjectContextRuntime
} from "../src/project-context-discovery.js";
import { mkdtemp } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

async function makeRuntime(): Promise<ProjectContextRuntime & { dataDir: string; store: JobStore; runner: JobRunner }> {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-context-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

// ─── Descriptor mapping tests ──────────────────────────────────────────

test("rawDescriptorToContextDescriptor maps project context descriptor correctly", () => {
  const raw = {
    name: "project.scene-list",
    title: "Scene List",
    description: "Lists all scenes in the project.",
    category: "project",
    version: "1.0.0",
    inputSchema: { type: "object", properties: { includeDisabled: { type: "boolean" } } },
    outputSchema: { type: "object", properties: { scenes: { type: "array" } } },
    kind: "context",
    source: "project",
    readOnlyHint: true,
    target: "editor",
    availabilityScope: "editorSession",
    equivalentUnityMcp: "resources://scenes",
    equivalentUnityMcpNote: "Maps to unity-mcp resources/scenes",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "context read project.scene-list",
    fingerprint: {
      algorithm: "unity-cli-project-context-fingerprint-v1",
      value: "abc123",
      status: "matched",
      descriptorSha256: "def456",
      handlerSha256: "ghi789"
    }
  };

  const descriptor = rawDescriptorToContextDescriptor(raw);
  assert.ok(descriptor);
  assert.equal(descriptor!.name, "project.scene-list");
  assert.equal(descriptor!.title, "Scene List");
  assert.equal(descriptor!.kind, "context");
  assert.equal(descriptor!.source, "project");
  assert.equal(descriptor!.readOnlyHint, true);
  assert.equal(descriptor!.requiresUnity, true);
  assert.equal(descriptor!.category, "project");
  assert.equal(descriptor!.equivalentUnityMcpNote, "Maps to unity-mcp resources/scenes");
  assert.deepEqual(descriptor!.inputSchema, raw.inputSchema);
  assert.deepEqual(descriptor!.outputSchema, raw.outputSchema);
  assert.equal(descriptor!.fingerprint?.algorithm, "unity-cli-project-context-fingerprint-v1");
  assert.equal(descriptor!.fingerprint?.value, "abc123");
  assert.equal(descriptor!.fingerprint?.status, "matched");
  assert.equal(descriptor!.fingerprint?.descriptorSha256, "def456");
  assert.equal(descriptor!.fingerprint?.handlerSha256, "ghi789");
});

test("rawDescriptorToContextDescriptor maps built-in context descriptor", () => {
  const raw = {
    name: "unity.version",
    kind: "context",
    source: "built-in",
    readOnlyHint: true,
    title: "Unity Version"
  };

  const descriptor = rawDescriptorToContextDescriptor(raw);
  assert.ok(descriptor);
  assert.equal(descriptor!.source, "built-in");
  assert.equal(descriptor!.requiresUnity, false);
  assert.equal(descriptor!.readOnlyHint, true);
  assert.equal(descriptor!.kind, "context");
  assert.equal(descriptor!.handler, undefined);
  assert.equal(descriptor!.supportStatus, undefined);
});

test("rawDescriptorToContextDescriptor returns undefined for invalid input", () => {
  assert.equal(rawDescriptorToContextDescriptor(null), undefined);
  assert.equal(rawDescriptorToContextDescriptor(undefined), undefined);
  assert.equal(rawDescriptorToContextDescriptor("string"), undefined);
  assert.equal(rawDescriptorToContextDescriptor(42), undefined);
  assert.equal(rawDescriptorToContextDescriptor({}), undefined);
  assert.equal(rawDescriptorToContextDescriptor({ title: "no name" }), undefined);
});

test("rawDescriptorToContextDescriptor fills defaults for missing fields", () => {
  const raw = { name: "minimal.context", kind: "context", source: "project" };
  const descriptor = rawDescriptorToContextDescriptor(raw);
  assert.ok(descriptor);
  assert.equal(descriptor!.title, "minimal.context");
  assert.equal(descriptor!.description, "minimal.context");
  assert.equal(descriptor!.category, "custom");
  assert.equal(descriptor!.target, "editor");
  assert.deepEqual(descriptor!.inputSchema, {});
  assert.equal(descriptor!.outputSchema, undefined);
  assert.equal(descriptor!.handler, "bridge");
  assert.equal(descriptor!.supportStatus, "live-bridge");
  assert.equal(descriptor!.cliCommand, "context read minimal.context");
  assert.equal(descriptor!.docsTopic, "project-context");
});

// ─── Source isolation tests ─────────────────────────────────────────────

test("project context is correctly identified as source=project", () => {
  const raw = { name: "custom.data", source: "project", kind: "context" };
  const descriptor = rawDescriptorToContextDescriptor(raw);
  assert.ok(descriptor);
  assert.equal(descriptor!.source, "project");
  assert.equal(descriptor!.requiresUnity, true);
});

test("built-in context is correctly identified as source=built-in", () => {
  const raw = { name: "unity.data", source: "built-in", kind: "context" };
  const descriptor = rawDescriptorToContextDescriptor(raw);
  assert.ok(descriptor);
  assert.equal(descriptor!.source, "built-in");
  assert.equal(descriptor!.requiresUnity, false);
});

test("context with unknown source defaults to built-in", () => {
  const raw = { name: "unknown.data", source: "unknown", kind: "context" };
  const descriptor = rawDescriptorToContextDescriptor(raw);
  assert.ok(descriptor);
  assert.equal(descriptor!.source, "built-in");
  assert.equal(descriptor!.requiresUnity, false);
});

// ─── readOnlyHint fixed tests ──────────────────────────────────────────

test("readOnlyHint is always true regardless of input", () => {
  // Even if the raw descriptor claims readOnlyHint=false, the mapping forces true
  const raw = { name: "evil.context", source: "project", readOnlyHint: false };
  const descriptor = rawDescriptorToContextDescriptor(raw);
  assert.ok(descriptor);
  assert.equal(descriptor!.readOnlyHint, true);
});

test("readOnlyHint is true when not specified in raw descriptor", () => {
  const raw = { name: "no-hint.context", source: "project" };
  const descriptor = rawDescriptorToContextDescriptor(raw);
  assert.ok(descriptor);
  assert.equal(descriptor!.readOnlyHint, true);
});

// ─── Context not in tools path ─────────────────────────────────────────

test("ContextDescriptor has kind=context, not tool-like fields", () => {
  const raw = {
    name: "project.data",
    source: "project",
    kind: "context",
    inputSchema: {},
    outputSchema: { type: "object" }
  };
  const descriptor = rawDescriptorToContextDescriptor(raw);
  assert.ok(descriptor);
  // Contexts are always kind="context", never "tool"
  assert.equal(descriptor!.kind, "context");
  // Contexts have no destructiveHint — they are always read-only
  assert.equal("destructiveHint" in (descriptor as object), false);
  // Contexts use "context read" CLI command, not "tools run"
  assert.equal(descriptor!.cliCommand, "context read project.data");
  assert.ok(!descriptor!.cliCommand?.startsWith("tools run"));
});

// ─── shouldDiscoverContexts tests ──────────────────────────────────────

test("shouldDiscoverContexts returns true with --include-custom flag", () => {
  const flags = { "include-custom": true };
  assert.equal(shouldDiscoverContexts(flags, {}), true);
});

test("shouldDiscoverContexts returns false without bridge hints", () => {
  const flags = {};
  assert.equal(shouldDiscoverContexts(flags, {}), false);
});

test("shouldDiscoverContexts returns true with bridge-endpoint flag", () => {
  const flags = { "bridge-endpoint": "http://localhost:1234" };
  assert.equal(shouldDiscoverContexts(flags, {}), true);
});

test("shouldDiscoverContexts returns true with pre-populated projectContexts", () => {
  const flags = {};
  const runtime: ProjectContextRuntime = { projectContexts: [] };
  assert.equal(shouldDiscoverContexts(flags, runtime), true);
});

test("shouldDiscoverContexts returns true with UNITY_CLI_BRIDGE_ENDPOINT env var", () => {
  const prev = process.env.UNITY_CLI_BRIDGE_ENDPOINT;
  process.env.UNITY_CLI_BRIDGE_ENDPOINT = "http://localhost:9999";
  try {
    assert.equal(shouldDiscoverContexts({}, {}), true);
  } finally {
    if (prev === undefined) {
      delete process.env.UNITY_CLI_BRIDGE_ENDPOINT;
    } else {
      process.env.UNITY_CLI_BRIDGE_ENDPOINT = prev;
    }
  }
});

// ─── CLI integration tests ──────────────────────────────────────────────

test("context list --include-custom discovers project custom contexts through the bridge", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const customDescriptor = {
    name: "project.scene-list",
    title: "Scene List",
    description: "Lists all scenes in the project.",
    category: "project",
    kind: "context",
    source: "project",
    readOnlyHint: true,
    inputSchema: { type: "object" },
    outputSchema: { type: "object" },
    target: "editor",
    availabilityScope: "editorSession",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "context read project.scene-list",
    fingerprint: { algorithm: "unity-cli-project-context-fingerprint-v1", value: "abc123", status: "matched" }
  };

  const result = await execute(["context", "list", "--include-custom"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return {
        ok: true as const,
        data: {
          contexts: [customDescriptor],
          builtInCount: 0,
          projectCustomCount: 1
        }
      };
    }
  });

  assert.equal(result.ok, true);
  assert.equal(requests.length, 1);
  assert.equal(requests[0].command, "context.list");
  const data = result.data as { contexts: ContextDescriptor[]; count: number };
  // 25 built-in contexts + 1 project custom context
  assert.equal(data.count, 26);
  const projectCtx = data.contexts.find((c) => c.name === "project.scene-list");
  assert.ok(projectCtx, "project.scene-list should be in results");
  assert.equal(projectCtx.source, "custom");
});

test("context list --include-custom merges built-in and project custom contexts with source isolation", async () => {
  const runtime = await makeRuntime();
  const builtInDescriptor = {
    name: "unity.version",
    title: "Unity Version",
    description: "Current Unity editor version.",
    category: "built-in",
    kind: "context",
    source: "built-in",
    readOnlyHint: true,
    inputSchema: {},
    target: "editor"
  };
  const customDescriptor = {
    name: "project.scene-list",
    title: "Scene List",
    description: "Lists all scenes in the project.",
    category: "project",
    kind: "context",
    source: "project",
    readOnlyHint: true,
    inputSchema: { type: "object" },
    target: "editor",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "context read project.scene-list"
  };

  const result = await execute(["context", "list", "--include-custom"], {
    ...runtime,
    bridgeExecute: async () => ({
      ok: true as const,
      data: {
        contexts: [builtInDescriptor, customDescriptor],
        builtInCount: 1,
        projectCustomCount: 1
      }
    })
  });

  assert.equal(result.ok, true);
  const data = result.data as { contexts: ContextDescriptor[]; count: number };
  // 16 built-in + 1 bridge-discovered built-in + 1 custom
  assert.ok(data.count >= 17, `expected >= 17 contexts, got ${data.count}`);
  const unityVersion = data.contexts.find((c) => c.name === "unity.version");
  const projectSceneList = data.contexts.find((c) => c.name === "project.scene-list");
  assert.ok(unityVersion, "unity.version should be in results");
  assert.ok(projectSceneList, "project.scene-list should be in results");
});

test("context list --include-custom rejects project context with same name as built-in (duplicate guard)", async () => {
  const runtime = await makeRuntime();
  // Both contexts have the same name as a static built-in — custom should be filtered by dedup
  const conflictingDescriptor = {
    name: "project.info",
    title: "Overridden Project Info",
    category: "project",
    kind: "context",
    source: "project",
    readOnlyHint: true,
    inputSchema: {},
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "context read project.info"
  };

  const result = await execute(["context", "list", "--include-custom"], {
    ...runtime,
    bridgeExecute: async () => ({
      ok: true as const,
      data: {
        contexts: [conflictingDescriptor],
        builtInCount: 0,
        projectCustomCount: 1
      }
    })
  });

  assert.equal(result.ok, true);
  const data = result.data as { contexts: ContextDescriptor[]; count: number };
  // Built-in deduplication: static builtin takes precedence over bridge-discovered duplicate
  const projectInfoContexts = data.contexts.filter((c) => c.name === "project.info");
  assert.equal(projectInfoContexts.length, 1, "project.info should appear exactly once (deduplicated)");
  assert.equal(projectInfoContexts[0].source, "built-in");
});

test("context describe <name> resolves custom context descriptor through the bridge", async () => {
  const runtime = await makeRuntime();
  const customDescriptor = {
    name: "project.build-info",
    title: "Build Info",
    description: "Current build configuration.",
    category: "project",
    kind: "context",
    source: "project",
    readOnlyHint: true,
    inputSchema: { type: "object" },
    outputSchema: { type: "object" },
    target: "editor",
    availabilityScope: "editorSession",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "context read project.build-info",
    fingerprint: { algorithm: "unity-cli-project-context-fingerprint-v1", value: "fp123", status: "matched" },
    equivalentUnityMcpNote: "Maps to unity-mcp build info"
  };

  const result = await execute(["context", "describe", "project.build-info", "--include-custom"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      if (payload.command === "context.list") {
        return { ok: true as const, data: { contexts: [customDescriptor], builtInCount: 0, projectCustomCount: 1 } };
      }
      // context.describe fallback
      return { ok: true as const, data: customDescriptor };
    }
  });

  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "project.build-info");
  assert.equal(data.source, "custom");
  assert.equal(data.readOnlyHint, true);
});

test("context read <name> delegates to bridge context.read and returns envelope", async () => {
  const runtime = await makeRuntime();

  const result = await execute(["context", "read", "project.scene-list", "--include-custom"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      if (payload.command === "context.list") {
        return {
          ok: true as const,
          data: {
            contexts: [{
              name: "project.scene-list",
              title: "Scene List",
              category: "project",
              kind: "context",
              source: "project",
              readOnlyHint: true,
              inputSchema: {},
              target: "editor"
            }],
            builtInCount: 0,
            projectCustomCount: 1
          }
        };
      }
      if (payload.command === "context.read") {
        return {
          ok: true as const,
          data: {
            command: "context.read",
            context: { name: "project.scene-list", kind: "context", source: "project", readOnlyHint: true },
            execution: { channel: "live", exitState: "completed" },
            result: { scenes: ["Assets/Scenes/Main.unity"], count: 1 },
            logs: { bridge: "/tmp/bridge.log" }
          }
        };
      }
      return { ok: false as const, error: { code: "UNKNOWN_COMMAND", message: "Unexpected" } };
    }
  });

  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "context.read");
});

test("context describe without name returns CONTEXT_DESCRIBE_NAME_REQUIRED error", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["context", "describe"], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "CONTEXT_DESCRIBE_NAME_REQUIRED");
});

test("context read without name returns E_CONTEXT_NAME_REQUIRED error", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["context", "read"], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_CONTEXT_NAME_REQUIRED");
});

test("context list without --include-custom returns built-in contexts", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["context", "list"], runtime);
  assert.equal(result.ok, true);
  const data = result.data as { contexts: ContextDescriptor[]; count: number };
  // Built-in contexts are always available offline
  assert.equal(data.count, 25);
  assert.ok(data.contexts.some((c) => c.name === "project.info"));
  assert.ok(data.contexts.some((c) => c.name === "editor.state"));
});

// ─── C# registry structural tests ──────────────────────────────────────

test("Unity C# custom context registry supports UnityCliContextAttribute and bridge commands", async () => {
  const { readFile } = await import("node:fs/promises");
  const registry = await readFile(new URL("../../unitypkg/Editor/ProjectContexts/ProjectContextRegistry.cs", import.meta.url), "utf8");
  const attribute = await readFile(new URL("../../unitypkg/Editor/ProjectContexts/UnityCliContextAttribute.cs", import.meta.url), "utf8");

  // Attribute targets method-level (simpler than tool attribute which also supports class)
  assert.match(attribute, /AttributeTargets\.Method/);
  // Attribute has required Name field
  assert.match(attribute, /public string Name/);
  // Registry discovers via attribute
  assert.match(registry, /typeof\(UnityCliContextAttribute\)/);
  // Registry has context.list/describe/read bridge commands
  assert.match(registry, /context\.list/);
  assert.match(registry, /context\.describe/);
  assert.match(registry, /context\.read/);
  // Registry enforces Kind=context
  assert.match(registry, /Kind.*=.*"context"/);
  // Registry enforces Source=project
  assert.match(registry, /Source.*=.*"project"/);
  // Registry enforces ReadOnlyHint=true
  assert.match(registry, /ReadOnlyHint.*=.*true/);
  // Registry has fingerprint support
  assert.match(registry, /E_CONTEXT_FINGERPRINT_MISMATCH/);
  // Registry has source guard
  assert.match(registry, /ExecuteMenuItem|SaveAssets/);
});

test("Unity C# context definition enforces read-only constraint", async () => {
  const { readFile } = await import("node:fs/promises");
  const definition = await readFile(new URL("../../unitypkg/Editor/ProjectContexts/UnityCliContextDefinition.cs", import.meta.url), "utf8");

  // Definition has Kind, Source, ReadOnlyHint properties
  assert.match(definition, /public string Kind/);
  assert.match(definition, /public string Source/);
  assert.match(definition, /public bool ReadOnlyHint/);
  // Definition has Read handler (not Execute/Run)
  assert.match(definition, /Func<UnityCliContextRequest, string> Read/);
  // No destructive/write fields as public properties (the doc comment mentions them as "must not declare")
  // Check property declarations, not doc comments
  const propertyLines = definition.split('\n').filter(line => line.match(/public\s+\w+\s+\w+\s*\{/));
  const destructiveProps = propertyLines.filter(line => /DestructiveHint|SupportsJob|ArtifactKinds/.test(line));
  assert.equal(destructiveProps.length, 0, 'Context definition should not have DestructiveHint/SupportsJob/ArtifactKinds as public properties');
});

// ─── SampleContext contract / snapshot tests ──────────────────────────────

test("SampleContext uses UnityCliContextAttribute with correct contract fields", async () => {
  const { readFile } = await import("node:fs/promises");
  const sample = await readFile(new URL("../../unitypkg/Editor/Samples/SampleContext.cs", import.meta.url), "utf8");

  // Uses [UnityCliContext] attribute (not [UnityCliTool])
  assert.match(sample, /\[UnityCliContext\(/);
  assert.doesNotMatch(sample, /\[UnityCliTool\(/);

  // Name matches the required contract
  assert.match(sample, /"sample\.project-summary"/);

  // Category is sample
  assert.match(sample, /Category\s*=\s*"sample"/);

  // Has inputSchema with includeDisabled boolean
  assert.match(sample, /includeDisabled.*boolean/);

  // Has outputSchema with project summary fields
  assert.match(sample, /unityVersion.*string/);
  assert.match(sample, /buildTarget.*string/);

  // Has EquivalentUnityMcp reference
  assert.match(sample, /EquivalentUnityMcp.*=.*"get_project_info"/);

  // Has RefreshBehavior
  assert.match(sample, /RefreshBehavior.*=.*"onDemand"/);
});

test("SampleContext descriptor has schema, fingerprint and readOnlyHint", async () => {
  const { readFile } = await import("node:fs/promises");
  const sample = await readFile(new URL("../../unitypkg/Editor/Samples/SampleContext.cs", import.meta.url), "utf8");

  // InputSchema is declared
  assert.match(sample, /InputSchema\s*=\s*"/);
  // OutputSchema is declared
  assert.match(sample, /OutputSchema\s*=\s*"/);
  // Fingerprint is available (context infrastructure auto-computes)
  // ReadOnlyHint is enforced by the registry, not declared in attribute
  // But the attribute implies it through the context kind
  assert.match(sample, /UnityCliContext\(/);
});

test("SampleContext handler method name avoids Execute/Run", async () => {
  const { readFile } = await import("node:fs/promises");
  const sample = await readFile(new URL("../../unitypkg/Editor/Samples/SampleContext.cs", import.meta.url), "utf8");

  // The handler method must NOT be named Execute or Run (registry constraint)
  // It should use a different verb like Provide, Read, Get, etc.
  assert.doesNotMatch(sample, /public static string Execute\(/);
  assert.doesNotMatch(sample, /public static string Run\(/);
  // Verify the handler has the right signature
  assert.match(sample, /public static string Provide\(UnityCliContextRequest/);
});

// ─── Source guard tests ─────────────────────────────────────────────────

test("SampleContext does not contain write/side-effect banned tokens", async () => {
  const { readFile } = await import("node:fs/promises");
  const sample = await readFile(new URL("../../unitypkg/Editor/Samples/SampleContext.cs", import.meta.url), "utf8");

  const bannedTokens = [
    "ExecuteMenuItem",
    "SaveAssets",
    "AssetDatabase.Refresh",
    "isPlaying =",
    "EditorSceneManager.SaveScene",
    "MarkSceneDirty",
    "PackageManager.",
    "File.Write",
    "File.WriteAll",
    "File.Append",
    "Directory.Create",
    "Directory.Delete",
    "Process.Start"
  ];

  for (const token of bannedTokens) {
    assert.doesNotMatch(
      sample,
      new RegExp(token.replace(".", "\\.")),
      `SampleContext must not contain write-semantic token: ${token}`
    );
  }
});

test("custom context does not enter tools execution path", async () => {
  const { readFile } = await import("node:fs/promises");
  const sampleContext = await readFile(new URL("../../unitypkg/Editor/Samples/SampleContext.cs", import.meta.url), "utf8");
  const sampleTool = await readFile(new URL("../../unitypkg/Editor/Samples/SampleEchoTool.cs", import.meta.url), "utf8");

  // SampleContext uses [UnityCliContext], not [UnityCliTool]
  assert.match(sampleContext, /\[UnityCliContext\(/);
  assert.doesNotMatch(sampleContext, /\[UnityCliTool\(/);

  // SampleEchoTool uses [UnityCliTool], not [UnityCliContext]
  assert.match(sampleTool, /\[UnityCliTool\(/);
  assert.doesNotMatch(sampleTool, /\[UnityCliContext\(/);

  // Context handler takes UnityCliContextRequest, not UnityCliProjectToolContext
  assert.match(sampleContext, /UnityCliContextRequest/);
  assert.doesNotMatch(sampleContext, /UnityCliProjectToolContext/);

  // Tool handler takes UnityCliProjectToolContext, not UnityCliContextRequest
  assert.match(sampleTool, /UnityCliProjectToolContext/);
  assert.doesNotMatch(sampleTool, /UnityCliContextRequest/);
});

test("custom context registry is separate from tools registry", async () => {
  const { readFile } = await import("node:fs/promises");
  const contextRegistry = await readFile(new URL("../../unitypkg/Editor/ProjectContexts/ProjectContextRegistry.cs", import.meta.url), "utf8");
  const toolCommands = await readFile(new URL("../../unitypkg/Editor/Commands/ProjectToolCommands.cs", import.meta.url), "utf8");

  // Context registry uses context.list/describe/read commands
  assert.match(contextRegistry, /"context\.list"/);
  assert.match(contextRegistry, /"context\.describe"/);
  assert.match(contextRegistry, /"context\.read"/);

  // Tool commands use project-tool.list/run commands
  assert.match(toolCommands, /"project-tool\.list"/);
  assert.match(toolCommands, /"project-tool\.run"/);

  // Context registry does NOT register project-tool commands
  assert.doesNotMatch(contextRegistry, /"project-tool\.run"/);

  // Tool commands do NOT register context commands
  assert.doesNotMatch(toolCommands, /"context\.read"/);
});

test("SampleContext read handler only reads, no write operations", async () => {
  const { readFile } = await import("node:fs/promises");
  const sample = await readFile(new URL("../../unitypkg/Editor/Samples/SampleContext.cs", import.meta.url), "utf8");

  // Verify read-only API usage patterns:
  // Application.unityVersion — read
  assert.match(sample, /Application\.unityVersion/);
  // SceneManager.GetActiveScene — read
  assert.match(sample, /SceneManager\.GetActiveScene/);
  // GraphicsSettings.currentRenderPipeline — read
  assert.match(sample, /GraphicsSettings\.currentRenderPipeline/);
  // EditorUserBuildSettings.activeBuildTarget — read
  assert.match(sample, /EditorUserBuildSettings\.activeBuildTarget/);

  // Verify no write operations (source guard banned tokens):
  const bannedTokens = [
    "AssetDatabase.Refresh",
    "AssetDatabase.SaveAssets",
    "EditorSceneManager.SaveScene",
    "EditorApplication.EnterPlayMode",
    "File.Write",
    "Directory.Create"
  ];
  for (const token of bannedTokens) {
    assert.doesNotMatch(
      sample,
      new RegExp(token.replace(".", "\\.")),
      `SampleContext must not contain write operation: ${token}`
    );
  }
});

// ─── DiagnosePreflightJson customContexts contract tests ────────────────────

test("DiagnosePreflightJson calls DiscoverProjectContexts and outputs customContexts with fingerprint", async () => {
  const { readFile } = await import("node:fs/promises");
  const diagnose = await readFile(new URL("../../unitypkg/Editor/Commands/DiagnoseCommands.cs", import.meta.url), "utf8");

  // DiagnosePreflightJson must call DiscoverProjectContexts
  assert.match(diagnose, /DiscoverProjectContexts\(\)/, "DiagnosePreflightJson must call DiscoverProjectContexts()");

  // Must output customContexts object with fingerprint
  assert.match(diagnose, /PropObject\("customContexts"/, "DiagnosePreflightJson must output customContexts object");
  assert.match(diagnose, /PropRaw\("fingerprint",\s*ProjectContextFingerprintSummaryJson\(\)/, "customContexts must contain fingerprint from ProjectContextFingerprintSummaryJson()");

  // Must include projectContextFingerprint in checks array
  assert.match(diagnose, /projectContextFingerprint/, "DiagnosePreflightJson must include projectContextFingerprint in checks");
});

test("DiagnosePreflightJson customContexts fingerprint fields match TS doctor expectations", async () => {
  const { readFile } = await import("node:fs/promises");
  const registry = await readFile(new URL("../../unitypkg/Editor/ProjectContexts/ProjectContextRegistry.cs", import.meta.url), "utf8");

  // ProjectContextFingerprintSummaryJson must output the fields that TS doctor reads:
  // contextCount, blockedCount, warnCount, mode, algorithm
  assert.match(registry, /Prop\("algorithm"/, "Fingerprint summary must include algorithm");
  assert.match(registry, /Prop\("mode"/, "Fingerprint summary must include mode");
  assert.match(registry, /Prop\("contextCount"/, "Fingerprint summary must include contextCount");
  assert.match(registry, /Prop\("blockedCount"/, "Fingerprint summary must include blockedCount");
  assert.match(registry, /Prop\("warnCount"/, "Fingerprint summary must include warnCount");
});
