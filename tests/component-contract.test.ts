import assert from "node:assert/strict";
import test from "node:test";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";
import { contractToolNames, discoverLiveTools, listCsFiles, readEditorCsSources } from "./helpers/csharp-test-helpers.js";

/**
 * Component 域契约测试：descriptor 改为 live discovery，离线不再从 findToolByName/filterToolsByGroup 读取 C# 工具。
 */

test("component tools are contract-discovered and expose expected live descriptor fields", async () => {
  assert.deepEqual(contractToolNames("component"), [
    "component.add",
    "component.get",
    "component.list-types",
    "component.modify",
    "component.remove",
    "component.set"
  ]);

  const tools = await discoverLiveTools(
    makeLiveTool({
      name: "component.add",
      summary: "Add a component to a GameObject.",
      description: "Add a component to a GameObject and optionally initialize properties.",
      group: "component",
      playModeSupport: "session-affecting",
      sideEffects: ["component-add", "scene-modify"],
      outputSchema: { type: "object", properties: { changedProperties: { type: "array" } } },
      inputSchema: { type: "object", properties: { properties: { type: "object" } } },
      examples: [{ argv: ["tools", "run", "component.add", "--input", "{}"], description: "" }]
    }),
    makeLiveTool({
      name: "component.remove",
      summary: "Remove one or more components.",
      description: "Remove one or more components from a GameObject.",
      group: "component",
      playModeSupport: "session-affecting",
      sideEffects: ["component-remove", "scene-modify"],
      inputSchema: { type: "object", properties: { componentIndex: { type: "integer" }, components: { type: "array" } } }
    }),
    makeLiveTool({
      name: "component.set",
      summary: "Set component properties.",
      description: "Set one or more component properties.",
      group: "component",
      playModeSupport: "session-affecting",
      sideEffects: ["component-modify", "scene-modify"],
      outputSchema: { type: "object", properties: { changedProperties: { type: "array" } } },
      inputSchema: { type: "object", properties: { properties: { type: "object" }, componentIndex: { type: "integer" } } },
      examples: [{ argv: ["tools", "run", "component.set", "--input", "{}"], description: "" }]
    }),
    makeLiveTool({
      name: "component.get",
      summary: "Read component data.",
      description: "Read component data with exact paths or viewQuery scopes; paths and viewQuery are mutually exclusive.",
      group: "component",
      inputSchema: {
        type: "object",
        properties: {
          instanceId: { type: "integer" },
          componentInstanceId: { type: "integer" },
          componentIndex: { type: "integer" },
          component: { type: "string" },
          paths: { type: "array" },
          viewQuery: { type: "object" },
          maxArrayItems: { type: "integer" }
        }
      },
      outputSchema: { type: "object", properties: { data: { type: "object" }, truncated: { type: "boolean" }, nextPath: { type: "string" } } }
    }),
    makeLiveTool({
      name: "component.modify",
      summary: "Patch serialized component data.",
      description: "Patch serialized component data.",
      group: "component",
      destructiveHint: true,
      supportsDryRun: true,
      playModeSupport: "session-affecting",
      inputSchema: { type: "object", properties: { pathPatches: { type: "array" } } }
    }),
    makeLiveTool({
      name: "component.list-types",
      summary: "List available component types.",
      description: "List available component types.",
      group: "component",
      outputSchema: { type: "object", properties: { items: { type: "array" } } }
    })
  );

  const add = tools.get("component.add")!;
  assert.equal(add.playModeSupport, "session-affecting");
  assert.deepEqual(add.sideEffects, ["component-add", "scene-modify"]);
  assert.ok(add.outputSchema);
  assert.ok(add.examples?.some((example) => example.argv?.includes("--input")));

  const remove = tools.get("component.remove")!;
  assert.equal(remove.destructiveHint, false);
  assert.equal(remove.playModeSupport, "session-affecting");
  assert.deepEqual(remove.sideEffects, ["component-remove", "scene-modify"]);

  const set = tools.get("component.set")!;
  assert.equal(set.playModeSupport, "session-affecting");
  assert.deepEqual(set.sideEffects, ["component-modify", "scene-modify"]);
  assert.ok(set.outputSchema);
  assert.ok(set.examples?.some((example) => example.argv?.includes("--input")));

  const get = tools.get("component.get")!;
  const getProps = get.inputSchema.properties as Record<string, unknown>;
  const getOutput = get.outputSchema?.properties as Record<string, unknown>;
  assert.equal(get.playModeSupport, "supported");
  assert.ok(getProps.instanceId);
  assert.ok(getProps.componentInstanceId);
  assert.ok(getProps.componentIndex);
  assert.ok(getProps.component);
  assert.ok(getProps.paths);
  assert.ok(getProps.viewQuery);
  assert.ok(getProps.maxArrayItems);
  assert.ok(getOutput.data);
  assert.ok(getOutput.truncated);
  assert.ok(getOutput.nextPath);
  assert.match(get.description, /mutually exclusive/);

  const modify = tools.get("component.modify")!;
  const modifyProps = modify.inputSchema.properties as Record<string, unknown>;
  assert.equal(modify.destructiveHint, true);
  assert.equal(modify.supportsDryRun, true);
  assert.equal(modify.playModeSupport, "session-affecting");
  assert.ok(modifyProps.pathPatches);
  assert.equal(modifyProps.jsonPatch, undefined);
  assert.equal(modifyProps.componentDiff, undefined);

  const listTypes = tools.get("component.list-types")!;
  assert.equal(listTypes.playModeSupport, "supported");
  assert.ok(listTypes.outputSchema);
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

test("component.modify exposes destructive metadata because Edit Mode can target persistent Prefab components", () => {
  const command = bridgeCommandRegistry.lookup("component", "modify");
  assert.equal(command?.destructiveHint, true);
});

test("component.modify descriptor example uses a verifiable SerializedProperty path", async () => {
  const source = await (await import("node:fs/promises")).readFile(new URL("../unitypkg/Editor/Tools/component/modify.cs", import.meta.url), "utf8");
  assert.match(source, /\{ "path", "m_LocalPosition\.x" \}/);
  assert.doesNotMatch(source, /\{ "path", "localPosition\/x" \}/);
});

test("C# handlers exist for component deep commands (RegisterCommand or IUnityCliTool)", async () => {
  const allSource = await readEditorCsSources();
  for (const command of ["component.get", "component.modify", "component.list-types"]) {
    const hasRegisterCommand = allSource.includes(`RegisterCommand(registry, "${command}"`);
    const hasIUnityCliTool = allSource.includes(`Name = "${command}"`);
    assert.ok(hasRegisterCommand || hasIUnityCliTool, `${command} should be registered via RegisterCommand or IUnityCliTool`);
  }
});

test("C# component lifecycle handlers support properties and indexed duplicate components", async () => {
  const toolSource = await (await import("node:fs/promises")).readFile(new URL("../unitypkg/Editor/Tools/component/add.cs", import.meta.url), "utf8");
  const supportSource = await (await import("node:fs/promises")).readFile(new URL("../unitypkg/Editor/Tools/go/GameObjectToolSupport.cs", import.meta.url), "utf8");
  const setSource = await (await import("node:fs/promises")).readFile(new URL("../unitypkg/Editor/Tools/component/set.cs", import.meta.url), "utf8");
  assert.match(toolSource, /\[UnityCliAnyJson\]\s+public Dictionary<string, object> Properties/);
  assert.match(setSource, /\[UnityCliAnyJson\]\s+public object Value/);
  assert.match(supportSource, /BuildComponentPropertyChanges\(Type componentType, Dictionary<string, object> properties\)/);
  assert.match(supportSource, /UnityCliValueConverter\.ConvertValue\(value, type\)/);
  assert.doesNotMatch(supportSource, /JsonRawProperty|ComponentPropertiesJson|ParseJsonToObject/);
  assert.match(setSource, /ComponentIndex/);
  assert.match(supportSource, /ResolveComponent\(GameObject gameObject, string componentName, int\? componentIndex\)/);
  assert.match(supportSource, /AllowsMultipleComponents/);
  assert.match(supportSource, /GetParentTypeDisallowingMultipleInclusion/);
  assert.match(supportSource, /BindingFlags\.Static \| BindingFlags\.Public \| BindingFlags\.NonPublic/);
  assert.match(supportSource, /NativeSingleInstanceComponentTypes[\s\S]*typeof\(Rigidbody\)/);
  assert.ok(supportSource.indexOf("NativeSingleInstanceComponentTypes.Contains(type)") < supportSource.indexOf("DisallowMultipleTypeMethod.Invoke"));
  assert.match(supportSource, /DisallowMultipleComponent/);
  const executeBody = toolSource.slice(toolSource.indexOf("public System.Threading.Tasks.ValueTask<Result> ExecuteAsync"));
  assert.ok(executeBody.indexOf("E_COMPONENT_EXISTS") < executeBody.indexOf("ctx.CommitEffect()"));
  assert.match(toolSource, /Add AudioSource with initial properties/);
  assert.match(toolSource, /Component = "AudioSource"/);
  assert.match(toolSource, /type\.IsInstanceOfType\(component\)/);
  assert.match(toolSource, /E_COMPONENT_ADD_FAILED/);
  assert.match(toolSource, /GetComponents\(type\)/);
  assert.match(toolSource, /candidate\.GetInstanceID\(\) == componentId/);
  assert.doesNotMatch(toolSource.slice(toolSource.indexOf("VerifyComponentReadback")), /GetComponent\(type\)/);
  assert.match(supportSource, /E_COMPONENT_INDEX_OUT_OF_RANGE/);
  assert.match(toolSource, /ChangedProperties/);
});

test("component.remove validates RequireComponent dependencies before its effect barrier", async () => {
  const source = await (await import("node:fs/promises")).readFile(new URL("../unitypkg/Editor/Tools/component/remove.cs", import.meta.url), "utf8");
  assert.match(source, /EnsureDependenciesRemain\(gameObject, targets\)[\s\S]*ctx\.CommitEffect\(\)/);
  assert.match(source, /GetCustomAttributes\(typeof\(RequireComponent\), true\)/);
  assert.match(source, /E_COMPONENT_REMOVE_DEPENDENCY/);
  assert.match(source, /targetIds\.Any\(id => EditorUtility\.InstanceIDToObject\(id\) != null\)/);
  assert.doesNotMatch(source, /observedSingleTarget\.GetComponent\(componentName\) != null/);
});

test("C# component deep handlers return typed Result via IUnityCliTool", async () => {
  const { readFile } = await import("node:fs/promises");
  const getSource = await readFile(new URL("../unitypkg/Editor/Tools/component/get.cs", import.meta.url), "utf8");
  assert.match(getSource, /BuildSerializerOptions\(request\)/, "component.get should build shared serializer options from its typed request DTO");
  assert.match(getSource, /ObjectSerializer\.Serialize\(component, options, budget\)/, "component.get should serialize the component with the request-wide budget");
  const modifySource = await readFile(new URL("../unitypkg/Editor/Tools/component/modify.cs", import.meta.url), "utf8");
  assert.match(modifySource, /DryRun/, "component.modify should support dry-run");
  assert.match(modifySource, /ObjectPatch\.ApplyVerifiedSerializedPatches/, "component.modify should apply prevalidated serialized path patches");
  const listSource = await readFile(new URL("../unitypkg/Editor/Tools/component/list-types.cs", import.meta.url), "utf8");
  assert.match(listSource, /Name = "component\.list-types"/, "component.list-types should be an IUnityCliTool");
  const supportSource = await readFile(new URL("../unitypkg/Editor/Tools/go/GameObjectToolSupport.cs", import.meta.url), "utf8");
  assert.match(supportSource, /E_OBJECT_READ_SCOPE_CONFLICT[\s\S]*paths and viewQuery are mutually exclusive/, "component.get should inherit shared read-scope conflict behavior");
});
