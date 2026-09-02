import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";
import { contractToolNames, discoverLiveTools, readEditorCsSources } from "./helpers/csharp-test-helpers.js";

/** Reflection 域契约测试：descriptor 改走 live discovery。 */

test("reflection group tools exist with correct live descriptor hints", async () => {
  assert.deepEqual(contractToolNames("reflection"), ["reflection.method.call", "reflection.method.find", "type.schema"]);

  const tools = await discoverLiveTools(
    makeLiveTool({ name: "reflection.method.find", summary: "Find reflected methods.", description: "Find reflected methods.", group: "reflection" }),
    makeLiveTool({ name: "reflection.method.call", summary: "Invoke a reflected method.", description: "Invoke a reflected method; --dry-run previews supported calls.", group: "reflection", destructiveHint: true, supportsDryRun: true, permissions: ["arbitrary-method-invocation"] })
  );

  const find = tools.get("reflection.method.find")!;
  assert.equal(find.destructiveHint, false);

  const call = tools.get("reflection.method.call")!;
  assert.equal(call.destructiveHint, true);
  assert.equal(call.riskLevel, "critical");
  assert.equal(call.supportsDryRun, true);
  assert.ok(call.description.includes("--dry-run"));
  assert.doesNotMatch(call.description, /securityProof/i);
});

test("type.schema tool exists as live-discovered read-only bridge descriptor", async () => {
  const tools = await discoverLiveTools(
    makeLiveTool({
      name: "type.schema",
      summary: "Generate a JSON schema for a C# type.",
      description: "Generate a JSON schema including enum, generic, and $defs references.",
      group: "type",
      inputSchema: { type: "object", properties: {} },
      outputSchema: { type: "object", properties: { definitionCount: { type: "integer" } } }
    })
  );
  const schema = tools.get("type.schema")!;
  assert.equal(schema.group, "type");
  assert.equal(schema.destructiveHint, false);
  assert.equal(schema.handler, "bridge");
  assert.match(schema.description, /enum/);
  assert.match(schema.description, /generic/);
  assert.match(schema.description, /\$defs/);
  assert.match(JSON.stringify(schema.outputSchema), /definitionCount/);
});

test("reflection.method.call supports instanceRef without confirmation state", async () => {
  const tools = await discoverLiveTools(
    makeLiveTool({
      name: "reflection.method.call",
      summary: "Invoke a reflected method.",
      description: "Invoke a reflected method; --dry-run previews supported calls.",
      group: "reflection",
      destructiveHint: true,
      supportsDryRun: true,
      permissions: ["arbitrary-method-invocation"],
      inputSchema: { type: "object", required: ["typeName", "methodName", "completionProbe"], properties: { instanceRef: { type: "string" }, parameterTypes: { type: "array" }, completionProbe: { type: "object" } } }
    })
  );
  const call = tools.get("reflection.method.call")!;
  const props = call.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.instanceRef);
  assert.ok(props.parameterTypes);
  assert.ok(props.completionProbe);
  assert.deepEqual(call.inputSchema.required, ["typeName", "methodName", "completionProbe"]);
  assert.equal(props.security, undefined);
});

test("reflection.method.call example is executable and overload selection is explicit", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/reflection/method/call.cs", import.meta.url), "utf8");
  const reflection = await readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliReflection.cs", import.meta.url), "utf8");
  assert.match(source, /TypeName = "UnityEngine\.Mathf", MethodName = "Abs"/);
  assert.match(source, /ParameterTypes = new List<string> \{ "System\.Single" \}/);
  assert.match(source, /CompletionProbe = new UnityCliCompletionProbe/);
  assert.match(source, /Execution = new ToolExampleExecution \{ DryRun = true \}/);
  assert.match(source, /public List<string> ParameterTypes/);
  assert.match(source, /request\.Args, request\.ParameterTypes, request\.InstanceRef/);
  assert.match(reflection, /private static MethodInfo ResolveCallMethod/);
  assert.match(reflection, /parameterTypeNames != null/);
  assert.match(reflection, /SequenceEqual\(parameterTypeNames, StringComparer\.Ordinal\)/);
  assert.match(reflection, /argumentCount[\s\S]*GetParameters\(\)\.Length == argumentCount/);
  assert.match(reflection, /E_METHOD_ARGUMENT_REQUIRED/);
  assert.match(reflection, /E_METHOD_ARGUMENTS_INVALID/);
  assert.match(reflection, /E_METHOD_AMBIGUOUS/);
  assert.match(reflection, /RequireSynchronousResult/);
  assert.match(reflection, /E_METHOD_COMPLETION_UNPROVABLE/);
  assert.match(reflection, /E_METHOD_INVOCATION_FAILED/);
  assert.doesNotMatch(reflection, /audit\["ok"\] = false/);
  assert.doesNotMatch(reflection, /type\.GetMethod\(methodName, bindings\)/);
  const bridge = await readFile(new URL("../src/bridge/domains/reflection.js", import.meta.url), "utf8");
  assert.match(bridge, /parameterTypes: structured\.parameterTypes/);
  assert.match(bridge, /completionProbe: structured\.completionProbe/);
  assert.doesNotMatch(bridge, /security: structured\.security/);
  assert.doesNotMatch(source, /MethodName = "Translate"/);
  assert.doesNotMatch(source, /public (?:string Command|bool Confirmed|bool RequiresConfirmation|string RiskLevel|bool DestructiveHint)/);
  assert.doesNotMatch(reflection, /\{ "command", "reflection\.method\.call" \}/);
});

test("reflection probe bake requires Unity output and a readable cubemap artifact", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/graphics/bake-reflection-probe.cs", import.meta.url), "utf8");
  assert.match(source, /E_GRAPHICS_ASSET_DIRECTORY_NOT_FOUND/);
  assert.ok(source.indexOf("E_GRAPHICS_ASSET_DIRECTORY_NOT_FOUND") < source.indexOf("ctx.CommitEffect()"));
  assert.match(source, /E_REFLECTION_PROBE_BAKE_FAILED/);
  assert.match(source, /E_REFLECTION_PROBE_BAKE_ARTIFACT_MISSING/);
  assert.match(source, /AssetDatabase\.ImportAsset\(probePath, ImportAssetOptions\.ForceSynchronousImport\)/);
  assert.match(source, /AssetDatabase\.LoadAssetAtPath<Cubemap>\(probePath\)/);
  assert.match(source, /File\.ReadAllBytes\(outputPath\)/);
  assert.match(source, /E_REFLECTION_PROBE_BAKE_ARTIFACT_INVALID/);
  assert.match(source, /info\.Length < 4/);
  assert.match(source, /var bytes = File\.ReadAllBytes\(outputPath\)/);
});

test("bridge specs resolve for reflection and type tools", () => {
  const methodFind = bridgeCommandRegistry.lookup("reflection", "method-find");
  assert.equal(methodFind?.bridgeCommand, "project-tool.run");
  const methodCall = bridgeCommandRegistry.lookup("reflection", "method-call");
  assert.equal(methodCall?.bridgeCommand, "project-tool.run");
  const typeSchema = bridgeCommandRegistry.lookup("type", "schema");
  assert.equal(typeSchema?.bridgeCommand, "project-tool.run");
});

test("contract entries exist for reflection and type tools with present C# handler", () => {
  for (const name of ["reflection.method.find", "reflection.method.call", "type.schema"]) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in command contract`);
    assert.equal(contract?.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("reflection.method.call exposes destructive metadata", () => {
  const command = bridgeCommandRegistry.lookup("reflection", "method-call");
  assert.equal(command?.destructiveHint, true);
});

test("reflection and type commands are exposed by typed C# tools", async () => {
  const allSource = await readEditorCsSources();
  for (const command of ["reflection.method.find", "reflection.method.call", "type.schema"]) {
    assert.ok(allSource.includes(`Name = "${command}"`), `${command} should expose the public tool name through IUnityCliTool`);
  }
  assert.match(allSource, /ReflectionInvoker\.FindMethods\(/);
  assert.match(allSource, /ReflectionInvoker\.PrepareCall\(/);
  assert.match(allSource, /ReflectionInvoker\.InvokePrepared\(/);
  assert.doesNotMatch(allSource, /ReflectionInvoker\.CallMethod\(/);
  assert.match(allSource, /TypeSchemaGenerator\.Generate\(/);
  assert.match(allSource, /schema\["\$defs"\] = context\.Definitions/);
  assert.match(allSource, /new Dictionary<string, object> \{ \{ "\$ref", "#\/\$defs\/" \+ key \} \}/);
  assert.match(allSource, /TryGetDictionaryTypes/);
  assert.match(allSource, /genericArguments/);
  assert.doesNotMatch(allSource, /UnityCliJsonWriter/);
});
