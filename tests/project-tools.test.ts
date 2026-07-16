/**
 * Tests for Attribute-first custom tool descriptors and CLI discovery.
 */

import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { execute } from "../src/cli.js";
import { projectDescriptorToToolMetadata } from "../src/project-tool-discovery.js";
import { JobRunner } from "../src/job-runner.js";
import { JobStore } from "../src/job-store.js";
import type { BridgeRequest } from "../src/bridge/index.js";
import { mkdtemp } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-project-tools-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

test("live Tool example execution metadata becomes CLI flags without entering input", () => {
  const tool = projectDescriptorToToolMetadata({
    name: "sample.preview",
    summary: "Preview a sample operation.",
    description: "Preview a sample operation.",
    group: "sample",
    inputSchema: { type: "object", properties: { path: { type: "string" } }, required: ["path"], additionalProperties: false },
    destructiveHint: false,
    examples: [{ description: "Preview a path", input: { path: "Assets/Test.asset" }, execution: { dryRun: true, timeoutMs: 5000 } }]
  });
  assert.ok(tool);
  assert.deepEqual(tool.examples?.[0].input, { path: "Assets/Test.asset" });
  assert.deepEqual(tool.examples?.[0].argv, ["tools", "run", "sample.preview", "--input", "{\"path\":\"Assets/Test.asset\"}", "--dry-run", "--timeout-ms", "5000"]);
});

test("tools group list <sample> --include-custom discovers Unity project custom descriptors through the bridge", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const customDescriptor = {
    name: "sample.echo",
    summary: "Echoes input from an Attribute-first Unity Editor tool.",
    description: "Echoes input from an Attribute-first Unity Editor tool.",
    group: "sample",
    inputSchema: { type: "object", properties: { message: { type: "string" } }, required: ["message"] },
    outputSchema: { type: "object" },
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run sample.echo",
    playModeSupport: "editor-only"
  };

  const result = await execute(["tools", "group", "list", "sample", "--include-custom"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { tools: [customDescriptor] } };
    }
  });

  assert.ok(result.ok);
  assert.equal(requests.length, 1);
  assert.equal(requests[0].command, "project-tool.list");
  const data = result.data as { tools: Array<{ name: string; source: string; playModeSupport?: string }> };
  assert.deepEqual(data.tools.map((tool) => tool.name), ["sample.echo"]);
  assert.equal(data.tools[0].source, "project");
  assert.equal(data.tools[0].playModeSupport, "editor-only");
});

test("tools describe resolves custom descriptor without hand-written CLI metadata", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "describe", "sample.echo"], {
    ...runtime,
    bridgeExecute: async (payload) => ({
      ok: true,
      requestId: payload.requestId,
      data: {
        tools: [{
          name: "sample.echo",
          summary: "Echoes input from an Attribute-first Unity Editor tool.",
          description: "Echoes input from an Attribute-first Unity Editor tool.",
          group: "sample",
          inputSchema: { type: "object" },
          destructiveHint: false,
          playModeSupport: "editor-only"
        }]
      }
    })
  });

  assert.ok(result.ok);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "sample.echo");
  assert.equal(data.source, "project");
  assert.equal("mode" in data, false);
  assert.equal("requiresUnity" in data, false);
  assert.equal(data.supportStatus, "live-bridge");
  assert.equal(data.cliCommand, "tools run sample.echo");
  assert.equal(data.playModeSupport, "editor-only");
});

test("tools describe infers runtime route for target=runtime project descriptors", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "describe", "runtime.wait"], {
    ...runtime,
    bridgeExecute: async (payload) => ({
      ok: true,
      requestId: payload.requestId,
      data: {
        tools: [{
          name: "runtime.wait",
          summary: "Wait for Play Mode frames.",
          description: "Waits in Play Mode and returns elapsed frame data.",
          group: "runtime",
          inputSchema: { type: "object" },
          destructiveHint: false,
          target: "runtime",
          playModeSupport: "runtime-only"
        }]
      }
    })
  });

  assert.ok(result.ok);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "runtime.wait");
  assert.equal(data.source, "project");
  assert.equal(data.target, "runtime");
  assert.equal(data.playModeSupport, "runtime-only");
  assert.equal(data.supportStatus, "live-bridge-runtime");
  assert.equal(data.availabilityScope, "playModeSession");
});

test("Unity C# custom tool registry supports IUnityCliTool interface descriptors", async () => {
  const registry = await readFile(new URL("../../unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs", import.meta.url), "utf8");
  const runtimeExecutor = await readFile(new URL("../../unitypkg/Editor/Infrastructure/UnityCliRuntimeExecutor.cs", import.meta.url), "utf8");
  const protocolRequest = await readFile(new URL("../../unitypkg/Editor/Infrastructure/UnityCliProtocolRequest.cs", import.meta.url), "utf8");
  const interfaceFile = await readFile(new URL("../../unitypkg/Editor/ProjectTools/IUnityCliTool.cs", import.meta.url), "utf8");

  assert.match(interfaceFile, /interface IUnityCliTool<TReq, TRes>/);
  assert.match(interfaceFile, /ToolDescriptor Describe\(\)/);
  assert.match(interfaceFile, /ValueTask<TRes> ExecuteAsync\(TReq request, UnityCliProjectToolContext ctx, CancellationToken ct = default\)/);
  assert.match(registry, /TryRegisterInterfaceProjectTool/);
  assert.match(registry, /typeof\(IUnityCliTool<,>\)/);
  assert.match(registry, /UnityCliSchemaDeriver\.DeriveSchema/);
  assert.match(registry, /\[UnityCliAnyJson\] public object Input \{ get; set; \}/);
  assert.match(runtimeExecutor, /\[UnityCliAnyJson\] public object Input \{ get; set; \}/);
  assert.match(registry, /request\.ExecutionTimeoutMs\(definition\.DefaultTimeoutMs, definition\.MaxTimeoutMs\)/);
  assert.match(runtimeExecutor, /request\.ExecutionTimeoutMs\(definition\.DefaultTimeoutMs, definition\.MaxTimeoutMs\)/);
  assert.match(registry, /ProjectToolEffectsData\(definition, result\)/);
  assert.match(registry, /"declaredSideEffects"/);
  assert.match(registry, /"plannedEffectCount"/);
  assert.match(registry, /var fileWrites = fileWritesDeclared \? ProjectToolEffectPaths\(plannedEffects\) : Array\.Empty<string>\(\)/);
  assert.doesNotMatch(registry, /args\.TimeoutMs|ProjectToolExecutionArgs/);
  assert.doesNotMatch(runtimeExecutor, /args\.TimeoutMs/);
  assert.match(protocolRequest, /public int ExecutionTimeoutMs\(int fallback, int maximum\)/);
});
