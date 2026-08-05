import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";
import { discoverLiveTools, readEditorCsSources } from "./helpers/csharp-test-helpers.js";

const PROJECT_TOOLS = ["project.tag.add", "project.tag.remove", "project.layer.add", "project.layer.remove"];

test("project tag and layer tools expose independent persistent-write descriptors", async () => {
  const tools = await discoverLiveTools(...PROJECT_TOOLS.map((name) => makeLiveTool({ name, summary: "Mutate TagManager.", description: "Writes ProjectSettings/TagManager.asset.", group: "project", destructiveHint: true, playModeSupport: "supported", sideEffects: ["project-settings-write"], inputSchema: { type: "object", required: ["name"], properties: { name: { type: "string" } } } })));
  for (const name of PROJECT_TOOLS) {
    const tool = tools.get(name)!;
    assert.equal(tool.destructiveHint, true);
    assert.equal(tool.handler, "bridge");
    assert.equal(tool.playModeSupport, "supported");
    assert.ok(tool.sideEffects?.includes("project-settings-write"));
    assert.deepEqual(tool.inputSchema?.required, ["name"]);
  }
});

test("bridge specs resolve for independent project tools", () => {
  for (const name of PROJECT_TOOLS) {
    const [domain, object, action] = name.split(".");
    const spec = bridgeCommandRegistry.lookup(domain, `${object}-${action}`);
    assert.equal(spec?.bridgeCommand, "project-tool.run");
    assert.equal(spec?.toolName, name);
  }
});

test("contract entries exist for independent project tools with present C# handlers", () => {
  for (const name of PROJECT_TOOLS) assert.equal(findCommandContract(name)?.csharp_handler, "present");
});

test("C# handlers exist for independent project tools", async () => {
  const allSource = await readEditorCsSources();
  for (const command of PROJECT_TOOLS) assert.ok(allSource.includes(`Name = "${command}"`), `${command} should be an IUnityCliTool`);
  assert.doesNotMatch(allSource, /Name = "project\.(tag|layer)"/);
});

test("project tag and layer no-op returns before its sole effect barrier", async () => {
  const support = await readFile(new URL("../unitypkg/Editor/Tools/project/ProjectTagLayerToolSupport.cs", import.meta.url), "utf8");
  const noOp = support.indexOf("if (!plan.Changed)");
  const commit = support.indexOf("ctx.CommitEffect()");
  assert.ok(noOp >= 0 && commit > noOp);
  assert.match(support, /NoOpReason = add \? "already-present" : "already-absent"/);
});

test("project bridge spec forwards name and index without action", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/editor.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(flags, "name"\)/);
  assert.match(spec, /numberField\(flags, "index"\)/);
  assert.doesNotMatch(spec, /stringField\(flags, "action"\)/);
});
