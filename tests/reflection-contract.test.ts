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
  assert.equal(find.readOnlyHint, true);
  assert.equal(find.destructiveHint, false);
  assert.equal(find.equivalentUnityMcp, "reflection-method-find");

  const call = findToolByName("reflection.method.call")!;
  assert.equal(call.readOnlyHint, false);
  assert.equal(call.destructiveHint, true);
  assert.equal(call.riskLevel, "critical");
  assert.equal(call.equivalentUnityMcp, "reflection-method-call");
});

test("type.schema tool exists as read-only bridge", () => {
  const schema = findToolByName("type.schema")!;
  assert.equal(schema.group, "type");
  assert.equal(schema.readOnlyHint, true);
  assert.equal(schema.destructiveHint, false);
  assert.equal(schema.handler, "bridge");
  assert.equal(schema.equivalentUnityMcp, "type-get-json-schema");
});

test("reflection.method.call supports instanceRef and security confirmation", () => {
  const call = findToolByName("reflection.method.call")!;
  const props = call.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.instanceRef, "instanceRef should be supported for instance methods");
  assert.ok(props.security, "security confirmation should be required");
});

test("bridge specs resolve for reflection and type tools", () => {
  const methodFind = bridgeCommandRegistry.lookup("reflection", "method-find");
  assert.equal(methodFind?.bridgeCommand, "reflection.method.find");
  const methodCall = bridgeCommandRegistry.lookup("reflection", "method-call");
  assert.equal(methodCall?.bridgeCommand, "reflection.method.call");
  const typeSchema = bridgeCommandRegistry.lookup("type", "schema");
  assert.equal(typeSchema?.bridgeCommand, "type.schema");
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

test("C# RegisterCommand calls exist for reflection and type commands", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  for (const command of ["reflection.method.find", "reflection.method.call", "type.schema"]) {
    assert.ok(
      allSource.includes(`RegisterCommand(registry, "${command}"`),
      `${command} should be registered via RegisterCommand in C#`
    );
  }
});
