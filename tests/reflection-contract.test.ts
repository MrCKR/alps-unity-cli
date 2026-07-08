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
 * Reflection 域契约测试：reflection.method.find / reflection.method.call / type.schema。
 * 对齐 IvanMurzak reflection-method-find/call 与 type-get-json-schema。
 * reflection.method.call 不做调用范围限制，标 critical 风险，要求 security 确认。
 */

test("reflection group tools exist with correct safety hints", () => {
  const reflectionTools = filterToolsByGroup("reflection");
  const names = reflectionTools.map((t) => t.name).sort();
  assert.deepEqual(names, ["reflection.method.call", "reflection.method.find"]);

  const find = findToolByName("reflection.method.find")!;
  assert.equal(find.destructiveHint, false);

  const call = findToolByName("reflection.method.call")!;
  assert.equal(call.destructiveHint, true);
  assert.equal(call.riskLevel, "critical");
});

test("type.schema tool exists as read-only bridge", () => {
  const schema = findToolByName("type.schema")!;
  assert.equal(schema.group, "type");
  assert.equal(schema.destructiveHint, false);
  assert.equal(schema.handler, "bridge");
  assert.match(schema.description, /enum/, "type.schema should advertise enum schema support");
  assert.match(schema.description, /generic/, "type.schema should advertise generic schema support");
  assert.match(schema.description, /\$defs/, "type.schema should advertise $defs references");
  assert.match(JSON.stringify(schema.outputSchema), /definitionCount/, "type.schema output should include definitionCount");
});

test("reflection.method.call supports instanceRef and dry-run confirmation flow", () => {
  const call = findToolByName("reflection.method.call")!;
  const props = call.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.instanceRef, "instanceRef should be supported for instance methods");
  assert.equal(props.security, undefined, "CLI should provide destructive security proof instead of user-authored security JSON");
  assert.equal(call.supportsDryRun, true);
  assert.ok(call.description.includes("--dry-run"));
  assert.ok(call.description.includes("--yes"));
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
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("destructive security policy covers reflection.method.call", () => {
  const commands = new Set(destructiveCommands().map((e) => e.command));
  assert.ok(commands.has("reflection.method.call"));
});

test("C# RegisterCommand calls removed for reflection and type commands (migrated to IUnityCliTool)", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  for (const command of ["reflection.method.find", "reflection.method.call", "type.schema"]) {
    assert.ok(
      !allSource.includes(`RegisterCommand(registry, "${command}"`),
      `${command} should NOT be registered (migrated to IUnityCliTool)`
    );
    assert.ok(allSource.includes(`Name = "${command}"`), `${command} should expose the public tool name through IUnityCliTool`);
  }
  assert.match(allSource, /ReflectionInvoker\.FindMethods\(/, "reflection.method.find should call ReflectionInvoker.FindMethods");
  assert.match(allSource, /ReflectionInvoker\.CallMethod\(/, "reflection.method.call should call ReflectionInvoker.CallMethod");
  assert.match(allSource, /TypeSchemaGenerator\.Generate\(/, "type.schema should call TypeSchemaGenerator.Generate");
  assert.match(allSource, /PropObject\("\$defs"/, "type.schema should emit $defs for nested complex types");
  assert.match(allSource, /Prop\("\$ref", "#\/\$defs\//, "type.schema should reference nested complex types via $ref");
  assert.match(allSource, /TryGetDictionaryTypes/, "type.schema should handle dictionary-shaped generic types");
  assert.match(allSource, /genericArguments/, "type.schema should expose generic type arguments");
});
