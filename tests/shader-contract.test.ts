import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName, filterToolsByGroup } from "../src/tools/metadata/index.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { readFile, readdir } from "node:fs/promises";

/** 递归列举 .cs 文件。 */
async function listCsFiles(directory: URL): Promise<URL[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  const urls = await Promise.all(entries.map(async (entry) => {
    const child = new URL(`${entry.name}${entry.isDirectory() ? "/" : ""}`, directory);
    if (entry.isDirectory()) {
      return listCsFiles(child);
    }
    return entry.isFile() && entry.name.endsWith(".cs") ? [child] : [];
  }));
  return urls.flat().sort((left, right) => left.pathname.localeCompare(right.pathname));
}

/**
 * Shader 域契约测试：shader.info / read / create / update / list / get-data。
 * 对齐 IvanMurzak assets-shader-get-data / assets-shader-list-all 与 CoplayDev shader 工具，
 * 验证 metadata/bridge/contract/C# 四层一致，且错误恢复提示已迁移到当前 `tools run` 形态。
 */

test("shader group tools exist with correct safety hints", () => {
  const tools = filterToolsByGroup("shader").map((t) => t.name).sort();
  assert.deepEqual(tools, ["shader.create", "shader.delete", "shader.get-data", "shader.info", "shader.list", "shader.read", "shader.update"]);

  const info = findToolByName("shader.info")!;
  assert.equal(info.destructiveHint, false);
  assert.equal(info.handler, "bridge");
  assert.equal(info.playModeSupport, "supported");

  const create = findToolByName("shader.create")!;
  assert.equal(create.playModeSupport, "session-affecting");
  assert.ok(create.sideEffects?.includes("asset-write"));

  const update = findToolByName("shader.update")!;
  assert.equal(update.playModeSupport, "session-affecting");
  assert.ok(update.sideEffects?.includes("shader-recompile"));
  assert.equal(update.riskLevel, "medium");

  const del = findToolByName("shader.delete")!;
  assert.equal(del.destructiveHint, true);
  assert.equal(del.supportsDryRun, true);
  assert.ok(del.sideEffects?.includes("asset-delete"));

  const list = findToolByName("shader.list")!;
  assert.equal(list.playModeSupport, "supported");

  const getData = findToolByName("shader.get-data")!;
  assert.equal(getData.playModeSupport, "supported");
});

test("shader.read, shader.list and shader.get-data expose bounded output schemas", () => {
  const read = findToolByName("shader.read")!;
  const readProps = read.inputSchema.properties as Record<string, unknown>;
  const readOut = read.outputSchema?.properties as Record<string, unknown>;
  assert.ok(readProps.offset, "shader.read should accept offset continuation");
  assert.ok(readProps.maxBytes, "shader.read should accept maxBytes cap");
  assert.ok(readOut.nextOffset, "shader.read output should expose nextOffset when truncated");
  assert.match(read.description, /nextOffset/, "shader.read should document continuation");

  const list = findToolByName("shader.list")!;
  const listOut = list.outputSchema?.properties as Record<string, unknown>;
  assert.ok(listOut.shaders, "list output should expose shaders array");
  assert.ok(listOut.count, "list output should expose count");

  const getData = findToolByName("shader.get-data")!;
  const props = getData.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.path, "get-data should accept path");
  assert.ok(props.name, "get-data should accept name for Shader.Find resolution");
  assert.equal((props.includeSourceCode as { default?: boolean }).default, false, "source code should be opt-in");
  assert.ok(props.maxSubshaders, "get-data should expose maxSubshaders cap");
  assert.ok(props.maxPasses, "get-data should expose maxPasses cap");
  const out = getData.outputSchema?.properties as Record<string, unknown>;
  assert.ok(out.properties, "get-data output should expose properties");
  assert.ok(out.isSupported, "get-data output should expose isSupported");
  assert.ok(out.diagnostics, "get-data output should expose diagnostics");
  assert.ok(out.subshaders, "get-data output should expose subshader/pass structure");
});

test("bridge specs resolve for shader tools", () => {
  for (const command of ["info", "read", "create", "update", "delete", "list", "get-data"]) {
    const spec = bridgeCommandRegistry.lookup("shader", command);
    assert.equal(spec?.bridgeCommand, "project-tool.run", `shader ${command} should route via project-tool.run`);
    assert.equal(spec?.toolName, `shader.${command}`, `shader ${command} toolName should match`);
  }
});

test("contract entries exist for shader tools with present C# handler", () => {
  for (const name of ["shader.info", "shader.read", "shader.create", "shader.update", "shader.delete", "shader.list", "shader.get-data"]) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in command contract`);
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("C# handlers exist for shader commands (RegisterCommand or IUnityCliTool)", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  for (const command of ["shader.info", "shader.read", "shader.create", "shader.update", "shader.delete", "shader.list", "shader.get-data"]) {
    // 接受 RegisterCommand(旧)或 IUnityCliTool(新,Name="...")两种形态
    const hasRegisterCommand = allSource.includes(`RegisterCommand(registry, "${command}"`);
    const hasIUnityCliTool = allSource.includes(`Name = "${command}"`);
    assert.ok(hasRegisterCommand || hasIUnityCliTool, `${command} should be registered via RegisterCommand or IUnityCliTool`);
  }
});

test("shader C# error recovery hints use current tools run form, not legacy unity_cli", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/ShaderCommands.cs", import.meta.url), "utf8");
  assert.equal(source.includes("unity_cli shader info"), false, "must not reference legacy 'unity_cli shader info'");
  assert.equal(source.includes("unity_cli asset search"), false, "must not reference legacy 'unity_cli asset search'");
  assert.match(source, /tools run asset\.search/, "error hints should route to tools run asset.search");
  assert.match(source, /tools run shader\.list/, "error hints should route to tools run shader.list");
});

test("shader.get-data C# wraps result and returns diagnostics plus subshader structure", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/ShaderCommands.cs", import.meta.url), "utf8");
  const methodBlock = source.slice(source.indexOf("ShaderGetData(BridgeRequest request)"), source.indexOf("private static string ShaderInfo"));
  assert.match(methodBlock, /return SuccessJson\(w\.EndObject/, "ShaderGetData must wrap its payload in SuccessJson");
  assert.match(methodBlock, /ShaderDiagnosticsJson/, "ShaderGetData should include compile diagnostics");
  assert.match(source, /WriteShaderSubshadersJson[\s\S]*GetShaderData/, "ShaderGetData should expose SubShader/Pass structure through ShaderUtil");
  assert.match(source, /ShaderDelete[\s\S]*AssetDatabase\.DeleteAsset/, "shader.delete should delete through AssetDatabase");
});

test("shader bridge spec accepts structured content and source alias for create/update", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/shader.js", import.meta.url), "utf8");
  assert.match(spec, /resolveShaderContent/, "shader bridge should centralize content resolution");
  assert.match(spec, /stringField\(flags, "source"\)/, "shader bridge should accept structured 'source' alias");
  assert.match(spec, /stringField\(flags, "content"\)/, "shader bridge should accept structured 'content'");
  assert.match(spec, /includeSubshaders/, "shader bridge should forward get-data includeSubshaders");
  assert.match(spec, /maxPasses/, "shader bridge should forward get-data maxPasses");
});
