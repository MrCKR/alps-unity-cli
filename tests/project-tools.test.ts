/**
 * Tests for Attribute-first custom tool descriptors and CLI discovery.
 */

import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { execute } from "../src/cli.js";
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
      return { ok: true, data: { tools: [customDescriptor] } };
    }
  });

  assert.equal(result.ok, true);
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
    bridgeExecute: async () => ({
      ok: true,
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

  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "sample.echo");
  assert.equal(data.source, "project");
  assert.equal("mode" in data, false);
  assert.equal("requiresUnity" in data, false);
  assert.equal(data.supportStatus, "live-bridge");
  assert.equal(data.cliCommand, "tools run sample.echo");
  assert.equal(data.playModeSupport, "editor-only");
});

test("Unity C# custom tool registry supports IUnityCliTool interface descriptors and sample Editor tool", async () => {
  const registry = await readFile(new URL("../../unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs", import.meta.url), "utf8");
  const interfaceFile = await readFile(new URL("../../unitypkg/Editor/ProjectTools/IUnityCliTool.cs", import.meta.url), "utf8");
  const sample = await readFile(new URL("../../unitypkg/Editor/Samples/SampleEchoTool.cs", import.meta.url), "utf8");

  // 新形态:IUnityCliTool<TReq,TRes> 泛型接口声明工具
  assert.match(interfaceFile, /interface IUnityCliTool<TReq, TRes>/);
  assert.match(interfaceFile, /ToolDescriptor Describe\(\)/);
  assert.match(interfaceFile, /TRes Execute\(TReq request, UnityCliProjectToolContext ctx\)/);

  // registry 扫描 IUnityCliTool<,> 实现类(与旧 attribute 并存)
  assert.match(registry, /TryRegisterInterfaceProjectTool/);
  assert.match(registry, /typeof\(IUnityCliTool<,>\)/);
  assert.match(registry, /UnityCliSchemaDeriver\.DeriveSchema/);

  // SampleEchoTool 新形态:实现 IUnityCliTool,不再用 attribute
  assert.match(sample, /class SampleEchoTool : IUnityCliTool</);
  assert.match(sample, /public ToolDescriptor Describe\(\)/);
  assert.match(sample, /public Result Execute\(Request request, UnityCliProjectToolContext ctx\)/);
  assert.match(sample, /required string Message/);
  assert.doesNotMatch(sample, /\[UnityCliTool\(/);
  assert.doesNotMatch(sample, /InputSchema\s*=\s*"/);
  assert.doesNotMatch(sample, /manifest\.json|config\.json|\.yaml|\.yml/);
});

test("SampleContext custom context is not registered as a tool", async () => {
  const sampleContext = await readFile(new URL("../../unitypkg/Editor/Samples/SampleContext.cs", import.meta.url), "utf8");
  const registry = await readFile(new URL("../../unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs", import.meta.url), "utf8");

  // SampleContext uses [UnityCliContext], never [UnityCliTool]
  assert.match(sampleContext, /\[UnityCliContext\(/);
  assert.doesNotMatch(sampleContext, /\[UnityCliTool\(/);

  // The tool registry does not discover UnityCliContextAttribute — only UnityCliToolAttribute
  assert.match(registry, /typeof\(UnityCliToolAttribute\)/);
  assert.doesNotMatch(registry, /typeof\(UnityCliContextAttribute\)/);
});
