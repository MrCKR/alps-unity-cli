import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";
import { contractToolNames, discoverLiveTools, readCsSources, readEditorCsSources } from "./helpers/csharp-test-helpers.js";

const GRAPHICS_COMMANDS = [
  "graphics.bake-cancel",
  "graphics.bake-clear",
  "graphics.bake-reflection-probe",
  "graphics.bake-settings",
  "graphics.bake-start",
  "graphics.bake-status",
  "graphics.light-probe-group",
  "graphics.pipeline-info",
  "graphics.pipeline-quality",
  "graphics.pipeline-settings",
  "graphics.probe-positions",
  "graphics.quality",
  "graphics.reflection-probe",
  "graphics.scene-debug",
  "graphics.settings",
  "graphics.skybox-get",
  "graphics.skybox-set-ambient",
  "graphics.skybox-set-fog",
  "graphics.skybox-set-material",
  "graphics.skybox-set-properties",
  "graphics.skybox-set-reflection",
  "graphics.skybox-set-sun",
  "graphics.stats",
  "graphics.stats-counters",
  "graphics.stats-memory",
  "graphics.volume"
];

test("graphics group exposes all 26 tools via contract discovery", () => {
  assert.deepEqual(contractToolNames("graphics"), [...GRAPHICS_COMMANDS].sort());
});

test("graphics live descriptors expose representative play mode semantics", async () => {
  const tools = await discoverLiveTools(
    makeLiveTool({ name: "graphics.pipeline-info", summary: "Read render pipeline and quality info.", description: "Read render pipeline and quality info.", group: "graphics" }),
    makeLiveTool({ name: "graphics.skybox-get", summary: "Read skybox settings.", description: "Read skybox settings.", group: "graphics" }),
    makeLiveTool({ name: "graphics.stats", summary: "Read graphics stats.", description: "Read graphics stats.", group: "graphics" }),
    makeLiveTool({ name: "graphics.stats-counters", summary: "Read graphics counters.", description: "Read graphics counters.", group: "graphics" }),
    makeLiveTool({ name: "graphics.stats-memory", summary: "Read graphics memory stats.", description: "Read graphics memory stats.", group: "graphics" }),
    makeLiveTool({ name: "graphics.bake-status", summary: "Read bake status.", description: "Read bake status.", group: "graphics" }),
    makeLiveTool({ name: "graphics.volume", summary: "Read volume profile state.", description: "Read volume profile state.", group: "graphics" }),
    makeLiveTool({ name: "graphics.bake-settings", summary: "Read or update bake settings.", description: "Read or update persistent LightingSettings.", group: "graphics", destructiveHint: true, playModeSupport: "editor-only", sideEffects: ["scene-modify"] }),
    makeLiveTool({ name: "graphics.quality", summary: "Mutate quality settings.", description: "Mutate quality settings.", group: "graphics", playModeSupport: "session-affecting", sideEffects: ["scene-modify"] }),
    makeLiveTool({ name: "graphics.settings", summary: "Mutate graphics settings.", description: "Mutate graphics settings.", group: "graphics", destructiveHint: true, playModeSupport: "editor-only", sideEffects: ["settings-write"] }),
    makeLiveTool({ name: "graphics.pipeline-quality", summary: "Mutate pipeline quality settings.", description: "Mutate pipeline quality settings.", group: "graphics", playModeSupport: "session-affecting", sideEffects: ["scene-modify"] }),
    makeLiveTool({ name: "graphics.pipeline-settings", summary: "Mutate pipeline settings.", description: "Mutate pipeline settings.", group: "graphics", playModeSupport: "session-affecting", sideEffects: ["scene-modify"] })
  );

  for (const name of ["graphics.pipeline-info", "graphics.skybox-get", "graphics.stats", "graphics.stats-counters", "graphics.stats-memory", "graphics.bake-status", "graphics.volume"]) {
    assert.equal(tools.get(name)?.playModeSupport, "supported", `${name} should be supported`);
  }
  for (const name of ["graphics.quality", "graphics.pipeline-quality", "graphics.pipeline-settings"]) {
    assert.equal(tools.get(name)?.playModeSupport, "session-affecting", `${name} should disclose graphics settings writes`);
  }
  const settings = tools.get("graphics.settings")!;
  assert.equal(settings.destructiveHint, true);
  assert.equal(settings.playModeSupport, "editor-only");
  assert.ok(settings.sideEffects?.includes("settings-write"));
  const bakeSettings = tools.get("graphics.bake-settings")!;
  assert.equal(bakeSettings.destructiveHint, true);
  assert.equal(bakeSettings.playModeSupport, "editor-only");
});

test("bridge specs resolve for all graphics tools", () => {
  for (const name of GRAPHICS_COMMANDS) {
    const cmd = name.replace("graphics.", "");
    const spec = bridgeCommandRegistry.lookup("graphics", cmd);
    assert.ok(spec, `graphics ${cmd} should resolve`);
    assert.equal(spec?.bridgeCommand, "project-tool.run", `graphics ${cmd} should route via project-tool.run`);
    assert.equal(spec?.toolName, name, `graphics ${cmd} toolName should match`);
  }
});

test("contract entries exist for all graphics tools with present C# handler", () => {
  for (const name of GRAPHICS_COMMANDS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
    assert.equal(contract?.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("graphics C# handlers are registered", async () => {
  const allSource = await readEditorCsSources();
  for (const command of GRAPHICS_COMMANDS) {
    const hasRegisterCommand = allSource.includes(`RegisterCommand(registry, "${command}"`);
    const hasIUnityCliTool = allSource.includes(`Name = "${command}"`);
    assert.ok(hasRegisterCommand || hasIUnityCliTool, `${command} should be registered via RegisterCommand or IUnityCliTool`);
  }
});

test("graphics C# error recovery hints use current tools run forms", async () => {
  const source = await readCsSources(new URL("../unitypkg/Editor/Tools/graphics/", import.meta.url));
  assert.match(source, /tools run graphics\.quality/, "error hints should route to tools run graphics.quality");
});

test("graphics bridge spec forwards structured --input fields", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/graphics.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(s, "target"\)/, "bridge should forward structured target");
  assert.match(spec, /boolField\(s, "enabled"\)/, "bridge should forward structured enabled");
  assert.match(spec, /numberField\(s, "bounces"\)/, "bridge should forward structured bounces");
  assert.match(spec, /stringField\(s, "material"\)/, "bridge should forward structured material");
});

test("skybox property writes reject non-Assets materials and persist before readback", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/graphics/skybox-set-properties.cs", import.meta.url), "utf8");
  assert.match(source, /!skyboxPath\.StartsWith\("Assets\/", System\.StringComparison\.OrdinalIgnoreCase\)/, "builtin and package materials must fail before the effect barrier");
  assert.match(source, /E_SKYBOX_ASSET_PATH_INVALID[\s\S]*ctx\.CommitEffect\(\)[\s\S]*EditorUtility\.SetDirty\(skybox\)[\s\S]*AssetDatabase\.SaveAssetIfDirty\(skybox\)[\s\S]*ReloadAsset<Material>/, "skybox writes must persist the exact asset before fresh readback");
});

test("graphics C# keeps current pipeline/quality/bake implementation details", async () => {
  const source = await readCsSources(new URL("../unitypkg/Editor/Tools/graphics/", import.meta.url));
  assert.match(source, /ResolveQualityLevel[\s\S]*QualitySettings\.names[\s\S]*E_QUALITY_LEVEL_INVALID/, "graphics quality should resolve index or quality level name");
  assert.match(source, /var pipeline = resetToBuiltIn \? null : AssetDatabase\.LoadAssetAtPath<RenderPipelineAsset>/, "pipeline settings should plan built-in/null assignment before commit");
  assert.match(source, /GraphicsSettings\.defaultRenderPipeline = pipeline/, "pipeline settings should apply the prevalidated asset or null");
  assert.match(source, /BuildGraphicsSummary\(\)[\s\S]*BuildQualitySettings\(\)/, "pipeline info should return graphics and quality summaries through shared helpers");
  assert.match(source, /new LightingSettings\(\)/, "bake settings writes should create LightingSettings when the scene lacks one");
  assert.match(source, /builtin:Default-Skybox[\s\S]*AssetDatabase\.GetBuiltinExtraResource<Material>\("Default-Skybox\.mat"\)/, "skybox material restore should support Unity built-in default skybox");
  assert.doesNotMatch(source, /unity_cli graphics /, "graphics error guidance must use tools run graphics.* shape");
});
