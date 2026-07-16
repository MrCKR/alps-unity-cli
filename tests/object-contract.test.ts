import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { destructiveCommands } from "../src/security-policy.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";
import { contractToolNames, discoverLiveTools, listCsFiles, readEditorCsSources } from "./helpers/csharp-test-helpers.js";

/** Object 域契约测试：离线 metadata 断言迁移到 live discovery + 源码/契约断言。 */

test("object tools are contract-discovered and expose expected live descriptor fields", async () => {
  assert.deepEqual(contractToolNames("object"), ["object.get-data", "object.modify"]);

  const tools = await discoverLiveTools(
    makeLiveTool({
      name: "object.get-data",
      summary: "Read serialized object data.",
      description: "Read deep serialized object data; paths and viewQuery are mutually exclusive.",
      group: "object",
      inputSchema: {
        type: "object",
        properties: {
          instanceId: { type: "integer" },
          assetPath: { type: "string" },
          guid: { type: "string" },
          path: { type: "string" },
          paths: { type: "array" },
          viewQuery: { type: "object" },
          maxArrayItems: { type: "integer" }
        }
      },
      outputSchema: { type: "object", properties: { fields: { type: "array" }, path: { type: "string" }, truncated: { type: "boolean" }, nextPath: { type: "string" } } }
    }),
    makeLiveTool({
      name: "object.modify",
      summary: "Patch serialized object data.",
      description: "Patch serialized object data.",
      group: "object",
      destructiveHint: false,
      supportsDryRun: true,
      playModeSupport: "session-affecting",
      inputSchema: { type: "object", properties: { pathPatches: { type: "array" }, jsonPatch: { type: "array" } } }
    })
  );

  const getData = tools.get("object.get-data")!;
  assert.equal(getData.destructiveHint, false);
  assert.equal(getData.handler, "bridge");
  assert.equal(getData.playModeSupport, "supported");
  const getProps = getData.inputSchema.properties as Record<string, unknown>;
  const getOutput = getData.outputSchema?.properties as Record<string, unknown>;
  assert.ok(getProps.instanceId);
  assert.ok(getProps.assetPath);
  assert.ok(getProps.guid);
  assert.ok(getProps.path);
  assert.ok(getProps.paths);
  assert.ok(getProps.viewQuery);
  assert.ok(getProps.maxArrayItems);
  assert.ok(getOutput.fields);
  assert.ok(getOutput.path);
  assert.ok(getOutput.truncated);
  assert.ok(getOutput.nextPath);
  assert.equal(getOutput.data, undefined);
  assert.match(getData.description, /mutually exclusive/);

  const modify = tools.get("object.modify")!;
  const modifyProps = modify.inputSchema.properties as Record<string, unknown>;
  assert.equal(modify.destructiveHint, false);
  assert.equal(modify.supportsDryRun, true);
  assert.equal(modify.playModeSupport, "session-affecting");
  assert.equal(modify.riskLevel, "high");
  assert.ok(modifyProps.pathPatches);
  assert.ok(modifyProps.jsonPatch);
});

test("bridge specs resolve for object tools", () => {
  const objectGet = bridgeCommandRegistry.lookup("object", "get-data");
  assert.equal(objectGet?.bridgeCommand, "project-tool.run");
  const objectModify = bridgeCommandRegistry.lookup("object", "modify");
  assert.equal(objectModify?.bridgeCommand, "project-tool.run");
});

test("contract entries exist for object tools with present C# handler", () => {
  for (const name of ["object.get-data", "object.modify"]) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in command contract`);
    assert.equal(contract?.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("object.modify stays out of destructive security policy because it changes scene memory only", () => {
  const commands = new Set(destructiveCommands().map((entry) => entry.command));
  assert.equal(commands.has("object.modify"), false);
});

test("object commands are exposed by typed C# tools", async () => {
  const allSource = await readEditorCsSources();
  for (const command of ["object.get-data", "object.modify"]) {
    assert.ok(allSource.includes(`Name = "${command}"`), `${command} should expose its public tool name`);
  }
});

test("C# infrastructure files exist for ObjectRef, serializer and patch", async () => {
  const infraFiles = await listCsFiles(new URL("../unitypkg/Editor/Infrastructure/", import.meta.url));
  const names = infraFiles.map((file) => file.pathname.split("/").pop());
  assert.ok(names.includes("UnityCliObjectRef.cs"));
  assert.ok(names.includes("UnityCliObjectSerializer.cs"));
  assert.ok(names.includes("UnityCliObjectPatch.cs"));
});

test("object patch accepts only slash-separated member paths", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliObjectPatch.cs", import.meta.url), "utf8");
  assert.match(source, /path\.Split\('\/'\)/);
  assert.match(source, /pathPatches path must use '\/' between member segments/);
  assert.doesNotMatch(source, /向后兼容|a\.b.*兼容|var sep = path\.IndexOf/);
});

test("object patch writes nested value types back through their parent chain", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliObjectPatch.cs", import.meta.url), "utf8");
  assert.match(source, /List<NavigationFrame>/);
  assert.match(source, /current\.GetType\(\)\.IsValueType/);
  assert.match(source, /WriteBackValueType\(frames\[i\]\.Parent, frames\[i\]\.Segment, current, path\)/);
  assert.match(source, /property\.SetValue\(parent, ConvertValue\(value, property\.PropertyType\), null\)/);
  assert.match(source, /list\[segment\.Index\] = value/);
  assert.match(source, /dictionary\[segment\.Name\] = value/);
});

test("object.modify implements IUnityCliTool with dry-run support", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/object/modify.cs", import.meta.url), "utf8");
  assert.match(source, /class ObjectModifyTool/, "ObjectModifyTool should exist");
  assert.match(source, /DryRun/, "object.modify should support dry-run");
});

test("C# object serializer implements path aggregate and viewQuery conflict", async () => {
  const serializer = await readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliObjectSerializer.cs", import.meta.url), "utf8");
  const supportSource = await readFile(new URL("../unitypkg/Editor/Tools/object/ObjectToolSupport.cs", import.meta.url), "utf8");
  assert.match(serializer, /BuildPathReadAggregate[\s\S]*TypeName = "UnityCli\.PathReadAggregate"/, "paths should return a stable aggregate envelope");
  assert.match(serializer, /TryReadPath[\s\S]*ReadPathSegment[\s\S]*ReadIndexer/, "paths should read exact nested members and indexers");
  assert.match(supportSource, /E_OBJECT_READ_SCOPE_CONFLICT[\s\S]*paths and viewQuery are mutually exclusive/, "object serializer options should reject paths and viewQuery together");
});

test("C# object serializer represents non-finite dynamic numbers without invalid JSON", async () => {
  const serializer = await readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliObjectSerializer.cs", import.meta.url), "utf8");
  assert.match(serializer, /JsonNumberData\(double value\)/);
  assert.match(serializer, /double\.IsNaN\(value\)[\s\S]*return "NaN"/);
  assert.match(serializer, /double\.IsPositiveInfinity\(value\)[\s\S]*return "Infinity"/);
  assert.match(serializer, /double\.IsNegativeInfinity\(value\)[\s\S]*return "-Infinity"/);
  assert.match(serializer, /value is float single[\s\S]*JsonNumberData\(single\)/);
});

test("C# paged capabilities return nextCursor in the same numeric domain as cursor", async () => {
  const files = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const source = (await Promise.all(files.map((file) => readFile(file, "utf8")))).join("\n");
  assert.doesNotMatch(source, /string NextCursor\s*\{/);
  assert.doesNotMatch(source, /NextCursor\s*=.*ToString\(/);
  assert.match(source, /long\? NextCursor\s*\{/);
});

test("no duplicate main entry: go.inspect stays lightweight, object.get-data is the deep reader", async () => {
  const tools = await discoverLiveTools(
    makeLiveTool({ name: "go.inspect", summary: "Inspect a GameObject.", description: "Inspect a GameObject with a lightweight summary.", group: "go" }),
    makeLiveTool({ name: "object.get-data", summary: "Read serialized object data.", description: "Read deep serialized object data.", group: "object" })
  );
  const inspect = tools.get("go.inspect")!;
  const getData = tools.get("object.get-data")!;
  assert.notEqual(inspect.name, getData.name);
  assert.ok(getData.description.toLowerCase().includes("deep"));
});
