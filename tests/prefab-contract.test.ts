import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";
import { contractToolNames, discoverLiveTools, readCsSources, readEditorCsSources } from "./helpers/csharp-test-helpers.js";

const PREFAB_COMMANDS = [
  "prefab.create",
  "prefab.hierarchy",
  "prefab.info",
  "prefab.instantiate",
  "prefab.modify",
  "prefab.stage-close",
  "prefab.stage-open",
  "prefab.stage-save",
  "prefab.unpack"
];

test("prefab group is contract-discovered with 9 tools", () => {
  assert.deepEqual(contractToolNames("prefab"), [...PREFAB_COMMANDS].sort());
});

test("prefab live descriptors expose expected safety hints", async () => {
  const tools = await discoverLiveTools(
    makeLiveTool({ name: "prefab.info", summary: "Read prefab info.", description: "Read prefab info.", group: "prefab" }),
    makeLiveTool({ name: "prefab.create", summary: "Create a prefab asset.", description: "Create a prefab asset.", group: "prefab", playModeSupport: "session-affecting", sideEffects: ["asset-write"] }),
    makeLiveTool({ name: "prefab.stage-open", summary: "Open Prefab Stage.", description: "Open Prefab Stage.", group: "prefab", playModeSupport: "editor-only" }),
    makeLiveTool({ name: "prefab.instantiate", summary: "Instantiate a prefab.", description: "Instantiate a prefab.", group: "prefab", supportsDryRun: true, playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "prefab.unpack", summary: "Unpack a prefab instance.", description: "Unpack a prefab instance.", group: "prefab", supportsDryRun: true }),
    makeLiveTool({ name: "prefab.hierarchy", summary: "Read prefab hierarchy.", description: "Read prefab hierarchy.", group: "prefab", outputSchema: { type: "object", properties: { children: { type: "array" }, pageInfo: { type: "object" } } } })
  );

  const info = tools.get("prefab.info")!;
  assert.equal(info.destructiveHint, false);
  assert.equal(info.playModeSupport, "supported");

  const create = tools.get("prefab.create")!;
  assert.equal(create.playModeSupport, "session-affecting");
  assert.ok(create.sideEffects?.includes("asset-write"));

  const stageOpen = tools.get("prefab.stage-open")!;
  assert.equal(stageOpen.playModeSupport, "editor-only", "stage-open is an editor UI session op");

  const instantiate = tools.get("prefab.instantiate")!;
  assert.equal(instantiate.destructiveHint, false);
  assert.equal(instantiate.supportsDryRun, true);
  assert.equal(instantiate.playModeSupport, "session-affecting");

  const unpack = tools.get("prefab.unpack")!;
  assert.equal(unpack.destructiveHint, false);
  assert.equal(unpack.supportsDryRun, true);

  const hierarchy = tools.get("prefab.hierarchy")!;
  const out = hierarchy.outputSchema?.properties as Record<string, unknown>;
  assert.ok(out.children);
  assert.ok(out.pageInfo);
});

test("bridge specs resolve for prefab tools", () => {
  const lookups: Record<string, string> = {
    info: "prefab.info",
    create: "prefab.create",
    modify: "prefab.modify",
    "stage-open": "prefab.stage-open",
    "stage-save": "prefab.stage-save",
    "stage-close": "prefab.stage-close",
    hierarchy: "prefab.hierarchy",
    instantiate: "prefab.instantiate",
    unpack: "prefab.unpack"
  };
  for (const [cmd, expected] of Object.entries(lookups)) {
    const spec = bridgeCommandRegistry.lookup("prefab", cmd);
    assert.equal(spec?.bridgeCommand, "project-tool.run", `prefab ${cmd} should route via project-tool.run`);
    assert.equal(spec?.toolName, expected, `prefab ${cmd} toolName should match`);
  }
});

test("contract entries exist for prefab tools with present C# handler", () => {
  for (const name of PREFAB_COMMANDS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in command contract`);
    assert.equal(contract?.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("prefab C# handlers are registered", async () => {
  const allSource = await readEditorCsSources();
  for (const command of PREFAB_COMMANDS) {
    const hasRegisterCommand = allSource.includes(`RegisterCommand(registry, "${command}"`);
    const hasIUnityCliTool = allSource.includes(`Name = "${command}"`);
    assert.ok(hasRegisterCommand || hasIUnityCliTool, `${command} should be registered via RegisterCommand or IUnityCliTool`);
  }
});

test("prefab C# error recovery hints use current tools run forms", async () => {
  const source = await readCsSources(new URL("../unitypkg/Editor/Tools/prefab/", import.meta.url));
  assert.match(source, /tools run prefab\.info/, "Prefab path guidance should route to prefab.info in the current CLI shape");
  assert.match(source, /tools run prefab\.hierarchy/, "child errors should still route to prefab.hierarchy when hierarchy inspection is needed");
});

test("prefab bridge spec forwards structured --input fields", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/prefab.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(flags, "addChild"\)/, "bridge should forward structured addChild");
  assert.match(spec, /stringField\(flags, "removeChild"\)/, "bridge should forward structured removeChild");
  assert.doesNotMatch(spec, /boolField\(flags, "save"\)/, "stage-close must not expose implicit save behavior");
  assert.match(spec, /numberField\(flags, "pageSize"\)/, "bridge should forward structured pageSize");
});
