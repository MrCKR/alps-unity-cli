import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";
import { contractToolNames, discoverLiveTools, readCsSources, readEditorCsSources } from "./helpers/csharp-test-helpers.js";

const SHADER_COMMANDS = ["shader.create", "shader.delete", "shader.get-data", "shader.info", "shader.list", "shader.read", "shader.update"];

test("shader group tools are contract-discovered with correct live descriptor hints", async () => {
  assert.deepEqual(contractToolNames("shader"), [...SHADER_COMMANDS].sort());

  const tools = await discoverLiveTools(
    makeLiveTool({ name: "shader.info", summary: "Read shader info.", description: "Read shader info.", group: "shader" }),
    makeLiveTool({ name: "shader.create", summary: "Create a shader asset.", description: "Create a shader asset.", group: "shader", playModeSupport: "session-affecting", sideEffects: ["asset-write"] }),
    makeLiveTool({ name: "shader.update", summary: "Update a shader asset.", description: "Update a shader asset.", group: "shader", playModeSupport: "session-affecting", sideEffects: ["shader-recompile"], destructiveHint: false }),
    makeLiveTool({ name: "shader.delete", summary: "Delete a shader asset.", description: "Delete a shader asset.", group: "shader", destructiveHint: true, supportsDryRun: true, sideEffects: ["asset-delete"] }),
    makeLiveTool({ name: "shader.list", summary: "List shaders.", description: "List shaders.", group: "shader", outputSchema: { type: "object", properties: { shaders: { type: "array" }, count: { type: "integer" } } } }),
    makeLiveTool({ name: "shader.read", summary: "Read shader source.", description: "Read shader source with nextOffset continuation.", group: "shader", inputSchema: { type: "object", properties: { offset: { type: "integer" }, maxBytes: { type: "integer" } } }, outputSchema: { type: "object", properties: { nextOffset: { type: "integer" } } } }),
    makeLiveTool({ name: "shader.get-data", summary: "Read structured shader data.", description: "Read structured shader data.", group: "shader", inputSchema: { type: "object", properties: { path: { type: "string" }, name: { type: "string" }, includeSourceCode: { type: "boolean", default: false }, maxSubshaders: { type: "integer" }, maxPasses: { type: "integer" } } }, outputSchema: { type: "object", properties: { properties: { type: "array" }, isSupported: { type: "boolean" }, diagnostics: { type: "array" }, subshaders: { type: "array" } } } })
  );

  const info = tools.get("shader.info")!;
  assert.equal(info.destructiveHint, false);
  assert.equal(info.handler, "bridge");
  assert.equal(info.playModeSupport, "supported");

  const create = tools.get("shader.create")!;
  assert.equal(create.playModeSupport, "session-affecting");
  assert.ok(create.sideEffects?.includes("asset-write"));

  const update = tools.get("shader.update")!;
  assert.equal(update.playModeSupport, "session-affecting");
  assert.ok(update.sideEffects?.includes("shader-recompile"));
  assert.equal(update.riskLevel, "medium");

  const del = tools.get("shader.delete")!;
  assert.equal(del.destructiveHint, true);
  assert.equal(del.supportsDryRun, true);
  assert.ok(del.sideEffects?.includes("asset-delete"));

  assert.equal(tools.get("shader.list")?.playModeSupport, "supported");
  assert.equal(tools.get("shader.get-data")?.playModeSupport, "supported");

  const read = tools.get("shader.read")!;
  const readProps = read.inputSchema.properties as Record<string, unknown>;
  const readOut = read.outputSchema?.properties as Record<string, unknown>;
  assert.ok(readProps.offset);
  assert.ok(readProps.maxBytes);
  assert.ok(readOut.nextOffset);
  assert.match(read.description, /nextOffset/);

  const listOut = tools.get("shader.list")!.outputSchema?.properties as Record<string, unknown>;
  assert.ok(listOut.shaders);
  assert.ok(listOut.count);

  const getData = tools.get("shader.get-data")!;
  const props = getData.inputSchema.properties as Record<string, { default?: boolean }>;
  assert.ok(props.path);
  assert.ok(props.name);
  assert.equal(props.includeSourceCode.default, false);
  assert.ok(props.maxSubshaders);
  assert.ok(props.maxPasses);
  const out = getData.outputSchema?.properties as Record<string, unknown>;
  assert.ok(out.properties);
  assert.ok(out.isSupported);
  assert.ok(out.diagnostics);
  assert.ok(out.subshaders);
});

test("bridge specs resolve for shader tools", () => {
  for (const command of ["info", "read", "create", "update", "delete", "list", "get-data"]) {
    const spec = bridgeCommandRegistry.lookup("shader", command);
    assert.equal(spec?.bridgeCommand, "project-tool.run");
    assert.equal(spec?.toolName, `shader.${command}`);
  }
});

test("contract entries exist for shader tools with present C# handler", () => {
  for (const name of SHADER_COMMANDS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in command contract`);
    assert.equal(contract?.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("shader C# handlers are registered", async () => {
  const allSource = await readEditorCsSources();
  for (const command of SHADER_COMMANDS) {
    const hasRegisterCommand = allSource.includes(`RegisterCommand(registry, "${command}"`);
    const hasIUnityCliTool = allSource.includes(`Name = "${command}"`);
    assert.ok(hasRegisterCommand || hasIUnityCliTool);
  }
});

test("shader C# error recovery hints use current tools run forms", async () => {
  const source = await readCsSources(new URL("../unitypkg/Editor/Tools/shader/", import.meta.url));
  assert.match(source, /tools run asset\.search/);
  assert.match(source, /tools run shader\.list/);
});

test("shader.get-data C# returns diagnostics plus subshader structure", async () => {
  const source = await readCsSources(new URL("../unitypkg/Editor/Tools/shader/", import.meta.url));
  assert.match(source, /class ShaderGetDataTool : IUnityCliTool</);
  assert.match(source, /Diagnostics = includeMessages[\s\S]*BuildDiagnostics\(shader\)[\s\S]*BuildDiagnosticsSummary\(shader\)/);
  assert.match(source, /BuildSubshaders\(shader, includeSourceCode, maxSubshaders, maxPasses\)/);
  assert.match(source, /GetShaderData/);
  assert.match(source, /AssetDatabase\.DeleteAsset/);
});

test("shader delete confirms removal from disk and AssetDatabase", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/shader/delete.cs", import.meta.url), "utf8");
  assert.match(source, /ctx\.Wait\.Until\([\s\S]*!File\.Exists\(fullPath\)[\s\S]*!Directory\.Exists\(fullPath\)[\s\S]*AssetPathToGUID\(path\)[\s\S]*LoadMainAssetAtPath\(path\) == null/);
  assert.match(source, /E_SHADER_DELETE_PROOF_MISSING/);
});

test("shader bridge spec accepts only structured content for create/update", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/shader.js", import.meta.url), "utf8");
  assert.match(spec, /resolveShaderContent/);
  assert.doesNotMatch(spec, /stringField\(flags, "source"\)/);
  assert.match(spec, /stringField\(flags, "content"\)/);
  assert.match(spec, /includeSubshaders/);
  assert.match(spec, /maxPasses/);
});
