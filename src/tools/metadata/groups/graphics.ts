import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

const plannedEffectsSchema = { type: "array", items: { type: "object" } };
const logPathSchema = { type: "string" };
const graphicsQualityOutput = objSchema({ graphics: { type: "object" }, qualitySettings: { type: "object" }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema });
const graphicsReadOutput = objSchema({ graphics: { type: "object" }, qualitySettings: { type: "object" }, logPath: logPathSchema });
const targetOutput = { type: "object" };

export const GRAPHICS_TOOLS: ToolMetadata[] = [
  {
    name: "graphics.quality",
    title: "Graphics Quality Level",
    description: "Read or update Unity QualitySettings: current quality level, VSync, anti-aliasing, shadows, LOD and related render-quality toggles. Pass level/qualityLevel as an index or quality level name; call without inputs to inspect available names first. Changes affect project/editor graphics state and current Play Mode session.",
    group: "graphics",
    inputSchema: objSchema({
      qualityLevel: { type: ["integer", "string"], description: "Quality level index or exact quality level name." },
      level: { type: ["integer", "string"], description: "Alias for qualityLevel, aligned with unity-mcp pipeline_set_quality." },
      vSyncCount: { type: "integer", description: "QualitySettings.vSyncCount." },
      antiAliasing: { type: "integer", description: "QualitySettings.antiAliasing sample count." },
      shadowDistance: { type: "number", description: "QualitySettings.shadowDistance." },
      masterTextureLimit: { type: "integer", description: "QualitySettings.globalTextureMipmapLimit." },
      lodBias: { type: "number", description: "QualitySettings.lodBias." },
      maximumLODLevel: { type: "integer", description: "QualitySettings.maximumLODLevel." },
      anisotropicFiltering: { type: "string", enum: ["Disable", "Enable", "ForceEnable"] },
      shadows: { type: "string", enum: ["Disable", "HardOnly", "All"] },
      shadowResolution: { type: "string", enum: ["Low", "Medium", "High", "VeryHigh"] },
      realtimeReflectionProbes: { type: "boolean" },
      softParticles: { type: "boolean" }
    }),
    outputSchema: graphicsQualityOutput,
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["quality-settings", "project-settings-write"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.quality",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.pipeline_set_quality / pipeline_get_info",
    examples: [
      { description: "Inspect quality settings", argv: ["tools", "run", "graphics.quality", "--input", "{}"] },
      { description: "Set quality by level name and vSync", argv: ["tools", "run", "graphics.quality", "--input", "{\"level\":\"High\",\"vSyncCount\":1}"] }
    ]
  },
  {
    name: "graphics.settings",
    title: "Graphics Settings",
    description: "Read or update common GraphicsSettings and QualitySettings fields. Use renderPipeline:'built-in' or null to clear the default SRP asset, or pass an Assets/*.asset RenderPipelineAsset path. For detailed pipeline-asset properties use graphics.pipeline-info first and pipeline-specific extension tools where available.",
    group: "graphics",
    inputSchema: objSchema({
      antiAliasing: { type: "integer" },
      shadowDistance: { type: "number" },
      vSyncCount: { type: "integer" },
      masterTextureLimit: { type: "integer" },
      lodBias: { type: "number" },
      maximumLODLevel: { type: "integer" },
      renderPipeline: { type: "string", description: "Assets/*.asset RenderPipelineAsset path, built-in, or null." },
      property: { type: "string", enum: ["vSyncCount", "antiAliasing", "shadowDistance"], description: "Legacy generic setting name." },
      value: { type: "string", description: "Legacy generic setting value." }
    }),
    outputSchema: graphicsQualityOutput,
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["graphics-settings", "quality-settings", "project-settings-write"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.settings",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.pipeline_set_settings",
    examples: [
      { description: "Inspect graphics settings", argv: ["tools", "run", "graphics.settings", "--input", "{}"] },
      { description: "Use built-in render pipeline", argv: ["tools", "run", "graphics.settings", "--input", "{\"renderPipeline\":\"built-in\"}"] }
    ]
  },
  {
    name: "graphics.pipeline",
    title: "Graphics Render Pipeline",
    description: "Read a compact active/default render pipeline and current QualitySettings summary. This is the quick pipeline ping equivalent; use graphics.pipeline-info for the fuller shape.",
    group: "graphics",
    inputSchema: objSchema({}),
    outputSchema: graphicsReadOutput,
    destructiveHint: false,
    playModeSupport: "supported",
    sideEffects: [],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.pipeline",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.ping / pipeline_get_info",
    examples: [
      { description: "Read pipeline summary", argv: ["tools", "run", "graphics.pipeline", "--input", "{}"] }
    ]
  },
  {
    name: "graphics.volume",
    title: "Graphics Volume",
    description: "Query Volume components and assigned profiles in the active scene. This is read-only and returns an empty list when SRP Volume types are unavailable.",
    group: "graphics",
    inputSchema: objSchema({ target: { type: "string", description: "Optional GameObject name/path/id to filter one Volume." }, pageSize: { type: "integer" }, cursor: { type: "string" } }),
    outputSchema: objSchema({ volumes: { type: "array", items: { type: "object" } }, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "supported",
    sideEffects: [],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.volume",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.volume_list",
    examples: [
      { description: "List scene volumes", argv: ["tools", "run", "graphics.volume", "--input", "{}"] },
      { description: "Inspect one volume by hierarchy path", argv: ["tools", "run", "graphics.volume", "--input", "{\"target\":\"Global Volume\"}"] }
    ]
  },
  {
    name: "graphics.screenshot",
    title: "Graphics Screenshot",
    description: "Capture a SceneView or active Camera screenshot with optional graphics diagnostics. Writes an image artifact; pass path under a deliberate artifact/output directory.",
    group: "graphics",
    inputSchema: objSchema({ path: { type: "string" }, width: { type: "integer" }, height: { type: "integer" }, format: { type: "string" }, overwrite: { type: "boolean" }, includeDiagnostics: { type: "boolean" } }, ["path"]),
    outputSchema: objSchema({ diagnostics: { type: "object" }, qualitySettings: { type: "object" }, screenshot: { type: "object" }, screenshotPath: { type: "string" }, artifactPath: { type: "string" }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["artifact-write", "scene-view-render"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.screenshot",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.capture_screenshot",
    examples: [
      { description: "Capture graphics screenshot", argv: ["tools", "run", "graphics.screenshot", "--input", "{\"path\":\".tmp/graphics/scene.png\",\"overwrite\":true,\"includeDiagnostics\":true}"] }
    ]
  },
  {
    name: "graphics.bake-start",
    title: "Start Light Bake",
    description: "Start Unity Lightmapping.BakeAsync for the active scene. This may run for a long time, produce lighting artifacts, and requires --yes; use graphics.bake-status first and only run after explicit approval.",
    group: "graphics",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ baking: { type: "boolean" }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema }),
    destructiveHint: true,
    playModeSupport: "editor-only",
    sideEffects: ["lightmapping-bake", "asset-write", "editor-long-running"],
    riskLevel: "medium",
    longRunning: true,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.bake-start",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.light_bake_start",
    examples: [
      { description: "Start a real async bake after approval", argv: ["tools", "run", "graphics.bake-start", "--yes", "--input", "{}"] }
    ]
  },
  {
    name: "graphics.bake-cancel",
    title: "Cancel Light Bake",
    description: "Cancel the current lighting bake if one is running. Safe to call when no bake is active; returns cancelled:false.",
    group: "graphics",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ baking: { type: "boolean" }, cancelled: { type: "boolean" }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "editor-only",
    sideEffects: ["lightmapping-bake-cancel"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.bake-cancel",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.light_bake_cancel",
    examples: [
      { description: "Cancel active bake if present", argv: ["tools", "run", "graphics.bake-cancel", "--input", "{}"] }
    ]
  },
  {
    name: "graphics.bake-status",
    title: "Light Bake Status",
    description: "Query current lighting bake running state, progress, and lighting mode without starting or cancelling a bake.",
    group: "graphics",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ baking: { type: "boolean" }, progress: { type: "number" }, mode: { type: "object" }, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "supported",
    sideEffects: [],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.bake-status",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.light_bake_status",
    examples: [
      { description: "Read bake status", argv: ["tools", "run", "graphics.bake-status", "--input", "{}"] }
    ]
  },
  {
    name: "graphics.bake-clear",
    title: "Clear Baked Lighting Data",
    description: "Clear baked and/or realtime lighting data for the current scene/project. This is destructive diagnostic cleanup and requires --yes; dry-run is not supported by the bridge handler.",
    group: "graphics",
    inputSchema: objSchema({ bakedData: { type: "boolean", default: true }, realtimeData: { type: "boolean", default: true } }),
    outputSchema: objSchema({ clearedBakedData: { type: "boolean" }, clearedRealtimeData: { type: "boolean" }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema }),
    destructiveHint: true,
    playModeSupport: "editor-only",
    sideEffects: ["lightmapping-data-clear", "asset-write"],
    riskLevel: "high",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.bake-clear",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.light_bake_clear",
    examples: [
      { description: "Clear lighting data after explicit review", argv: ["tools", "run", "graphics.bake-clear", "--yes", "--input", "{\"bakedData\":true,\"realtimeData\":false}"] }
    ]
  },
  {
    name: "graphics.bake-reflection-probe",
    title: "Bake Reflection Probe",
    description: "Bake one ReflectionProbe cubemap. Requires an existing ReflectionProbe target, may write probe cubemap data, and requires --yes; use only when a real probe bake is intended.",
    group: "graphics",
    inputSchema: objSchema({ target: { type: "string", description: "ReflectionProbe GameObject name, hierarchy path, or instance id." }, path: { type: "string", description: "Optional output path for baked cubemap." } }, ["target"]),
    outputSchema: objSchema({ probe: { type: "object" }, baked: { type: "boolean" }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema }),
    destructiveHint: true,
    playModeSupport: "editor-only",
    sideEffects: ["reflection-probe-bake", "asset-write"],
    riskLevel: "medium",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.bake-reflection-probe",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.reflection_probe_bake",
    examples: [
      { description: "Bake an existing probe after approval", argv: ["tools", "run", "graphics.bake-reflection-probe", "--yes", "--input", "{\"target\":\"ReflectionProbe\"}"] }
    ]
  },
  {
    name: "graphics.bake-settings",
    title: "Light Bake Settings",
    description: "Read or update LightingSettings for the active or named scene. Passing no inputs is read-only; setting fields creates a LightingSettings object when the scene has none and marks lighting settings/scene dirty.",
    group: "graphics",
    inputSchema: objSchema({
      scene: { type: "string" },
      lightingMode: { type: "string" },
      bakedGI: { type: "boolean" },
      realtimeGI: { type: "boolean" },
      directSamples: { type: "integer" },
      indirectSamples: { type: "integer" },
      environmentSamples: { type: "integer" },
      bounces: { type: "integer" },
      filtering: { type: "string" }
    }),
    outputSchema: objSchema({ settings: { type: "object" }, scene: { type: "string" }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["lighting-settings", "scene-modify"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.bake-settings",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.light_bake_settings",
    examples: [
      { description: "Read bake settings", argv: ["tools", "run", "graphics.bake-settings", "--input", "{}"] },
      { description: "Update sample count", argv: ["tools", "run", "graphics.bake-settings", "--input", "{\"directSamples\":32}"] }
    ]
  },
  {
    name: "graphics.light-probe-group",
    title: "Light Probe Group",
    description: "Read, create, or update a LightProbeGroup component. Use hierarchy paths or instance ids when duplicate names exist; positions are a space-separated list of x,y,z triples.",
    group: "graphics",
    inputSchema: objSchema({ target: { type: "string" }, create: { type: "boolean" }, positions: { type: "string", description: "Space-separated Vector3 list, e.g. '0,0,0 1,1,1'." } }),
    outputSchema: objSchema({ target: targetOutput, probeCount: { type: "integer" }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["scene-modify", "component-add", "lighting-probes"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.light-probe-group",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.light_probe_group",
    examples: [
      { description: "Create or update probe group", argv: ["tools", "run", "graphics.light-probe-group", "--input", "{\"target\":\"ProbeRoot\",\"create\":true,\"positions\":\"0,0,0 1,1,1\"}"] }
    ]
  },
  {
    name: "graphics.reflection-probe",
    title: "Reflection Probe",
    description: "Read, create, or update a ReflectionProbe component on a scene GameObject. Use path/id targets for reliability after creating temporary probe objects.",
    group: "graphics",
    inputSchema: objSchema({
      target: { type: "string" },
      create: { type: "boolean" },
      mode: { type: "string" },
      resolution: { type: "integer" },
      bounces: { type: "integer" },
      hdr: { type: "boolean" },
      boxSize: { type: "string", description: "Vector3 as x,y,z." },
      boxOffset: { type: "string", description: "Vector3 as x,y,z." }
    }),
    outputSchema: objSchema({ target: targetOutput, probe: { type: "object" }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["scene-modify", "component-add", "reflection-probe-settings"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.reflection-probe",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.reflection_probe",
    examples: [
      { description: "Create or update reflection probe", argv: ["tools", "run", "graphics.reflection-probe", "--input", "{\"target\":\"ReflectionProbeGate\",\"create\":true,\"resolution\":64,\"hdr\":true}"] }
    ]
  },
  {
    name: "graphics.probe-positions",
    title: "Probe Positions",
    description: "Set probe positions for a LightProbeGroup or ReflectionProbe. Reflection probes use the first Vector3 as center; LightProbeGroup uses all supplied positions.",
    group: "graphics",
    inputSchema: objSchema({ target: { type: "string" }, type: { type: "string", enum: ["reflection", "light"] }, positions: { type: "string", description: "Space-separated Vector3 list." } }, ["target"]),
    outputSchema: objSchema({ target: targetOutput, probeType: { type: "string" }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["scene-modify", "probe-position-update"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.probe-positions",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.probe_positions",
    examples: [
      { description: "Set reflection probe center", argv: ["tools", "run", "graphics.probe-positions", "--input", "{\"target\":\"ReflectionProbeGate\",\"type\":\"reflection\",\"positions\":\"0,1,0\"}"] }
    ]
  },
  {
    name: "graphics.stats",
    title: "Graphics Render Stats",
    description: "Query current rendering statistics such as draw calls, batches, triangles, vertices, render textures and set-pass calls.",
    group: "graphics",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ stats: { type: "object" }, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "supported",
    sideEffects: [],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.stats",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.render_stats",
    examples: [
      { description: "Read render stats", argv: ["tools", "run", "graphics.stats", "--input", "{}"] }
    ]
  },
  {
    name: "graphics.stats-counters",
    title: "Graphics Stats Counters",
    description: "List a compact array of render stat counters for dashboards and quick diagnostics.",
    group: "graphics",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ counters: { type: "array", items: { type: "object" } }, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "supported",
    sideEffects: [],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.stats-counters",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.render_stats_counters",
    examples: [
      { description: "Read render stat counters", argv: ["tools", "run", "graphics.stats-counters", "--input", "{}"] }
    ]
  },
  {
    name: "graphics.stats-memory",
    title: "Graphics Stats Memory",
    description: "Query GPU/render memory counters including texture memory, render texture bytes and vertex buffer usage.",
    group: "graphics",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ usedTextureMemory: { type: "integer" }, renderTextureBytes: { type: "integer" }, usedVertexBuffer: { type: "integer" }, usedVertexBufferVRAM: { type: "integer" }, renderTextureCount: { type: "integer" }, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "supported",
    sideEffects: [],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.stats-memory",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.render_stats_memory",
    examples: [
      { description: "Read graphics memory stats", argv: ["tools", "run", "graphics.stats-memory", "--input", "{}"] }
    ]
  },
  {
    name: "graphics.scene-debug",
    title: "Scene Debug Mode",
    description: "Set the active SceneView shading/camera draw mode for visual debugging. This changes only the current Editor UI session and returns whether a SceneView was available.",
    group: "graphics",
    inputSchema: objSchema({ shadingMode: { type: "string" }, drawMode: { type: "string" } }),
    outputSchema: objSchema({ sceneViewAvailable: { type: "boolean" }, currentDrawMode: { type: ["string", "null"] }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["editor-scene-view"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.scene-debug",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.scene_debug",
    examples: [
      { description: "Read current SceneView debug mode", argv: ["tools", "run", "graphics.scene-debug", "--input", "{}"] },
      { description: "Set wireframe draw mode", argv: ["tools", "run", "graphics.scene-debug", "--input", "{\"drawMode\":\"Wireframe\"}"] }
    ]
  },
  {
    name: "graphics.skybox-get",
    title: "Get Skybox Settings",
    description: "Read current RenderSettings skybox material, sun, ambient, fog, and reflection settings.",
    group: "graphics",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ skybox: { type: "object" }, sun: { type: "object" }, ambient: { type: "object" }, fog: { type: "object" }, reflection: { type: "object" }, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "supported",
    sideEffects: [],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.skybox-get",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.skybox_get",
    examples: [
      { description: "Read skybox/environment settings", argv: ["tools", "run", "graphics.skybox-get", "--input", "{}"] }
    ]
  },
  {
    name: "graphics.skybox-set-material",
    title: "Set Skybox Material",
    description: "Assign an existing Assets/*.mat material as RenderSettings.skybox. Use graphics.skybox-get first to record the previous material; pass builtin:Default-Skybox to restore Unity's built-in default skybox.",
    group: "graphics",
    inputSchema: objSchema({ material: { type: "string", description: "Existing Assets/*.mat material path or builtin:Default-Skybox." } }, ["material"]),
    outputSchema: objSchema({ skybox: { type: "string" }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["render-settings", "asset-write"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.skybox-set-material",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.skybox_set_material",
    examples: [
      { description: "Assign skybox material", argv: ["tools", "run", "graphics.skybox-set-material", "--input", "{\"material\":\"Assets/Skyboxes/Day.mat\"}"] },
      { description: "Restore built-in default skybox", argv: ["tools", "run", "graphics.skybox-set-material", "--input", "{\"material\":\"builtin:Default-Skybox\"}"] }
    ]
  },
  {
    name: "graphics.skybox-set-properties",
    title: "Set Skybox Properties",
    description: "Set one named property on the currently assigned skybox material. If no skybox is assigned, first call graphics.skybox-set-material.",
    group: "graphics",
    inputSchema: objSchema({ property: { type: "string" }, value: { type: "string" } }, ["property", "value"]),
    outputSchema: objSchema({ skybox: { type: "string" }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["material-modify", "asset-write"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.skybox-set-properties",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.skybox_set_properties",
    examples: [
      { description: "Set skybox exposure", argv: ["tools", "run", "graphics.skybox-set-properties", "--input", "{\"property\":\"_Exposure\",\"value\":\"1\"}"] }
    ]
  },
  {
    name: "graphics.skybox-set-ambient",
    title: "Set Ambient Light",
    description: "Set RenderSettings ambient mode, intensity and colors for the current scene/session. Call graphics.skybox-get first if you need to restore previous values.",
    group: "graphics",
    inputSchema: objSchema({
      mode: { type: "string" },
      intensity: { type: "number" },
      color: { type: "string", description: "HTML color or comma RGBA." },
      ambientEquatorColor: { type: "string" },
      ambientGroundColor: { type: "string" },
      ambientSkyColor: { type: "string" }
    }),
    outputSchema: objSchema({ ambientMode: { type: "string" }, ambientIntensity: { type: "number" }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["render-settings", "scene-modify"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.skybox-set-ambient",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.skybox_set_ambient",
    examples: [
      { description: "Set ambient intensity", argv: ["tools", "run", "graphics.skybox-set-ambient", "--input", "{\"intensity\":1}"] }
    ]
  },
  {
    name: "graphics.skybox-set-fog",
    title: "Set Fog Settings",
    description: "Set RenderSettings fog enabled, mode, color, density and distance for the current scene/session.",
    group: "graphics",
    inputSchema: objSchema({
      enabled: { type: "boolean" },
      mode: { type: "string" },
      color: { type: "string" },
      density: { type: "number" },
      startDistance: { type: "number" },
      endDistance: { type: "number" }
    }),
    outputSchema: objSchema({ fog: { type: "boolean" }, fogMode: { type: "string" }, fogColor: { type: "string" }, fogDensity: { type: "number" }, fogStartDistance: { type: "number" }, fogEndDistance: { type: "number" }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["render-settings", "scene-modify"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.skybox-set-fog",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.skybox_set_fog",
    examples: [
      { description: "Disable fog", argv: ["tools", "run", "graphics.skybox-set-fog", "--input", "{\"enabled\":false}"] }
    ]
  },
  {
    name: "graphics.skybox-set-reflection",
    title: "Set Reflection Settings",
    description: "Set RenderSettings environment reflection mode, bounces, intensity and default resolution.",
    group: "graphics",
    inputSchema: objSchema({
      mode: { type: "string" },
      bounces: { type: "integer" },
      intensity: { type: "number" },
      resolution: { type: "string", description: "Enum name on Unity 2023.2+, numeric resolution on Unity 2022.3." }
    }),
    outputSchema: objSchema({ defaultReflectionMode: { type: "string" }, reflectionBounces: { type: "integer" }, reflectionIntensity: { type: "number" }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["render-settings", "scene-modify"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.skybox-set-reflection",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.skybox_set_reflection",
    examples: [
      { description: "Set reflection intensity", argv: ["tools", "run", "graphics.skybox-set-reflection", "--input", "{\"intensity\":1}"] }
    ]
  },
  {
    name: "graphics.skybox-set-sun",
    title: "Set Sun Source",
    description: "Set RenderSettings.sun from a GameObject with a Light component, or omit target to clear/use the current sun reference. Prefer hierarchy path or instance id when names can duplicate.",
    group: "graphics",
    inputSchema: objSchema({ target: { type: "string", description: "Optional Light GameObject name/path/id." } }),
    outputSchema: objSchema({ sun: { type: ["string", "null"] }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["render-settings", "scene-modify"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.skybox-set-sun",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.skybox_set_sun",
    examples: [
      { description: "Set sun from a Light object", argv: ["tools", "run", "graphics.skybox-set-sun", "--input", "{\"target\":\"Directional Light\"}"] }
    ]
  },
  {
    name: "graphics.pipeline-settings",
    title: "Graphics Pipeline Settings",
    description: "Read or update the default Scriptable Render Pipeline asset assignment. Pass renderPipeline:'built-in' or null to clear SRP and use the Built-in pipeline, or pass an Assets/*.asset RenderPipelineAsset path. This changes project/editor graphics settings.",
    group: "graphics",
    inputSchema: objSchema({ renderPipeline: { type: "string", description: "Assets/*.asset RenderPipelineAsset path, built-in, or null." } }),
    outputSchema: objSchema({ graphics: { type: "object" }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["render-pipeline-settings", "project-settings-write"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.pipeline-settings",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.pipeline_set_settings",
    examples: [
      { description: "Inspect pipeline assignment", argv: ["tools", "run", "graphics.pipeline-settings", "--input", "{}"] },
      { description: "Switch to Built-in pipeline", argv: ["tools", "run", "graphics.pipeline-settings", "--input", "{\"renderPipeline\":\"built-in\"}"] }
    ]
  },
  {
    name: "graphics.pipeline-quality",
    title: "Graphics Pipeline Quality",
    description: "Read or set the active QualitySettings level by numeric index or exact level name. This is a focused alias of graphics.quality for pipeline workflows.",
    group: "graphics",
    inputSchema: objSchema({ qualityLevel: { type: ["integer", "string"], description: "Quality level index or exact quality level name." }, level: { type: ["integer", "string"], description: "Alias for qualityLevel." } }),
    outputSchema: objSchema({ qualitySettings: { type: "object" }, plannedEffects: plannedEffectsSchema, logPath: logPathSchema }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["quality-settings", "project-settings-write"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.pipeline-quality",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.pipeline_set_quality",
    examples: [
      { description: "Inspect quality levels", argv: ["tools", "run", "graphics.pipeline-quality", "--input", "{}"] },
      { description: "Set quality by name", argv: ["tools", "run", "graphics.pipeline-quality", "--input", "{\"level\":\"High\"}"] }
    ]
  },
  {
    name: "graphics.pipeline-info",
    title: "Graphics Pipeline Info",
    description: "Read active/default pipeline, pipeline asset path/type, and current QualitySettings summary. Use this before graphics.settings/pipeline-settings or URP/HDRP extension tools.",
    group: "graphics",
    inputSchema: objSchema({}),
    outputSchema: graphicsReadOutput,
    destructiveHint: false,
    playModeSupport: "supported",
    sideEffects: [],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run graphics.pipeline-info",
    docsTopic: "graphics",
    equivalentUnityMcp: "manage_graphics.pipeline_get_info",
    examples: [
      { description: "Read pipeline details", argv: ["tools", "run", "graphics.pipeline-info", "--input", "{}"] }
    ]
  }
];
