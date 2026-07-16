import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";
import { contractToolNames, discoverLiveTools, readEditorCsSources } from "./helpers/csharp-test-helpers.js";

/** ScriptableObject 域契约测试：descriptor 改走 live discovery。 */

test("scriptable-object tools are contract-discovered and expose expected live descriptor fields", async () => {
  assert.deepEqual(contractToolNames("scriptable-object"), [
    "scriptable-object.create",
    "scriptable-object.info",
    "scriptable-object.list-types",
    "scriptable-object.modify"
  ]);

  const tools = await discoverLiveTools(
    makeLiveTool({
      name: "scriptable-object.create",
      summary: "Create a ScriptableObject asset.",
      description: "Create a ScriptableObject asset.",
      group: "scriptable-object",
      destructiveHint: true,
      supportsDryRun: true,
      playModeSupport: "session-affecting",
      sideEffects: ["asset-write"]
    }),
    makeLiveTool({
      name: "scriptable-object.info",
      summary: "Read ScriptableObject info.",
      description: "Read ScriptableObject info.",
      group: "scriptable-object",
      outputSchema: { type: "object", properties: { scriptableObject: { type: "object" } } }
    }),
    makeLiveTool({
      name: "scriptable-object.list-types",
      summary: "List ScriptableObject types.",
      description: "List ScriptableObject types.",
      group: "scriptable-object"
    }),
    makeLiveTool({
      name: "scriptable-object.modify",
      summary: "Modify a ScriptableObject asset.",
      description: "Modify a ScriptableObject asset.",
      group: "scriptable-object",
      destructiveHint: true,
      supportsDryRun: true,
      playModeSupport: "session-affecting",
      sideEffects: ["asset-write"]
    })
  );

  const create = tools.get("scriptable-object.create")!;
  assert.equal(create.destructiveHint, true);
  assert.equal(create.supportsDryRun, true);
  assert.equal(create.handler, "bridge");
  assert.equal(create.playModeSupport, "session-affecting");
  assert.ok(create.sideEffects?.includes("asset-write"));

  const info = tools.get("scriptable-object.info")!;
  assert.equal(info.playModeSupport, "supported");
  const out = info.outputSchema?.properties as Record<string, unknown>;
  assert.ok(out.scriptableObject);

  const listTypes = tools.get("scriptable-object.list-types")!;
  assert.equal(listTypes.destructiveHint, false);
  assert.equal(listTypes.playModeSupport, "supported");

  const modify = tools.get("scriptable-object.modify")!;
  assert.equal(modify.destructiveHint, true);
  assert.equal(modify.supportsDryRun, true);
  assert.equal(modify.playModeSupport, "session-affecting");
  assert.ok(modify.sideEffects?.includes("asset-write"));
  assert.equal(modify.riskLevel, "high");
});

test("bridge specs resolve for scriptable-object tools", () => {
  for (const command of ["create", "info", "list-types", "modify"]) {
    const spec = bridgeCommandRegistry.lookup("scriptable-object", command);
    assert.equal(spec?.bridgeCommand, "project-tool.run");
  }
});

test("contract entries exist for scriptable-object tools with present C# handler", () => {
  for (const name of ["scriptable-object.create", "scriptable-object.info", "scriptable-object.list-types", "scriptable-object.modify"]) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in command contract`);
    assert.equal(contract?.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("scriptable-object commands are exposed by typed C# tools", async () => {
  const allSource = await readEditorCsSources();
  for (const command of ["scriptable-object.create", "scriptable-object.info", "scriptable-object.list-types", "scriptable-object.modify"]) {
    assert.ok(allSource.includes(`Name = "${command}"`), `${command} should expose its public tool name`);
  }
});

test("scriptable-object C# error recovery hints use current tools run forms", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/scriptable-object/ScriptableObjectToolSupport.cs", import.meta.url), "utf8");
  assert.match(source, /tools run asset\.search/, "error hints should route to tools run asset.search");
});

test("scriptable-object bridge spec forwards structured --input fields", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/scriptable-object.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(input, flags, "type"\)/, "bridge should forward structured type");
  assert.match(spec, /stringField\(input, flags, "property"\)/, "bridge should forward structured property");
  assert.match(spec, /inputField\(input, "value"\)/, "bridge should forward structured value");
  assert.match(spec, /inputField\(input, "patches"\)/, "bridge should forward structured patches");
  assert.match(spec, /boolField\(input, flags, "overwrite"\)/, "bridge should forward structured overwrite");
});
