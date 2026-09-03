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
  "graphics.pipeline-settings",
  "graphics.quality",
  "graphics.reflection-probe",
  "graphics.scene-debug",
  "graphics.skybox-get",
  "graphics.skybox-set-ambient",
  "graphics.skybox-set-fog",
  "graphics.skybox-set-material",
  "graphics.skybox-set-properties",
  "graphics.skybox-set-reflection",
  "graphics.skybox-set-sun",
  "graphics.stats",
  "graphics.volume"
];

test("graphics group exposes dedicated quality, pipeline, probe, and unified stats tools", () => {
  assert.deepEqual(contractToolNames("graphics"), [...GRAPHICS_COMMANDS].sort());
});

test("graphics live descriptors expose representative play mode semantics", async () => {
  const tools = await discoverLiveTools(
    makeLiveTool({ name: "graphics.pipeline-info", summary: "Read render pipeline and quality info.", description: "Read render pipeline and quality info.", group: "graphics" }),
    makeLiveTool({ name: "graphics.skybox-get", summary: "Read skybox settings.", description: "Read skybox settings.", group: "graphics" }),
    makeLiveTool({ name: "graphics.stats", summary: "Read graphics stats.", description: "Read graphics stats.", group: "graphics" }),
    makeLiveTool({ name: "graphics.light-probe-group", summary: "Read or set light probes.", description: "Read or set one LightProbeGroup with fresh readback.", group: "graphics", sideEffects: ["scene-modify"] }),
    makeLiveTool({ name: "graphics.reflection-probe", summary: "Read or configure a reflection probe.", description: "Read or configure one ReflectionProbe with fresh readback.", group: "graphics", sideEffects: ["scene-modify"] }),
    makeLiveTool({ name: "graphics.bake-status", summary: "Read bake status.", description: "Read bake status.", group: "graphics" }),
    makeLiveTool({ name: "graphics.volume", summary: "Read volume profile state.", description: "Read volume profile state.", group: "graphics" }),
    makeLiveTool({ name: "graphics.bake-settings", summary: "Read or update bake settings.", description: "Read or update persistent LightingSettings.", group: "graphics", destructiveHint: true, playModeSupport: "editor-only", sideEffects: ["scene-modify"] }),
    makeLiveTool({ name: "graphics.quality", summary: "Read or mutate QualitySettings.", description: "Dedicated QualitySettings read/write with fresh readback.", group: "graphics", playModeSupport: "session-affecting", sideEffects: ["settings-write"] }),
    makeLiveTool({ name: "graphics.pipeline-settings", summary: "Read or mutate the default pipeline.", description: "Dedicated render pipeline read/write with fresh path and GUID readback.", group: "graphics", playModeSupport: "session-affecting", sideEffects: ["settings-write", "asset-import"] })
  );

  for (const name of ["graphics.pipeline-info", "graphics.skybox-get", "graphics.stats", "graphics.light-probe-group", "graphics.reflection-probe", "graphics.bake-status", "graphics.volume"]) {
    assert.equal(tools.get(name)?.playModeSupport, "supported", `${name} should be supported`);
  }
  for (const name of ["graphics.quality", "graphics.pipeline-settings"]) {
    assert.equal(tools.get(name)?.playModeSupport, "session-affecting", `${name} should disclose graphics settings writes`);
  }
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

test("graphics dedicated replacements expose fresh mutation readback and bounded unified stats", async () => {
  const [quality, pipeline, lightProbe, reflectionProbe, stats] = await Promise.all([
    readFile(new URL("../unitypkg/Editor/Tools/graphics/quality.cs", import.meta.url), "utf8"),
    readFile(new URL("../unitypkg/Editor/Tools/graphics/pipeline-settings.cs", import.meta.url), "utf8"),
    readFile(new URL("../unitypkg/Editor/Tools/graphics/light-probe-group.cs", import.meta.url), "utf8"),
    readFile(new URL("../unitypkg/Editor/Tools/graphics/reflection-probe.cs", import.meta.url), "utf8"),
    readFile(new URL("../unitypkg/Editor/Tools/graphics/stats.cs", import.meta.url), "utf8"),
  ]);
  assert.match(quality, /PlayModeSupport = "session-affecting"/);
  assert.match(quality, /BuildQualitySettings\(\)[\s\S]*E_QUALITY_STATE_MISMATCH/);
  assert.match(pipeline, /PlayModeSupport = "session-affecting"/);
  assert.match(pipeline, /ReloadAsset<RenderPipelineAsset>[\s\S]*E_RENDER_PIPELINE_STATE_MISMATCH/);
  assert.match(lightProbe, /PlayModeSupport = "supported"[\s\S]*BuildProbeTarget[\s\S]*BuildProbePositions/);
  assert.match(lightProbe, /E_LIGHT_PROBE_GROUP_STATE_MISMATCH/);
  assert.match(reflectionProbe, /PlayModeSupport = "supported"[\s\S]*BuildProbeTarget/);
  assert.match(reflectionProbe, /E_REFLECTION_PROBE_STATE_MISMATCH/);
  assert.match(stats, /Read one unified render counter and memory snapshot/);
  assert.match(stats, /PlayModeSupport = "supported"/);
});

test("graphics C# keeps current pipeline/quality/bake implementation details", async () => {
  const source = await readCsSources(new URL("../unitypkg/Editor/Tools/graphics/", import.meta.url));
  assert.match(source, /ResolveQualityLevel[\s\S]*QualitySettings\.names[\s\S]*E_QUALITY_LEVEL_INVALID/, "graphics quality should resolve index or quality level name");
  assert.match(source, /var pipeline = resetToBuiltIn \? null : AssetDatabase\.LoadAssetAtPath<RenderPipelineAsset>/, "pipeline settings should plan built-in/null assignment before commit");
  assert.match(source, /GraphicsSettings\.defaultRenderPipeline = pipeline/, "pipeline settings should apply the prevalidated asset or null");
  assert.match(source, /BuildGraphicsSummary\(\)[\s\S]*BuildQualitySettings\(\)/, "pipeline info should return graphics and quality summaries through shared helpers");
  assert.match(source, /new LightingSettings\(\)/, "bake settings writes should create LightingSettings when the scene lacks one");
  assert.match(source, /builtin:Default-Skybox[\s\S]*AssetDatabase\.GetBuiltinExtraResource<Material>\("Default-Skybox\.mat"\)/, "skybox material restore should support Unity built-in default skybox");
  assert.doesNotMatch(source, /unitycli graphics /, "graphics error guidance must use tools run graphics.* shape");
});
