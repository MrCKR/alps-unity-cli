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
  assert.equal(componentGet?.bridgeCommand, "component.get");
  const componentModify = bridgeCommandRegistry.lookup("component", "modify");
  assert.equal(componentModify?.bridgeCommand, "component.modify");
  const componentListTypes = bridgeCommandRegistry.lookup("component", "list-types");
  assert.equal(componentListTypes?.bridgeCommand, "component.list-types");
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

test("C# RegisterCommand calls exist for component deep commands", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  for (const command of ["component.get", "component.modify", "component.list-types"]) {
    assert.ok(
      allSource.includes(`RegisterCommand(registry, "${command}"`),
      `${command} should be registered via RegisterCommand in C#`
    );
  }
});

test("C# component lifecycle handlers support properties and indexed duplicate components", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/GameObjectCommands.cs", import.meta.url), "utf8");
  assert.match(source, /ComponentPropertiesJson\(request\)/);
  assert.match(source, /BuildComponentPropertyChanges\(type, ComponentPropertiesJson\(request\)\)/);
  assert.match(source, /request\.OptionalIntArg\("componentIndex"\)/);
  assert.match(source, /ResolveComponent\(gameObject, componentName, componentIndex\)/);
  assert.match(source, /AllowsMultipleComponents\(type\)/);
  assert.match(source, /DisallowMultipleComponent/);
  assert.match(source, /type\.IsInstanceOfType\(component\)/);
  assert.match(source, /E_COMPONENT_ADD_FAILED/);
  assert.match(source, /E_COMPONENT_INDEX_OUT_OF_RANGE/);
  assert.match(source, /changedProperties/);
});

test("C# component deep handlers return bridge success envelopes", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/GameObjectCommands.cs", import.meta.url), "utf8");
  assert.match(source, /ComponentGet[\s\S]*BuildSerializerOptions\(request\)[\s\S]*ObjectSerializer\.Serialize\(component, options\)[\s\S]*return SuccessJson\(w\.EndObject\(\)\.ToString\(\)\);/, "component.get should support shared paths/viewQuery serializer options and bridge success envelope");
  assert.match(source, /ComponentModify[\s\S]*return SuccessJson\(dry\.EndObject\(\)\.ToString\(\)\);[\s\S]*return SuccessJson\(w\.EndObject\(\)\.ToString\(\)\);/, "component.modify dry-run and execution should return bridge success envelopes");
  assert.match(source, /ComponentListTypes[\s\S]*return SuccessJson\(w\.EndObject\(\)\.ToString\(\)\);/, "component.list-types should return a bridge success envelope");
  const objectSource = await readFile(new URL("../unitypkg/Editor/Commands/ObjectCommands.cs", import.meta.url), "utf8");
  assert.match(objectSource, /E_OBJECT_READ_SCOPE_CONFLICT[\s\S]*paths and viewQuery are mutually exclusive/, "component.get should inherit shared read-scope conflict behavior");
});
