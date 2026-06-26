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
 * Object 域契约测试：object.get-data / object.modify。
 * 对齐 IvanMurzak object-get-data/object-modify，验证 metadata/bridge/contract/C# 四层一致。
 */

test("object group tools exist with correct safety hints", () => {
  const objectTools = filterToolsByGroup("object");
  const names = objectTools.map((t) => t.name).sort();
  assert.deepEqual(names, ["object.get-data", "object.modify"]);

  const getData = findToolByName("object.get-data")!;
  assert.equal(getData.destructiveHint, false);
  assert.equal(getData.handler, "bridge");
  assert.equal(getData.playModeSupport, "supported");
  assert.equal(getData.equivalentUnityMcp, "object-get-data");

  const modify = findToolByName("object.modify")!;
  assert.equal(modify.destructiveHint, true);
  assert.equal(modify.supportsDryRun, true);
  assert.equal(modify.playModeSupport, "runtime-only");
  assert.equal(modify.riskLevel, "high");
  assert.equal(modify.equivalentUnityMcp, "object-modify");
});

test("object.modify input schema supports pathPatches and jsonPatch", () => {
  const modify = findToolByName("object.modify")!;
  const props = modify.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.pathPatches, "pathPatches should be in schema");
  assert.ok(props.jsonPatch, "jsonPatch should be in schema");
});

test("object.get-data supports ObjectRef resolution and token-safe read scopes", () => {
  const getData = findToolByName("object.get-data")!;
  const props = getData.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.instanceId, "instanceId should be supported");
  assert.ok(props.assetPath, "assetPath should be supported");
  assert.ok(props.guid, "guid should be supported");
  assert.ok(props.path, "path should be supported");
  const output = getData.outputSchema?.properties as Record<string, unknown>;
  assert.ok(props.paths, "paths should be supported for exact reads");
  assert.ok(props.viewQuery, "viewQuery should be supported for scoped reads");
  assert.ok(output.fields, "output schema should expose SerializedMember fields");
  assert.ok(output.path, "output schema should expose SerializedMember path");
  assert.equal(output.data, undefined, "output schema should not claim an extra data wrapper");
  assert.match(getData.description, /mutually exclusive/, "describe text should warn paths/viewQuery are mutually exclusive");
});

test("bridge specs resolve for object tools", () => {
  const objectGet = bridgeCommandRegistry.lookup("object", "get-data");
  assert.equal(objectGet?.bridgeCommand, "object.get-data");
  const objectModify = bridgeCommandRegistry.lookup("object", "modify");
  assert.equal(objectModify?.bridgeCommand, "object.modify");
});

test("contract entries exist for object tools with present C# handler", () => {
  for (const name of ["object.get-data", "object.modify"]) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in command contract`);
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("destructive security policy covers object.modify", () => {
  const commands = new Set(destructiveCommands().map((e) => e.command));
  assert.ok(commands.has("object.modify"));
});

test("C# RegisterCommand calls exist for object commands", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  for (const command of ["object.get-data", "object.modify"]) {
    assert.ok(
      allSource.includes(`RegisterCommand(registry, "${command}"`),
      `${command} should be registered via RegisterCommand in C#`
    );
  }
});

test("C# infrastructure files exist for ObjectRef, serializer and patch", async () => {
  const infraFiles = await listCsFiles(new URL("../unitypkg/Editor/Infrastructure/", import.meta.url));
  const names = infraFiles.map((f) => f.pathname.split("/").pop());
  assert.ok(names.includes("UnityCliObjectRef.cs"));
  assert.ok(names.includes("UnityCliObjectSerializer.cs"));
  assert.ok(names.includes("UnityCliObjectPatch.cs"));
});

test("object.modify C# wraps dry-run and execute results in SuccessJson", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/ObjectCommands.cs", import.meta.url), "utf8");
  const modifyBlock = source.slice(source.indexOf("ObjectModify(BridgeRequest request)"), source.indexOf("private static ObjectSerializer.Options BuildSerializerOptions"));
  assert.match(modifyBlock, /return SuccessJson\(dry\.EndObject/, "dry-run branch must wrap payload in SuccessJson");
  assert.match(modifyBlock, /PropRaw..audit[\s\S]*return SuccessJson\(w\.EndObject/, "execute branch must wrap payload in SuccessJson");
});

test("C# object serializer implements path aggregate and viewQuery conflict", async () => {
  const serializer = await readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliObjectSerializer.cs", import.meta.url), "utf8");
  const commands = await readFile(new URL("../unitypkg/Editor/Commands/ObjectCommands.cs", import.meta.url), "utf8");
  assert.match(serializer, /BuildPathReadAggregate[\s\S]*UnityCli\.PathReadAggregate/, "paths should return a stable aggregate envelope");
  assert.match(serializer, /TryReadPath[\s\S]*ReadPathSegment[\s\S]*ReadIndexer/, "paths should read exact nested members and indexers");
  assert.match(commands, /E_OBJECT_READ_SCOPE_CONFLICT[\s\S]*paths and viewQuery are mutually exclusive/, "bridge should reject paths and viewQuery together");
});

test("no duplicate main entry: go.inspect stays lightweight, object.get-data is the deep reader", () => {
  const inspect = findToolByName("go.inspect")!;
  const getData = findToolByName("object.get-data")!;
  assert.notEqual(inspect.name, getData.name);
  assert.ok(getData.description.toLowerCase().includes("deep"));
});
