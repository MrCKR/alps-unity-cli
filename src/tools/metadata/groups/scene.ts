import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const SCENE_TOOLS: ToolMetadata[] = [
  {
    name: "scene.hierarchy",
    title: "Scene Hierarchy",
    description: "Read a lightweight, paged hierarchy of active scene GameObjects. Use parent to list a subtree, maxDepth to bound recursion, nameFilter/filter to narrow results, and includeComponents only when component previews are needed. For deep object or component fields, use go.inspect, object.get-data, or component.get after selecting a stable path/id.",
    group: "scene",
    inputSchema: objSchema({
      pageSize: { type: "integer", default: 100, maximum: 500, description: "Maximum hierarchy items returned in this page." },
      cursor: { type: "integer", default: 0, description: "0-based result offset for pagination." },
      parent: { type: "string", description: "Optional parent GameObject path/name/id; returns descendants under that parent." },
      maxDepth: { type: "integer", default: 1, description: "Maximum descendant depth from roots or parent children. 0 lists only the selected level." },
      nameFilter: { type: "string", description: "Case-insensitive name/path filter." },
      includeComponents: { type: "boolean", default: false, description: "Include component type names for each item; leave false for large scenes." },
      includeInactive: { type: "boolean", default: false, description: "Include inactive GameObjects in hierarchy results." }
    }),
    outputSchema: pageSchema(objSchema({ id: { type: "integer" }, name: { type: "string" }, path: { type: "string" }, depth: { type: "integer" }, active: { type: "boolean" }, activeInHierarchy: { type: "boolean" }, tag: { type: "string" }, layer: { type: "integer" }, childCount: { type: "integer" }, componentCount: { type: "integer" }, components: { type: "array", items: { type: "string" } } })),
    destructiveHint: false,
    playModeSupport: "supported",
    requiresMainThread: true,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scene.hierarchy",
    docsTopic: "scene",
    equivalentUnityMcp: "manage_scene.get_hierarchy / scene-get-data",
    examples: [
      { description: "List a bounded hierarchy page", argv: ["tools", "run", "scene.hierarchy", "--input", "{\"pageSize\":25,\"maxDepth\":1}"] },
      { description: "Find camera nodes with component previews", argv: ["tools", "run", "scene.hierarchy", "--input", "{\"nameFilter\":\"Camera\",\"includeComponents\":true,\"pageSize\":10}"] }
    ]
  },
  {
    name: "scene.active",
    title: "Active Scene Info",
    description: "Return name, path and build index of the currently active scene. Read-only; use scene.list-opened for all loaded scenes and scene.set-active to change the active scene.",
    group: "scene",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ command: { type: "string" }, name: { type: "string" }, path: { type: "string" }, buildIndex: { type: "integer" }, isLoaded: { type: "boolean" }, isDirty: { type: "boolean" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    requiresMainThread: true,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scene.active",
    docsTopic: "scene",
    equivalentUnityMcp: "manage_scene.get_active",
    examples: [
      { description: "Get the active scene", argv: ["tools", "run", "scene.active"] }
    ]
  },
  {
    name: "scene.build-settings",
    title: "Build Settings",
    description: "List scenes registered in the Editor Build Settings (enabled scenes and their paths). Read-only; use build.scenes to add/remove scenes from build.",
    group: "scene",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ command: { type: "string" }, count: { type: "integer" }, scenes: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scene.build-settings",
    docsTopic: "scene",
    equivalentUnityMcp: "manage_scene.get_build_settings",
    examples: [
      { description: "List build settings scenes", argv: ["tools", "run", "scene.build-settings"] }
    ]
  },
  {
    name: "scene.screenshot",
    title: "Scene Screenshot",
    description: "Capture a screenshot of the active scene viewport and save to an Assets/* image path. Use screenshot.scene-view for explicit viewport control.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string", description: "Destination Assets/* image path." }, width: { type: "integer", default: 1280 }, height: { type: "integer", default: 720 }, format: { type: "string", enum: ["png", "jpg"], default: "png" }, overwrite: { type: "boolean" } }, ["path"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    riskLevel: "low",
    playModeSupport: "editor-only",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scene.screenshot",
    docsTopic: "scene",
    equivalentUnityMcp: "manage_camera.screenshot",
    examples: [
      { description: "Capture a scene screenshot", argv: ["tools", "run", "scene.screenshot", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/_scene_shot.png\"}"] }
    ]
  },
  {
    name: "scene.create",
    title: "Create Scene",
    description: "Create a new empty scene at an Assets/*.unity path and optionally load it. Refuses to overwrite an existing scene asset.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string", description: "Destination Assets/*.unity path." }, name: { type: "string", description: "Optional scene name (defaults to file name)." } }),
    destructiveHint: false,
    sideEffects: ["scene-modify", "asset-write"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scene.create",
    docsTopic: "scene",
    equivalentUnityMcp: "manage_scene.create",
    examples: [
      { description: "Create a new scene", argv: ["tools", "run", "scene.create", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/_probe.unity\"}"] }
    ]
  },
  {
    name: "scene.load",
    title: "Load Scene",
    description: "Load a scene by Assets/*.unity path or name. In Editor this opens the scene; use scene.list-opened to confirm loaded scenes. Triggers import if the scene asset is new.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string", description: "Assets/*.unity scene path." }, name: { type: "string", description: "Scene name (alternative to path)." } }),
    destructiveHint: false,
    sideEffects: ["scene-modify"],
    riskLevel: "medium",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scene.load",
    docsTopic: "scene",
    equivalentUnityMcp: "manage_scene.load",
    examples: [
      { description: "Load a scene by path", argv: ["tools", "run", "scene.load", "--input", "{\"path\":\"Assets/Scenes/Main.unity\"}"] }
    ]
  },
  {
    name: "scene.save",
    title: "Save Scene",
    description: "Save the active scene to disk. Requires --path for unsaved scenes. Destructive: writes the .unity asset. Supports --dry-run to preview.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string", description: "Assets/*.unity destination (required for unsaved scenes)." }, name: { type: "string", description: "Scene name (alternative to path)." } }),
    destructiveHint: true,
    supportsDryRun: true,
    sideEffects: ["asset-write", "scene-modify"],
    riskLevel: "medium",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scene.save",
    docsTopic: "scene",
    equivalentUnityMcp: "manage_scene.save",
    examples: [
      { description: "Save the active scene", argv: ["tools", "run", "scene.save"] }
    ]
  },
  {
    name: "scene.unload",
    title: "Unload Scene",
    description: "Unload an additively-loaded scene from the Editor. Cannot unload the only open scene. Destructive: removes the scene from the Editor session. Supports --dry-run.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string", description: "Assets/*.unity scene path to unload." }, name: { type: "string", description: "Scene name (alternative to path)." }, removeAll: { type: "boolean", description: "Unload all additively-loaded scenes." } }, ["path"]),
    destructiveHint: false,
    supportsDryRun: true,
    sideEffects: ["scene-modify"],
    riskLevel: "medium",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scene.unload",
    docsTopic: "scene",
    equivalentUnityMcp: "scene-unload",
    examples: [
      { description: "Preview unloading a scene", argv: ["tools", "run", "scene.unload", "--input", "{\"path\":\"Assets/Scenes/Level2.unity\"}", "--dry-run"] }
    ]
  },
  {
    name: "scene.set-active",
    title: "Set Active Scene",
    description: "Set the active scene among currently loaded scenes. The target scene must already be loaded (use scene.load first). Affects which scene new GameObjects are created in.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string", description: "Assets/*.unity path of a loaded scene." }, name: { type: "string", description: "Scene name (alternative to path)." } }, ["path"]),
    destructiveHint: false,
    sideEffects: ["editor-session"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scene.set-active",
    docsTopic: "scene",
    equivalentUnityMcp: "scene-set-active",
    examples: [
      { description: "Set the active scene", argv: ["tools", "run", "scene.set-active", "--input", "{\"path\":\"Assets/Scenes/Main.unity\"}"] }
    ]
  },
  {
    name: "scene.list-opened",
    title: "List Opened Scenes",
    description: "List currently loaded (opened) scenes with name, path, build index, loaded/dirty state and active flag. Read-only.",
    group: "scene",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ command: { type: "string" }, count: { type: "integer" }, activeScene: { type: "string" }, scenes: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scene.list-opened",
    docsTopic: "scene",
    equivalentUnityMcp: "manage_scene.get_loaded_scenes",
    examples: [
      { description: "List opened scenes", argv: ["tools", "run", "scene.list-opened"] }
    ]
  },
  {
    name: "scene.data",
    title: "Scene Data",
    description: "Parameterized deep read of the active scene hierarchy: maxDepth, nameFilter, includeComponents, maxResults. Read-only; for paged lightweight state use scene.hierarchy or context read scene.*.",
    group: "scene",
    inputSchema: objSchema({ maxDepth: { type: "integer", default: 2, description: "Maximum traversal depth." }, maxResults: { type: "integer", default: 200, description: "Maximum items returned." }, nameFilter: { type: "string", description: "Case-insensitive name filter." }, includeComponents: { type: "boolean", default: false, description: "Include component type names per item." } }),
    outputSchema: objSchema({ command: { type: "string" }, count: { type: "integer" }, items: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scene.data",
    docsTopic: "scene",
    equivalentUnityMcp: "scene-get-data",
    examples: [
      { description: "Read scene data with filter", argv: ["tools", "run", "scene.data", "--input", "{\"nameFilter\":\"Camera\",\"maxDepth\":2}\"}"] }
    ]
  },
  {
    name: "scene.view-frame",
    title: "Frame Scene View",
    description: "Frame the Editor SceneView on a target GameObject or frame all. Does not modify scene data, only the viewport. target resolves by name, hierarchy path or instance id.",
    group: "scene",
    inputSchema: objSchema({ target: { type: "string", description: "GameObject to frame: name, hierarchy path or instance id." }, path: { type: "string", description: "Alias of target." }, frameAll: { type: "boolean", description: "Frame all objects instead of one target." } }),
    destructiveHint: false,
    playModeSupport: "editor-only",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run scene.view-frame",
    docsTopic: "scene",
    equivalentUnityMcp: "manage_scene.scene_view_frame",
    examples: [
      { description: "Frame all objects", argv: ["tools", "run", "scene.view-frame", "--input", "{\"frameAll\":true}"] }
    ]
  }
];
