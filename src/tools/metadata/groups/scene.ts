import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const SCENE_TOOLS: ToolMetadata[] = [
  {
    name: "scene.hierarchy",
    summary: "Read a lightweight, paged hierarchy of active scene GameObjects.",
    description: "Read a lightweight, paged hierarchy of active scene GameObjects. Use parent to list a subtree, maxDepth to bound recursion, nameFilter/filter to narrow results, and includeComponents only when component previews are needed. For deep object or component fields, use go.inspect, object.get-data, or component.get after selecting a stable path/id.",
    group: "scene",
    inputSchema: objSchema({
      pageSize: { type: "integer", default: 50, maximum: 500, description: "Maximum hierarchy items returned in this page." },
      cursor: { type: "integer", default: 0, description: "0-based result offset for pagination." },
      parent: { type: "string", description: "Optional parent GameObject path/name/id; lists that parent's direct children and bounded descendants." },
      maxDepth: { type: "integer", default: 1, description: "Maximum descendant depth from roots or parent children. 0 lists only the selected level." },
      query: { type: "string", description: "Case-insensitive name/path filter alias." },
      nameFilter: { type: "string", description: "Case-insensitive name/path filter." },
      componentType: { type: "string", description: "Optional component type filter, e.g. Camera or UnityEngine.Animator." },
      includeComponents: { type: "boolean", default: false, description: "Also include legacy components arrays; componentTypes are always returned as a compact summary." },
      includeInactive: { type: "boolean", default: false, description: "Include inactive GameObjects in hierarchy results." }
    }),
    outputSchema: pageSchema(objSchema({ id: { type: "integer" }, name: { type: "string" }, path: { type: "string" }, depth: { type: "integer" }, active: { type: "boolean" }, activeInHierarchy: { type: "boolean" }, tag: { type: "string" }, layer: { type: "integer" }, childCount: { type: "integer" }, childrenTruncated: { type: "boolean" }, componentCount: { type: "integer" }, componentTypes: { type: "array", items: { type: "string" } }, components: { type: "array", items: { type: "string" } } })),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "List a bounded hierarchy page", argv: ["tools", "run", "scene.hierarchy", "--input", "{\"pageSize\":25,\"maxDepth\":1}"] },
      { description: "Find camera nodes by component type", argv: ["tools", "run", "scene.hierarchy", "--input", "{\"componentType\":\"Camera\",\"pageSize\":10}"] }
    ]
  },
  {
    name: "scene.active",
    summary: "Return name, path and build index of the currently active scene.",
    description: "Return name, path and build index of the currently active scene. Read-only; use scene.list-opened for all loaded scenes and scene.set-active to change the active scene.",
    group: "scene",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ command: { type: "string" }, name: { type: "string" }, path: { type: "string" }, buildIndex: { type: "integer" }, isLoaded: { type: "boolean" }, isDirty: { type: "boolean" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Get the active scene", argv: ["tools", "run", "scene.active"] }
    ]
  },
  {
    name: "scene.build-settings",
    summary: "List scenes registered in the Editor Build Settings (enabled scenes and their paths).",
    description: "List scenes registered in the Editor Build Settings (enabled scenes and their paths). Read-only; use build.scenes to add/remove scenes from build.",
    group: "scene",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ command: { type: "string" }, count: { type: "integer" }, scenes: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "List build settings scenes", argv: ["tools", "run", "scene.build-settings"] }
    ]
  },
  {
    name: "scene.screenshot",
    summary: "Compatibility alias for active scene viewport screenshots; prefer screenshot.scene-view for explicit source.",
    description: "Compatibility alias that captures the active scene viewport and saves to an Assets/* image path. Prefer screenshot.scene-view for explicit SceneView source control, or screenshot.game-view for Game View output.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string", description: "Destination Assets/* image path." }, width: { type: "integer", default: 1280 }, height: { type: "integer", default: 720 }, format: { type: "string", enum: ["png", "jpg"], default: "png" }, overwrite: { type: "boolean" } }, ["path"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    playModeSupport: "editor-only",
    examples: [
      { description: "Capture a scene screenshot", argv: ["tools", "run", "scene.screenshot", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/_scene_shot.png\"}"] }
    ]
  },
  {
    name: "scene.create",
    summary: "Create a new empty scene at an Assets/*.unity path and optionally load it.",
    description: "Create a new empty scene at an Assets/*.unity path and optionally load it. Refuses to overwrite an existing scene asset.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string", description: "Destination Assets/*.unity path." }, name: { type: "string", description: "Optional scene name (defaults to file name)." } }),
    destructiveHint: false,
    sideEffects: ["scene-modify", "asset-write"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Create a new scene", argv: ["tools", "run", "scene.create", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/_probe.unity\"}"] }
    ]
  },
  {
    name: "scene.load",
    summary: "Load a scene by Assets/*.unity path or name.",
    description: "Load a scene by Assets/*.unity path or name. In Editor this opens the scene; use scene.list-opened to confirm loaded scenes. Triggers import if the scene asset is new.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string", description: "Assets/*.unity scene path." }, name: { type: "string", description: "Scene name (alternative to path)." } }),
    destructiveHint: false,
    sideEffects: ["scene-modify"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Load a scene by path", argv: ["tools", "run", "scene.load", "--input", "{\"path\":\"Assets/Scenes/Main.unity\"}"] }
    ]
  },
  {
    name: "scene.save",
    summary: "Save the active scene to disk.",
    description: "Save the active scene to disk. Requires --path for unsaved scenes. Destructive: writes the .unity asset. Supports --dry-run to preview.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string", description: "Assets/*.unity destination (required for unsaved scenes)." }, name: { type: "string", description: "Scene name (alternative to path)." } }),
    destructiveHint: true,
    supportsDryRun: true,
    sideEffects: ["asset-write", "scene-modify"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Save the active scene", argv: ["tools", "run", "scene.save"] }
    ]
  },
  {
    name: "scene.unload",
    summary: "Unload an additively-loaded scene from the Editor.",
    description: "Unload an additively-loaded scene from the Editor. Cannot unload the only open scene. Destructive: removes the scene from the Editor session. Supports --dry-run.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string", description: "Assets/*.unity scene path to unload." }, name: { type: "string", description: "Scene name (alternative to path)." }, removeAll: { type: "boolean", description: "Unload all additively-loaded scenes." } }, ["path"]),
    destructiveHint: false,
    supportsDryRun: true,
    sideEffects: ["scene-modify"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Preview unloading a scene", argv: ["tools", "run", "scene.unload", "--input", "{\"path\":\"Assets/Scenes/Level2.unity\"}", "--dry-run"] }
    ]
  },
  {
    name: "scene.set-active",
    summary: "Set the active scene among currently loaded scenes.",
    description: "Set the active scene among currently loaded scenes. The target scene must already be loaded (use scene.load first). Affects which scene new GameObjects are created in.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string", description: "Assets/*.unity path of a loaded scene." }, name: { type: "string", description: "Scene name (alternative to path)." } }, ["path"]),
    destructiveHint: false,
    sideEffects: ["editor-session"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Set the active scene", argv: ["tools", "run", "scene.set-active", "--input", "{\"path\":\"Assets/Scenes/Main.unity\"}"] }
    ]
  },
  {
    name: "scene.list-opened",
    summary: "List currently loaded (opened) scenes with name, path, build index, loaded/dirty state and active flag.",
    description: "List currently loaded (opened) scenes with name, path, build index, loaded/dirty state and active flag. Read-only.",
    group: "scene",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ command: { type: "string" }, count: { type: "integer" }, activeScene: { type: "string" }, scenes: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "List opened scenes", argv: ["tools", "run", "scene.list-opened"] }
    ]
  },
  {
    name: "scene.data",
    summary: "Deep-read active scene hierarchy with maxDepth/nameFilter; prefer scene.hierarchy for paged navigation.",
    description: "Parameterized deep read of the active scene hierarchy: maxDepth, nameFilter, includeComponents, maxResults. Read-only; for paged lightweight state use scene.hierarchy or context read scene.*.",
    group: "scene",
    inputSchema: objSchema({ maxDepth: { type: "integer", default: 2, description: "Maximum traversal depth." }, maxResults: { type: "integer", default: 200, description: "Maximum items returned." }, nameFilter: { type: "string", description: "Case-insensitive name filter." }, includeComponents: { type: "boolean", default: false, description: "Include component type names per item." } }),
    outputSchema: objSchema({ command: { type: "string" }, count: { type: "integer" }, items: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Read scene data with filter", argv: ["tools", "run", "scene.data", "--input", "{\"nameFilter\":\"Camera\",\"maxDepth\":2}\"}"] }
    ]
  },
  {
    name: "scene.move-object",
    summary: "Move a GameObject from its current scene into another already-loaded scene.",
    description: "Move a GameObject (resolved by target path or instanceId) into another already-loaded destination scene (--scene path). Useful for multi-scene editing. Requires the destination scene to be loaded (scene.load); returns moved=false if the object is already in the destination.",
    group: "scene",
    inputSchema: objSchema({ target: { type: "string", description: "GameObject name or hierarchy path to move." }, path: { type: "string", description: "Alias of target." }, instanceId: { type: "integer", description: "Instance id of the GameObject." }, scene: { type: "string", description: "Destination scene asset path (must be loaded). Required." } }, ["scene"]),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    supportsDryRun: true,
    examples: [
      { description: "Move an object to another scene", argv: ["tools", "run", "scene.move-object", "--input", "{\"target\":\"Player\",\"scene\":\"Assets/Scenes/Gameplay.unity\"}"] }
    ]
  },
  {
    name: "scene.validate",
    summary: "Validate a scene for missing scripts, null references, and Build Settings inclusion.",
    description: "Validate the active scene (or --path scene) for common issues: missing script components (null components from missing assemblies), null references, and whether the scene is included in Editor Build Settings scenes. Returns valid flag, issue counts, and next_actions to fix.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string", description: "Scene asset path to validate; defaults to the active scene." } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Validate the active scene", argv: ["tools", "run", "scene.validate", "--input", "{}"] }
    ]
  },
  {
    name: "scene.view-frame",
    summary: "Frame the Editor SceneView on a target GameObject or frame all.",
    description: "Frame the Editor SceneView on a target GameObject or frame all. Does not modify scene data, only the viewport. target resolves by name, hierarchy path or instance id.",
    group: "scene",
    inputSchema: objSchema({ target: { type: "string", description: "GameObject to frame: name, hierarchy path or instance id." }, path: { type: "string", description: "Alias of target." }, frameAll: { type: "boolean", description: "Frame all objects instead of one target." } }),
    destructiveHint: false,
    playModeSupport: "editor-only",
    examples: [
      { description: "Frame all objects", argv: ["tools", "run", "scene.view-frame", "--input", "{\"frameAll\":true}"] }
    ]
  }
];
