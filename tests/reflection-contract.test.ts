import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { destructiveCommands } from "../src/security-policy.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";
import { contractToolNames, discoverLiveTools, readEditorCsSources } from "./helpers/csharp-test-helpers.js";

/** Reflection 域契约测试：descriptor 改走 live discovery。 */

test("reflection group tools exist with correct live descriptor hints", async () => {
  assert.deepEqual(contractToolNames("reflection"), ["reflection.method.call", "reflection.method.find"]);

  const tools = await discoverLiveTools(
    makeLiveTool({ name: "reflection.method.find", summary: "Find reflected methods.", description: "Find reflected methods.", group: "reflection" }),
    makeLiveTool({ name: "reflection.method.call", summary: "Invoke a reflected method.", description: "Invoke a reflected method with --dry-run or --yes confirmation.", group: "reflection", destructiveHint: true, supportsDryRun: true, permissions: ["arbitrary-method-invocation"] })
  );

  const find = tools.get("reflection.method.find")!;
  assert.equal(find.destructiveHint, false);

  const call = tools.get("reflection.method.call")!;
  assert.equal(call.destructiveHint, true);
  assert.equal(call.riskLevel, "critical");
  assert.equal(call.supportsDryRun, true);
  assert.ok(call.description.includes("--dry-run"));
  assert.ok(call.description.includes("--yes"));
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

test("reflection.method.call supports instanceRef and confirmation flow", async () => {
  const tools = await discoverLiveTools(
    makeLiveTool({
      name: "reflection.method.call",
      summary: "Invoke a reflected method.",
      description: "Invoke a reflected method with --dry-run or --yes confirmation.",
      group: "reflection",
      destructiveHint: true,
      supportsDryRun: true,
      permissions: ["arbitrary-method-invocation"],
      inputSchema: { type: "object", properties: { instanceRef: { type: "string" } } }
    })
  );
  const call = tools.get("reflection.method.call")!;
  const props = call.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.instanceRef);
  assert.equal(props.security, undefined);
});

test("reflection.method.call example is executable and overload selection is explicit", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/reflection/method/call.cs", import.meta.url), "utf8");
  const reflection = await readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliReflection.cs", import.meta.url), "utf8");
  assert.match(source, /TypeName = "UnityEngine\.Random", MethodName = "InitState", Args = new object\[\] \{ 12345 \}/);
  assert.match(source, /public List<string> ParameterTypes/);
  assert.match(source, /request\.Args, request\.ParameterTypes, request\.InstanceRef/);
  assert.match(reflection, /private static MethodInfo ResolveCallMethod/);
  assert.match(reflection, /E_METHOD_AMBIGUOUS/);
  assert.doesNotMatch(reflection, /type\.GetMethod\(methodName, bindings\)/);
  assert.doesNotMatch(source, /MethodName = "Translate"/);
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

test("destructive security policy covers reflection.method.call", () => {
  const commands = new Set(destructiveCommands().map((entry) => entry.command));
  assert.ok(commands.has("reflection.method.call"));
});

test("reflection and type commands are exposed by typed C# tools", async () => {
  const allSource = await readEditorCsSources();
  for (const command of ["reflection.method.find", "reflection.method.call", "type.schema"]) {
    assert.ok(allSource.includes(`Name = "${command}"`), `${command} should expose the public tool name through IUnityCliTool`);
  }
  assert.match(allSource, /ReflectionInvoker\.FindMethods\(/);
  assert.match(allSource, /ReflectionInvoker\.CallMethod\(/);
  assert.match(allSource, /TypeSchemaGenerator\.Generate\(/);
  assert.match(allSource, /schema\["\$defs"\] = context\.Definitions/);
  assert.match(allSource, /new Dictionary<string, object> \{ \{ "\$ref", "#\/\$defs\/" \+ key \} \}/);
  assert.match(allSource, /TryGetDictionaryTypes/);
  assert.match(allSource, /genericArguments/);
  assert.doesNotMatch(allSource, /UnityCliJsonWriter/);
});
