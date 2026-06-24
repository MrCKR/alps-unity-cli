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

test("component deep tools exist alongside simple set", () => {
  const componentTools = filterToolsByGroup("component");
  const names = componentTools.map((t) => t.name).sort();
  assert.ok(names.includes("component.get"));
  assert.ok(names.includes("component.modify"));
  assert.ok(names.includes("component.list-types"));
  assert.ok(names.includes("component.set"));

  const get = findToolByName("component.get")!;
  assert.equal(get.equivalentUnityMcp, "gameobject-component-get");

  const modify = findToolByName("component.modify")!;
  assert.equal(modify.destructiveHint, true);
  assert.equal(modify.supportsDryRun, true);
  assert.equal(modify.equivalentUnityMcp, "gameobject-component-modify");

  const listTypes = findToolByName("component.list-types")!;
  assert.equal(listTypes.equivalentUnityMcp, "gameobject-component-list-all");
});

test("component.modify input schema supports pathPatches, jsonPatch and componentDiff", () => {
  const modify = findToolByName("component.modify")!;
  const props = modify.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.pathPatches, "pathPatches should be in schema");
  assert.ok(props.jsonPatch, "jsonPatch should be in schema");
  assert.ok(props.componentDiff, "componentDiff should be in schema");
});

test("component.get supports ObjectRef resolution fields (instanceId, componentInstanceId, componentIndex, component)", () => {
  const get = findToolByName("component.get")!;
  const props = get.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.instanceId, "instanceId should be supported");
  assert.ok(props.componentInstanceId, "componentInstanceId should be supported");
  assert.ok(props.componentIndex, "componentIndex should be supported");
  assert.ok(props.component, "component type name should be supported");
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

test("destructive security policy covers component.modify", () => {
  const commands = new Set(destructiveCommands().map((e) => e.command));
  assert.ok(commands.has("component.modify"));
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
