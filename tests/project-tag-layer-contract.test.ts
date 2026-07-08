import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";
import { discoverLiveTools, readEditorCsSources } from "./helpers/csharp-test-helpers.js";

/** Project 域契约测试：descriptor 改走 live discovery，路由与注册继续由 bridge spec / C# 源码覆盖。 */

test("project.tag and project.layer expose expected live descriptor fields", async () => {
  const tools = await discoverLiveTools(
    makeLiveTool({
      name: "project.tag",
      summary: "Mutate tags in TagManager.",
      description: "Mutate tags in TagManager.",
      group: "project",
      playModeSupport: "session-affecting",
      sideEffects: ["project-settings-write"],
      outputSchema: { type: "object", properties: { changed: { type: "boolean" }, plannedEffects: { type: "array" } } }
    }),
    makeLiveTool({
      name: "project.layer",
      summary: "Mutate layers in TagManager.",
      description: "Mutate layers in TagManager.",
      group: "project",
      playModeSupport: "session-affecting",
      sideEffects: ["project-settings-write"],
      outputSchema: { type: "object", properties: { changed: { type: "boolean" }, plannedEffects: { type: "array" } } }
    })
  );

  for (const name of ["project.tag", "project.layer"]) {
    const tool = tools.get(name)!;
    assert.equal(tool.destructiveHint, false);
    assert.equal(tool.handler, "bridge");
    assert.equal(tool.playModeSupport, "session-affecting");
    assert.ok(tool.sideEffects?.includes("project-settings-write"));
    const out = tool.outputSchema?.properties as Record<string, unknown>;
    assert.ok(out.changed, `${name} output should expose changed`);
    assert.ok(out.plannedEffects, `${name} output should expose plannedEffects`);
  }
});

test("bridge specs resolve for project tools", () => {
  const tag = bridgeCommandRegistry.lookup("project", "tag");
  assert.equal(tag?.bridgeCommand, "project-tool.run");
  assert.equal(tag?.toolName, "project.tag");
  const layer = bridgeCommandRegistry.lookup("project", "layer");
  assert.equal(layer?.bridgeCommand, "project-tool.run");
  assert.equal(layer?.toolName, "project.layer");
});

test("contract entries exist for project tools with present C# handler", () => {
  for (const name of ["project.tag", "project.layer"]) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in command contract`);
    assert.equal(contract?.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("C# handlers exist for project.tag/layer (RegisterCommand or IUnityCliTool)", async () => {
  const allSource = await readEditorCsSources();
  for (const command of ["project.tag", "project.layer"]) {
    const hasRegisterCommand = allSource.includes(`RegisterCommand(registry, "${command}"`);
    const hasIUnityCliTool = allSource.includes(`Name = "${command}"`);
    assert.ok(hasRegisterCommand || hasIUnityCliTool, `${command} should be registered via RegisterCommand or IUnityCliTool in C#`);
  }
});

test("project bridge spec forwards structured --input fields (action/name/index)", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/editor.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(flags, "action"\)/, "bridge should forward structured action");
  assert.match(spec, /stringField\(flags, "name"\)/, "bridge should forward structured name");
  assert.match(spec, /numberField\(flags, "index"\)/, "bridge should forward structured index");
});
