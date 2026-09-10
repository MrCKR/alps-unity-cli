import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";
import { contractToolNames, discoverLiveTools, readEditorCsSources } from "./helpers/csharp-test-helpers.js";

/** Reflection 域契约测试：descriptor 改走 live discovery。 */

test("reflection group tools exist with correct live descriptor hints", async () => {
  assert.deepEqual(contractToolNames("runtime-reflection"), ["runtime-reflection.method.find", "runtime-reflection.method.invoke", "runtime-reflection.type.schema"]);

  const tools = await discoverLiveTools(
    makeLiveTool({ name: "runtime-reflection.method.find", summary: "Find reflected methods.", description: "Find reflected methods.", group: "runtime-reflection" }),
    makeLiveTool({ name: "runtime-reflection.method.invoke", summary: "Invoke a reflected method.", description: "Invoke a reflected method; --dry-run previews supported calls.", group: "runtime-reflection", destructiveHint: true, supportsDryRun: true, permissions: ["arbitrary-method-invocation"] })
  );

  const find = tools.get("runtime-reflection.method.find")!;
  assert.equal(find.destructiveHint, false);

  const call = tools.get("runtime-reflection.method.invoke")!;
  assert.equal(call.destructiveHint, true);
  assert.equal(call.riskLevel, "critical");
  assert.equal(call.supportsDryRun, true);
  assert.ok(call.description.includes("--dry-run"));
  assert.doesNotMatch(call.description, /securityProof/i);
});

test("runtime-reflection.type.schema tool exists as live-discovered read-only bridge descriptor", async () => {
  const tools = await discoverLiveTools(
    makeLiveTool({
      name: "runtime-reflection.type.schema",
      summary: "Generate a JSON schema for a C# type.",
      description: "Generate a JSON schema including enum, generic, and $defs references.",
      group: "runtime-reflection",
      inputSchema: { type: "object", properties: {} },
      outputSchema: { type: "object", properties: { definitionCount: { type: "integer" } } }
    })
  );
  const schema = tools.get("runtime-reflection.type.schema")!;
  assert.equal(schema.group, "runtime-reflection");
  assert.equal(schema.destructiveHint, false);
  assert.equal(schema.handler, "bridge");
  assert.match(schema.description, /enum/);
  assert.match(schema.description, /generic/);
  assert.match(schema.description, /\$defs/);
  assert.match(JSON.stringify(schema.outputSchema), /definitionCount/);
});

test("runtime-reflection.method.invoke supports instanceRef without confirmation state", async () => {
  const tools = await discoverLiveTools(
    makeLiveTool({
      name: "runtime-reflection.method.invoke",
      summary: "Invoke a reflected method.",
      description: "Invoke a reflected method; --dry-run previews supported calls.",
      group: "runtime-reflection",
      destructiveHint: true,
      supportsDryRun: true,
      permissions: ["arbitrary-method-invocation"],
      inputSchema: { type: "object", properties: { instanceRef: { type: "string" } } }
    })
  );
  const call = tools.get("runtime-reflection.method.invoke")!;
  const props = call.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.instanceRef);
  assert.equal(props.security, undefined);
});

test("runtime-reflection.method.invoke example is executable and overload selection is explicit", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/runtime-reflection/method/invoke.cs", import.meta.url), "utf8");
  const reflection = await readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliReflection.cs", import.meta.url), "utf8");
  assert.match(source, /TypeName = "UnityEngine\.Mathf", MethodName = "Abs"/);
  assert.match(source, /ParameterTypes = new List<string> \{ "System\.Single" \}/);
  assert.match(source, /CompletionProbe = new UnityCliCompletionProbe/);
  assert.match(source, /Execution = new ToolExampleExecution \{ DryRun = true \}/);
  assert.match(source, /public List<string> ParameterTypes/);
  assert.match(source, /request\.Args, request\.ParameterTypes, request\.InstanceRef/);
  assert.match(reflection, /private static CallCandidate ResolveCallMethod/);
  assert.match(reflection, /E_METHOD_AMBIGUOUS/);
  assert.match(reflection, /RequireSynchronousResult/);
  assert.match(reflection, /E_METHOD_COMPLETION_UNPROVABLE/);
  assert.match(reflection, /E_METHOD_INVOCATION_FAILED/);
  assert.doesNotMatch(reflection, /audit\["ok"\] = false/);
  assert.doesNotMatch(reflection, /type\.GetMethod\(methodName, bindings\)/);
  const bridge = await readFile(new URL("../src/bridge/domains/runtime-reflection.js", import.meta.url), "utf8");
  assert.match(bridge, /parameterTypes: structured\.parameterTypes/);
  assert.match(bridge, /completionProbe: structured\.completionProbe/);
  assert.doesNotMatch(bridge, /security: structured\.security/);
  assert.doesNotMatch(source, /MethodName = "Translate"/);
  assert.doesNotMatch(source, /public (?:string Command|bool Confirmed|bool RequiresConfirmation|string RiskLevel|bool DestructiveHint)/);
  assert.doesNotMatch(reflection, /\{ "command", "reflection\.method\.call" \}/);
});

test("reflection probe bake requires Unity output and a readable cubemap artifact", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/lighting/bake/reflection-probe.cs", import.meta.url), "utf8");
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
  const methodFind = bridgeCommandRegistry.lookup("runtime-reflection", "method.find");
  assert.equal(methodFind?.bridgeCommand, "catalog.editor.run");
  const methodCall = bridgeCommandRegistry.lookup("runtime-reflection", "method.invoke");
  assert.equal(methodCall?.bridgeCommand, "catalog.editor.run");
  const typeSchema = bridgeCommandRegistry.lookup("runtime-reflection", "type.schema");
  assert.equal(typeSchema?.bridgeCommand, "catalog.editor.run");
});

test("contract entries exist for reflection and type tools with present C# handler", () => {
  for (const name of ["runtime-reflection.method.find", "runtime-reflection.method.invoke", "runtime-reflection.type.schema"]) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in command contract`);
    assert.equal(contract?.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("runtime-reflection.method.invoke exposes destructive metadata", () => {
  const command = bridgeCommandRegistry.lookup("runtime-reflection", "method.invoke");
  assert.equal(command?.destructiveHint, true);
});

test("reflection and type commands are exposed by typed C# tools", async () => {
  const allSource = await readEditorCsSources();
  for (const command of ["runtime-reflection.method.find", "runtime-reflection.method.invoke", "runtime-reflection.type.schema"]) {
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
