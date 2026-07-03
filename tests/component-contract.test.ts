import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName, filterToolsByGroup } from "../src/tools/metadata/index.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { destructiveCommands } from "../src/security-policy.js";
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
 * Component 域契约测试：component.get / component.modify / component.list-types。
 * 对齐 IvanMurzak gameobject-component-get/modify/list-all。
 */

test("component tools expose lifecycle and deep-read metadata", () => {
  const componentTools = filterToolsByGroup("component");
  const names = componentTools.map((t) => t.name).sort();
  assert.ok(names.includes("component.add"));
  assert.ok(names.includes("component.remove"));
  assert.ok(names.includes("component.set"));
  assert.ok(names.includes("component.get"));
  assert.ok(names.includes("component.modify"));
  assert.ok(names.includes("component.list-types"));

  const add = findToolByName("component.add")!;
  assert.equal(add.playModeSupport, "session-affecting");
  assert.deepEqual(add.sideEffects, ["component-add", "scene-modify"]);
  assert.ok(add.outputSchema);
  assert.ok(add.examples?.some((example) => example.argv.includes("--input")));

  const remove = findToolByName("component.remove")!;
  assert.equal(remove.destructiveHint, false);
  assert.equal(remove.playModeSupport, "session-affecting");
  assert.deepEqual(remove.sideEffects, ["component-remove", "scene-modify"]);

  const set = findToolByName("component.set")!;
  assert.equal(set.playModeSupport, "session-affecting");
  assert.deepEqual(set.sideEffects, ["component-modify", "scene-modify"]);
  assert.ok(set.outputSchema);
  assert.ok(set.examples?.some((example) => example.argv.includes("--input")));

  const get = findToolByName("component.get")!;
  assert.equal(get.playModeSupport, "supported");

  const modify = findToolByName("component.modify")!;
  assert.equal(modify.destructiveHint, false);
  assert.equal(modify.supportsDryRun, true);
  assert.equal(modify.playModeSupport, "session-affecting");

  const listTypes = findToolByName("component.list-types")!;
  assert.equal(listTypes.playModeSupport, "supported");
  assert.ok(listTypes.outputSchema);
});

test("component lifecycle schemas support properties and componentIndex", () => {
  const addProps = findToolByName("component.add")!.inputSchema.properties as Record<string, unknown>;
  assert.ok(addProps.properties, "component.add should accept initial properties");

  const setProps = findToolByName("component.set")!.inputSchema.properties as Record<string, unknown>;
  assert.ok(setProps.properties, "component.set should accept multiple properties");
  assert.ok(setProps.componentIndex, "component.set should select duplicate components by index");

  const removeProps = findToolByName("component.remove")!.inputSchema.properties as Record<string, unknown>;
  assert.ok(removeProps.componentIndex, "component.remove should select duplicate components by index");
  assert.ok(removeProps.components, "component.remove should accept a components array for batch removal");
});

test("component.modify input schema supports pathPatches, jsonPatch and componentDiff", () => {
  const modify = findToolByName("component.modify")!;
  const props = modify.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.pathPatches, "pathPatches should be in schema");
  assert.ok(props.jsonPatch, "jsonPatch should be in schema");
  assert.ok(props.componentDiff, "componentDiff should be in schema");
});

test("component.get supports ObjectRef resolution and token-safe read scopes", () => {
  const get = findToolByName("component.get")!;
  const props = get.inputSchema.properties as Record<string, unknown>;
  const output = get.outputSchema?.properties as Record<string, unknown>;
  assert.ok(props.instanceId, "instanceId should be supported");
  assert.ok(props.componentInstanceId, "componentInstanceId should be supported");
  assert.ok(props.componentIndex, "componentIndex should be supported");
  assert.ok(props.component, "component type name should be supported");
  assert.ok(props.paths, "paths should be supported for exact component reads");
  assert.ok(props.viewQuery, "viewQuery should be supported for scoped component reads");
  assert.ok(props.maxArrayItems, "maxArrayItems should cap large arrays/lists/dictionaries");
  assert.ok(output.data, "component.get output should expose serialized data");
  assert.ok(output.truncated, "component.get output should expose truncation continuation state");
  assert.ok(output.nextPath, "component.get output should expose nextPath for precision continuation");
  assert.match(get.description, /mutually exclusive/, "describe text should warn paths/viewQuery are mutually exclusive");
});

test("bridge specs resolve for component deep tools", () => {
  const componentGet = bridgeCommandRegistry.lookup("component", "get");
  assert.equal(componentGet?.bridgeCommand, "project-tool.run");
  assert.equal(componentGet?.toolName, "component.get");
  const componentModify = bridgeCommandRegistry.lookup("component", "modify");
  assert.equal(componentModify?.bridgeCommand, "project-tool.run");
  assert.equal(componentModify?.toolName, "component.modify");
  const componentListTypes = bridgeCommandRegistry.lookup("component", "list-types");
  assert.equal(componentListTypes?.bridgeCommand, "project-tool.run");
  assert.equal(componentListTypes?.toolName, "component.list-types");
});

test("contract entries exist for component tools with present C# handler", () => {
  for (const name of ["component.get", "component.modify", "component.list-types"]) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in command contract`);
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("component.modify is not in the destructive security policy", () => {
  const commands = new Set(destructiveCommands().map((e) => e.command));
  assert.equal(commands.has("component.modify"), false);
});

test("C# handlers exist for component deep commands (RegisterCommand or IUnityCliTool)", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  for (const command of ["component.get", "component.modify", "component.list-types"]) {
    const hasRegisterCommand = allSource.includes(`RegisterCommand(registry, "${command}"`);
    const hasIUnityCliTool = allSource.includes(`Name = "${command}"`);
    assert.ok(hasRegisterCommand || hasIUnityCliTool, `${command} should be registered via RegisterCommand or IUnityCliTool`);
  }
});

test("C# component lifecycle handlers support properties and indexed duplicate components", async () => {
  // component.add/set/remove 已迁移为 IUnityCliTool(unitypkg/Editor/Tools/component/*.cs);
  // helper 仍保留在 GameObjectCommands.cs 供其他域复用。
  const toolSource = await readFile(new URL("../unitypkg/Editor/Tools/component/add.cs", import.meta.url), "utf8");
  const supportSource = await readFile(new URL("../unitypkg/Editor/Tools/go/GameObjectToolSupport.cs", import.meta.url), "utf8");
  assert.match(supportSource, /ComponentPropertiesJson/);
  assert.match(supportSource, /BuildComponentPropertyChanges/);
  const setSource = await readFile(new URL("../unitypkg/Editor/Tools/component/set.cs", import.meta.url), "utf8");
  assert.match(supportSource, /ComponentPropertiesJson/);
  assert.match(supportSource, /BuildComponentPropertyChanges/);
  assert.match(setSource, /ComponentIndex/);
  assert.match(supportSource, /ResolveComponent\(GameObject gameObject, string componentName, int\? componentIndex\)/);
  assert.match(supportSource, /AllowsMultipleComponents/);
  assert.match(supportSource, /DisallowMultipleComponent/);
  assert.match(toolSource, /type\.IsInstanceOfType\(component\)/);
  assert.match(toolSource, /E_COMPONENT_ADD_FAILED/);
  assert.match(supportSource, /E_COMPONENT_INDEX_OUT_OF_RANGE/);
  assert.match(toolSource, /ChangedProperties/);
});

test("C# component deep handlers return typed Result via IUnityCliTool", async () => {
  // component.get/modify/list-types 已迁移为 IUnityCliTool,返回 typed Result(框架序列化)。
  const getSource = await readFile(new URL("../unitypkg/Editor/Tools/component/get.cs", import.meta.url), "utf8");
  assert.match(getSource, /BuildSerializerOptions\(bridgeRequest\)/, "component.get should build shared serializer options");
  assert.match(getSource, /ObjectSerializer\.Serialize\(component, options\)/, "component.get should serialize the component");
  const modifySource = await readFile(new URL("../unitypkg/Editor/Tools/component/modify.cs", import.meta.url), "utf8");
  assert.match(modifySource, /DryRun/, "component.modify should support dry-run");
  assert.match(modifySource, /ObjectPatch\.ApplyMergePatch/, "component.modify should apply merge patches");
  const listSource = await readFile(new URL("../unitypkg/Editor/Tools/component/list-types.cs", import.meta.url), "utf8");
  assert.match(listSource, /Name = "component\.list-types"/, "component.list-types should be an IUnityCliTool");
  const supportSource = await readFile(new URL("../unitypkg/Editor/Tools/go/GameObjectToolSupport.cs", import.meta.url), "utf8");
  assert.match(supportSource, /E_OBJECT_READ_SCOPE_CONFLICT[\s\S]*paths and viewQuery are mutually exclusive/, "component.get should inherit shared read-scope conflict behavior");
});
