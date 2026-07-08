import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";
import { contractToolNames, discoverLiveTools, readCsSources } from "./helpers/csharp-test-helpers.js";

/** Material 域契约测试：descriptor 改为 live discovery，结构与行为继续由 bridge spec / contract / C# 源码兜底。 */

test("material tools are contract-discovered and expose expected live descriptor fields", async () => {
  assert.deepEqual(contractToolNames("material"), [
    "material.assign",
    "material.create",
    "material.info",
    "material.set",
    "material.set-renderer-color"
  ]);

  const tools = await discoverLiveTools(
    makeLiveTool({
      name: "material.info",
      summary: "Read material information.",
      description: "Read material information with optional token-safe namePattern filtering.",
      group: "material",
      inputSchema: { type: "object", properties: { namePattern: { type: "string" } } },
      outputSchema: { type: "object", properties: { material: { type: "object" }, nameFilterApplied: { type: "boolean" } } }
    }),
    makeLiveTool({
      name: "material.create",
      summary: "Create a material asset.",
      description: "Create a material asset.",
      group: "material",
      playModeSupport: "session-affecting",
      sideEffects: ["asset-write"]
    }),
    makeLiveTool({
      name: "material.assign",
      summary: "Assign a material.",
      description: "Assign a material to a renderer.",
      group: "material",
      playModeSupport: "session-affecting",
      sideEffects: ["scene-modify"]
    }),
    makeLiveTool({
      name: "material.set",
      summary: "Set material properties.",
      description: "Set material properties.",
      group: "material",
      playModeSupport: "session-affecting",
      sideEffects: ["asset-write"],
      inputSchema: {
        type: "object",
        properties: {
          color: { type: "string" },
          floatValue: { type: "number" },
          vector: { type: "array" },
          texture: { type: "string" }
        }
      }
    }),
    makeLiveTool({
      name: "material.set-renderer-color",
      summary: "Set a renderer color override.",
      description: "Set a renderer color override with MaterialPropertyBlock.",
      group: "material",
      playModeSupport: "session-affecting",
      supportsDryRun: true,
      sideEffects: ["scene-modify", "renderer-property-block"]
    })
  );

  const info = tools.get("material.info")!;
  assert.equal(info.destructiveHint, false);
  assert.equal(info.handler, "bridge");
  assert.equal(info.playModeSupport, "supported");
  const infoProps = info.inputSchema.properties as Record<string, unknown>;
  const infoOutput = info.outputSchema?.properties as Record<string, unknown>;
  assert.ok(infoProps.namePattern);
  assert.ok(infoOutput.material);
  assert.ok(infoOutput.nameFilterApplied);

  const create = tools.get("material.create")!;
  assert.equal(create.playModeSupport, "session-affecting");
  assert.ok(create.sideEffects?.includes("asset-write"));

  const assign = tools.get("material.assign")!;
  assert.equal(assign.playModeSupport, "session-affecting");
  assert.ok(assign.sideEffects?.includes("scene-modify"));

  const set = tools.get("material.set")!;
  assert.equal(set.playModeSupport, "session-affecting");
  assert.ok(set.sideEffects?.includes("asset-write"));
  const setProps = set.inputSchema.properties as Record<string, unknown>;
  assert.ok(setProps.color);
  assert.ok(setProps.floatValue);
  assert.ok(setProps.vector);
  assert.ok(setProps.texture);

  const rendererColor = tools.get("material.set-renderer-color")!;
  assert.equal(rendererColor.playModeSupport, "session-affecting");
  assert.equal(rendererColor.destructiveHint, false);
  assert.ok(rendererColor.supportsDryRun);
  assert.deepEqual(rendererColor.sideEffects, ["scene-modify", "renderer-property-block"]);
});

test("bridge specs resolve for material tools", () => {
  for (const command of ["info", "create", "assign", "set", "set-renderer-color"]) {
    const spec = bridgeCommandRegistry.lookup("material", command);
    assert.equal(spec?.bridgeCommand, "project-tool.run", `material ${command} should route via project-tool.run`);
    assert.equal(spec?.toolName, `material.${command}`, `material ${command} toolName should match`);
  }
});

test("contract entries exist for material tools with present C# handler", () => {
  for (const name of ["material.info", "material.create", "material.assign", "material.set", "material.set-renderer-color"]) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in command contract`);
    assert.equal(contract?.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("material C# error recovery hints use current tools run form, not legacy unity_cli", async () => {
  const source = await readCsSources(new URL("../unitypkg/Editor/Tools/material/", import.meta.url));
  assert.equal(source.includes("unity_cli material info"), false, "must not reference legacy 'unity_cli material info'");
  assert.equal(source.includes("unity_cli asset search"), false, "must not reference legacy 'unity_cli asset search'");
  assert.match(source, /tools run material\.info/, "error hints should route to tools run material.info");
  assert.match(source, /tools run asset\.search/, "error hints should route to tools run asset.search");
});

test("material.info C# implements namePattern filtering", async () => {
  const source = await readCsSources(new URL("../unitypkg/Editor/Tools/material/", import.meta.url));
  assert.match(source, /ParseNameFilter\(string namePattern\)/, "C# should parse namePattern into a filter set");
  assert.match(source, /BuildMaterialInfo\(path, material, nameFilter\)/, "material.info should pass the parsed filter into the material summary builder");
  assert.match(source, /NameFilterApplied = nameFilter != null/, "material.info should report whether a name filter was applied");
});

test("renderer color uses MaterialPropertyBlock in C#", async () => {
  const source = await readCsSources(new URL("../unitypkg/Editor/Tools/material/", import.meta.url));
  assert.match(source, /MaterialPropertyBlock[\s\S]*SetColor/, "renderer color should use MaterialPropertyBlock");
});

test("material commands are registered in C#", async () => {
  const source = await readCsSources(new URL("../unitypkg/Editor/", import.meta.url));
  for (const command of ["material.info", "material.create", "material.assign", "material.set", "material.set-renderer-color"]) {
    const hasRegisterCommand = source.includes(`RegisterCommand(registry, "${command}"`);
    const hasIUnityCliTool = source.includes(`Name = "${command}"`);
    assert.ok(hasRegisterCommand || hasIUnityCliTool, `${command} should be registered via RegisterCommand or IUnityCliTool`);
  }
});

test("material bridge spec forwards structured --input fields", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/material.js", import.meta.url), "utf8");
  assert.match(spec, /structured\.target/, "bridge should forward structured target");
  assert.match(spec, /structured\.path/, "bridge should forward structured material path");
  assert.match(spec, /structured\.property/, "bridge should forward structured property");
});
