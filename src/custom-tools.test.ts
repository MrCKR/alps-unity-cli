/**
 * Tests for Attribute-first custom tool descriptors and CLI discovery.
 */

import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { execute } from "./cli.js";
import { JobRunner } from "./job-runner.js";
import { JobStore } from "./job-store.js";
import type { BridgeRequest } from "./bridge/index.js";
import { mkdtemp } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-custom-tools-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

test("tools list --include-custom discovers Unity project custom descriptors through the bridge", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const customDescriptor = {
    name: "sample.echo",
    title: "Sample Echo",
    description: "Echoes input from an Attribute-first Unity Editor tool.",
    group: "sample",
    inputSchema: { type: "object", properties: { message: { type: "string" } }, required: ["message"] },
    outputSchema: { type: "object" },
    readOnlyHint: true,
    destructiveHint: false,
    architectureSensitive: true,
    frozenDecision: "ALP-329-Q1",
    coverageScope: ["attribute-first", "zero-config"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "custom-tool run sample.echo"
  };

  const result = await execute(["tools", "list", "--include-custom", "--group", "sample"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { tools: [customDescriptor] } };
    }
  });

  assert.equal(result.ok, true);
  assert.equal(requests.length, 1);
  assert.equal(requests[0].command, "custom-tool.list");
  const data = result.data as { tools: Array<{ name: string; group: string; architectureSensitive?: boolean }> };
  assert.deepEqual(data.tools.map((tool) => tool.name), ["sample.echo"]);
  assert.equal(data.tools[0].group, "sample");
  assert.equal(data.tools[0].architectureSensitive, true);
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
          title: "Sample Echo",
          description: "Echoes input from an Attribute-first Unity Editor tool.",
          group: "sample",
          inputSchema: { type: "object" },
          readOnlyHint: true,
          destructiveHint: false,
          architectureSensitive: true,
          frozenDecision: "ALP-329-Q1",
          coverageScope: ["attribute-first", "zero-config"]
        }]
      }
    })
  });

  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "sample.echo");
  assert.equal(data.mode, "bridge");
  assert.equal(data.requiresUnity, true);
  assert.equal(data.supportStatus, "live-bridge");
  assert.equal(data.cliCommand, "custom-tool run sample.echo");
  assert.equal(data.architectureSensitive, true);
  assert.deepEqual(data.coverageScope, ["attribute-first", "zero-config"]);
});

test("Unity C# custom tool registry supports UnityCliToolAttribute descriptors and sample Editor tool", async () => {
  const registry = await readFile(new URL("../unitypkg/Editor/CustomTools/CustomToolRegistry.cs", import.meta.url), "utf8");
  const attribute = await readFile(new URL("../unitypkg/Editor/CustomTools/UnityCliToolAttribute.cs", import.meta.url), "utf8");
  const sample = await readFile(new URL("../unitypkg/Editor/Samples/SampleEchoTool.cs", import.meta.url), "utf8");

  assert.match(attribute, /AttributeTargets\.Class \| AttributeTargets\.Method/);
  assert.match(attribute, /public string Group \{ get; set; \}/);
  assert.match(attribute, /public bool ArchitectureSensitive \{ get; set; \}/);
  assert.match(registry, /typeof\(UnityCliToolAttribute\)/);
  assert.match(registry, /reason=ambiguous_class_level_execute_method/);
  assert.match(registry, /specific method when multiple candidates exist/);
  assert.match(registry, /CustomToolDefinitionJson/);
  assert.match(registry, /CustomToolFingerprintJson/);
  assert.match(registry, /CustomToolHandlerFingerprintMaterial/);
  assert.match(registry, /E_TOOL_FINGERPRINT_MISMATCH/);
  assert.match(registry, /UNITY_CLI_CUSTOM_TOOL_FINGERPRINT_MODE/);
  assert.match(registry, /UnityCliCustomToolFingerprints/);
  assert.match(registry, /sha256-handler-code-plus-descriptor-v1/);
  assert.match(registry, /Prop\("descriptorSource", definition\.DescriptorSource\)/);
  assert.match(registry, /PropRaw\("fingerprint", CustomToolFingerprintJson\(definition\)\)/);
  assert.match(registry, /Prop\("handler", "bridge"\)/);
  assert.match(sample, /\[UnityCliTool\(\s*"sample\.echo"/);
  assert.doesNotMatch(sample, /manifest\.json|config\.json|\.yaml|\.yml/);
});
