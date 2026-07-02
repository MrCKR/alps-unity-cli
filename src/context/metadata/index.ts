/**
 * Static built-in context metadata for the Unity CLI.
 *
 * Built-in contexts map Unity project and editor state into read-only descriptors.
 *
 * Context is always read-only. If a capability has side effects,
 * it must be a Tool, not a Context.
 */

import type { ContextMetadata, ContextPlayModeSupport, ContextSource } from "./types.js";

export type { ContextMetadata, ContextPlayModeSupport, ContextSource } from "./types.js";

// ── Project contexts ──

const PROJECT_INFO: ContextMetadata = {
  name: "project.info",
  description: "Project metadata: name, path, Unity version, and overall project structure.",
  source: "built-in",
  category: "project",
  inputSchema: {},
  refreshBehavior: "static",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: { type: "object", properties: { projectRoot: { type: "string" }, projectName: { type: "string" }, unityVersion: { type: "string" }, platform: { type: "string" }, assetsPath: { type: "string" } } },
  handler: "bridge",
  cliCommand: "context read project.info"
};

const PROJECT_TAGS: ContextMetadata = {
  name: "project.tags",
  description: "List all tags defined in the project's Tag Manager.",
  source: "built-in",
  category: "project",
  inputSchema: {},
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: { type: "object", properties: { tags: { type: "array", items: { type: "string" } }, count: { type: "integer" } } },
  handler: "bridge",
  cliCommand: "context read project.tags"
};

const PROJECT_LAYERS: ContextMetadata = {
  name: "project.layers",
  description: "List all layers defined in the project's Tag Manager.",
  source: "built-in",
  category: "project",
  inputSchema: {},
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: { type: "object", properties: { layers: { type: "array", items: { type: "object", properties: { index: { type: "integer" }, name: { type: "string" } } } }, count: { type: "integer" } } },
  handler: "bridge",
  cliCommand: "context read project.layers"
};

// ── Editor contexts ──

const EDITOR_STATE: ContextMetadata = {
  name: "editor.state",
  description: "Read-only snapshot of current Unity Editor state: Play Mode, compile/import/build busy flags, active scene, Prefab Stage, Bridge build stamp, and runtime session status. Use this before live tools to avoid acting while Unity is busy. Pass waitReady=true to block on the main thread until Unity is no longer busy, reducing AI polling round-trips; on timeout it returns the latest snapshot with timedOut=true.",
  source: "built-in",
  category: "editor",
  inputSchema: { type: "object", properties: { waitReady: { type: "boolean", description: "If true, block on the Unity main thread until the editor is no longer busy (compile/import/build/playmode/domain-reload), then return the idle snapshot. Defaults to false." }, timeoutMs: { type: "integer", default: 60000, description: "Maximum milliseconds to wait when waitReady=true. On timeout the latest snapshot is returned with timedOut=true." } } },
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: { type: "object", properties: { schemaVersion: { type: "string" }, observedAt: { type: "string" }, timedOut: { type: "boolean" }, diagnostics: { type: "string" }, state: { type: "object", properties: { playMode: { type: "object" }, busy: { type: "object" }, activeScene: { type: "object" }, prefabStage: { type: "object" }, bridge: { type: "object" }, runtime: { type: "object" } } } } },
  handler: "bridge",
  cliCommand: "context read editor.state"
};

const EDITOR_SELECTION: ContextMetadata = {
  name: "editor.selection",
  description: "Read the current Unity Editor selection in Hierarchy or Project Browser. Returns legacy name arrays plus reusable object details: instanceId, type, asset path/GUID, hierarchy path, GameObject reference, transform, and component summaries. It does not select, focus, ping, or modify anything.",
  source: "built-in",
  category: "editor",
  inputSchema: {},
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: {
    type: "object",
    properties: {
      activeObject: { type: ["string", "null"] },
      activeGameObject: { type: ["string", "null"] },
      activeTransform: { type: ["string", "null"] },
      activeObjectDetail: { type: ["object", "null"] },
      activeGameObjectDetail: { type: ["object", "null"] },
      activeTransformDetail: { type: ["object", "null"] },
      objects: { type: "array", items: { type: ["string", "null"] } },
      objectDetails: { type: "array", items: { type: ["object", "null"] } },
      gameObjects: { type: "array", items: { type: "string" } },
      gameObjectDetails: { type: "array", items: { type: "object" } },
      assetGUIDs: { type: "array", items: { type: ["string", "null"] } }
    }
  },
  handler: "bridge",
  cliCommand: "context read editor.selection"
};

const EDITOR_ACTIVE_TOOL: ContextMetadata = {
  name: "editor.active-tool",
  description: "Read the currently active Unity Editor transform/tool handle plus pivot mode, pivot rotation, handle position, and handle rotation. Does not change the active tool.",
  source: "built-in",
  category: "editor",
  inputSchema: {},
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: { type: "object", properties: { activeTool: { type: "string" }, isCustom: { type: "boolean" }, pivotMode: { type: "string" }, pivotRotation: { type: "string" }, handlePosition: { type: "object" }, handleRotation: { type: "object" } } },
  handler: "bridge",
  cliCommand: "context read editor.active-tool"
};

const EDITOR_WINDOWS: ContextMetadata = {
  name: "editor.windows",
  description: "Read currently open Unity EditorWindow instances with title, type names, focused state, and instance IDs. This observes editor UI state without opening, closing, focusing, or moving windows.",
  source: "built-in",
  category: "editor",
  inputSchema: {},
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: { type: "object", properties: { windows: { type: "array", items: { type: "object", properties: { title: { type: "string" }, typeName: { type: "string" }, isFocused: { type: "boolean" }, position: { type: "object" }, instanceID: { type: "integer" } } } }, count: { type: "integer" } } },
  handler: "bridge",
  cliCommand: "context read editor.windows"
};

const EDITOR_TOOL_STATES: ContextMetadata = {
  name: "editor.tool-states",
  description: "Read Unity Editor transform tool settings, pivot mode/rotation, snap settings, and grid availability. This is not MCP tool discovery; it observes editor tool configuration only.",
  source: "built-in",
  category: "editor",
  inputSchema: {},
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: { type: "object", properties: { transform: { type: "object" }, snap: { type: "object" }, gridAvailable: { type: "boolean" } } },
  handler: "bridge",
  cliCommand: "context read editor.tool-states"
};

const PREFAB_STAGE: ContextMetadata = {
  name: "prefab.stage",
  description: "Read current Prefab editing stage state: whether a prefab is being edited, its path, and dirty status. This observes Prefab Stage only and does not open, save, close, or modify it.",
  source: "built-in",
  category: "prefab",
  inputSchema: {},
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: { type: "object", properties: { isOpen: { type: "boolean" }, assetPath: { type: "string" }, prefabRootName: { type: "string" }, mode: { type: "string" }, isDirty: { type: "boolean" } } },
  handler: "bridge",
  cliCommand: "context read prefab.stage"
};

const EDITOR_MENU_ITEMS: ContextMetadata = {
  name: "editor.menu-items",
  description: "Attempt to enumerate Unity Editor menu items as read-only discovery. Unity has no stable public API for full menu enumeration, so this context may return an empty `items` array with diagnostics; executing a menu path remains the destructive Tool `editor.menu`.",
  source: "built-in",
  category: "editor",
  inputSchema: { type: "object", properties: { search: { type: "string", description: "Optional menu path/title substring filter if a Unity version exposes enumerable menu data." } } },
  refreshBehavior: "static",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: { type: "object", properties: { items: { type: "array", items: { type: "object", properties: { path: { type: "string" }, label: { type: "string" } } } }, count: { type: "integer" }, diagnostics: { type: "string" } } },
  handler: "bridge",
  cliCommand: "context read editor.menu-items"
};

// ── Scene contexts ──

const SCENE_GAMEOBJECT: ContextMetadata = {
  name: "scene.gameobject",
  description: "Read one GameObject's lightweight scene summary by id, hierarchy path, or name. Use this before go.inspect, component.get, object.get-data, or mutation tools to confirm the stable path/id. Does not modify the scene; for full component fields use component.get.",
  source: "built-in",
  category: "scene",
  inputSchema: {
    type: "object",
    properties: {
      target: { type: "string", description: "GameObject name or hierarchy path. If multiple objects match, pass path or id." },
      id: { type: "integer", description: "GameObject instance ID (takes priority over target/path)." },
      path: { type: "string", description: "Hierarchy path, e.g. 'Canvas/Button' or 'Main Camera'." },
      includeComponents: { type: "boolean", default: true, description: "Include lightweight component summaries. Set false when you only need the GameObject reference." }
    }
  },
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  playModeSupport: "supported",
  outputSchema: { type: "object", properties: { gameObject: { type: "object", properties: { name: { type: "string" }, path: { type: "string" }, instanceID: { type: "integer" }, active: { type: "boolean" }, components: { type: "array" } } } } },
  handler: "bridge",
  cliCommand: "context read scene.gameobject"
};

const SCENE_CAMERAS: ContextMetadata = {
  name: "scene.cameras",
  description: "List all active scene cameras with stable GameObject path, tag, enabled state, projection, FOV/orthographic size, depth, target texture, and pixel size. Read-only camera inventory.",
  source: "built-in",
  category: "scene",
  inputSchema: {},
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: { type: "object", properties: { cameras: { type: "array", items: { type: "object" } }, count: { type: "integer" } } },
  handler: "bridge",
  cliCommand: "context read scene.cameras"
};

const SCENE_VOLUMES: ContextMetadata = {
  name: "scene.volumes",
  description: "List Volume components in the active scene with target GameObject, enabled state, global flag, priority, weight, profile reference, and pagination metadata. Returns an empty list when Volume type is unavailable.",
  source: "built-in",
  category: "scene",
  inputSchema: {
    type: "object",
    properties: {
      target: { type: "string", description: "Optional volume target filter." },
      pageSize: { type: "integer", description: "Maximum number of volumes per page." },
      cursor: { type: "string", description: "Opaque pagination cursor." }
    }
  },
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: { type: "object", properties: { volumes: { type: "array", items: { type: "object" } }, count: { type: "integer" }, pageInfo: { type: "object" } } },
  handler: "bridge",
  cliCommand: "context read scene.volumes"
};

const SCENE_RENDERER_FEATURES: ContextMetadata = {
  name: "scene.renderer-features",
  description: "Read URP renderer feature availability and configured feature names for the active/default render pipeline. If URP or a pipeline asset is unavailable, returns available=false with diagnostics and next_actions.",
  source: "built-in",
  category: "scene",
  inputSchema: {},
  refreshBehavior: "static",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: { type: "object", properties: { available: { type: "boolean" }, renderer: { type: "string" }, features: { type: "array", items: { type: "string" } }, count: { type: "integer" }, diagnostics: { type: "string" }, next_actions: { type: "array", items: { type: "string" } } } },
  requiresPackages: ["com.unity.render-pipelines.universal"],
  handler: "bridge",
  cliCommand: "context read scene.renderer-features"
};

const SCENE_RENDERING_STATS: ContextMetadata = {
  name: "scene.rendering-stats",
  description: "Read current rendering statistics from UnityStats when available. If Unity's internal stats API is unavailable, returns available=false with diagnostics and next_actions.",
  source: "built-in",
  category: "scene",
  inputSchema: {},
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: { type: "object", properties: { available: { type: "boolean" }, stats: { type: "object", properties: { batches: { type: "string" }, triangles: { type: "string" }, vertices: { type: "string" }, screenResolution: { type: "string" } } }, diagnostics: { type: "string" }, next_actions: { type: "array", items: { type: "string" } } } },
  handler: "bridge",
  cliCommand: "context read scene.rendering-stats"
};

// ── Additional scene/prefab/tool contexts ──

const SCENE_GAMEOBJECT_COMPONENTS: ContextMetadata = {
  name: "scene.gameobject.components",
  description: "Lightweight list of components attached to a GameObject: type names, enabled state and optional readable property preview. Use this before component.get/component.modify to choose component name or index. Does NOT return full fields/props; for deep component data use `tools run component.get`.",
  source: "built-in",
  category: "scene",
  inputSchema: { type: "object", properties: { target: { type: "string", description: "GameObject name or hierarchy path. If ambiguous, pass path or id." }, id: { type: "integer", description: "GameObject instance ID." }, path: { type: "string", description: "Hierarchy path, e.g. Main Camera." }, includeProperties: { type: "boolean", default: true, description: "Include shallow readable properties for each component; set false for a compact type list." } } },
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  playModeSupport: "supported",
  outputSchema: { type: "object", properties: { gameObject: { type: "object" }, components: { type: "array", items: { type: "object" } }, count: { type: "integer" } } },
  handler: "bridge",
  cliCommand: "context read scene.gameobject.components"
};

const SCENE_GAMEOBJECT_COMPONENT: ContextMetadata = {
  name: "scene.gameobject.component",
  description: "Lightweight preview of one component attached to a GameObject. Use scene.gameobject.components first when unsure which component name exists. Does NOT replace deep reads; for complete fields/props use `tools run component.get`.",
  source: "built-in",
  category: "scene",
  inputSchema: { type: "object", properties: { target: { type: "string", description: "GameObject name or hierarchy path." }, id: { type: "integer", description: "GameObject instance ID." }, path: { type: "string", description: "Hierarchy path, e.g. Main Camera." }, component: { type: "string", description: "Component type name, e.g. Camera, Transform, or UnityEngine.Camera." } }, required: ["component"] },
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  playModeSupport: "supported",
  outputSchema: { type: "object", properties: { gameObject: { type: "object" }, component: { type: "object" } } },
  handler: "bridge",
  cliCommand: "context read scene.gameobject.component"
};

const SCENE_OPENED: ContextMetadata = {
  name: "scene.opened",
  description: "List currently loaded scenes in the Editor with name, path, build index, loaded state, dirty state, and active scene. Read-only; does not load, unload, save, or switch scenes.",
  source: "built-in",
  category: "scene",
  inputSchema: { type: "object", properties: {} },
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: { type: "object", properties: { scenes: { type: "array", items: { type: "object", properties: { name: { type: "string" }, path: { type: "string" }, buildIndex: { type: "integer" }, isLoaded: { type: "boolean" }, isDirty: { type: "boolean" } } } }, count: { type: "integer" }, activeScene: { type: "string" }, logPath: { type: "string" } } },
  handler: "bridge",
  cliCommand: "context read scene.opened"
};

const PREFAB_INFO: ContextMetadata = {
  name: "prefab.info",
  description: "Read Prefab asset metadata, dependencies, variant information, and nested prefab references.",
  source: "built-in",
  category: "prefab",
  inputSchema: { type: "object", properties: { path: { type: "string" } }, required: ["path"] },
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: { type: "object", properties: { prefab: { type: "object" } } },
  handler: "bridge",
  cliCommand: "context read prefab.info"
};

const PREFAB_HIERARCHY: ContextMetadata = {
  name: "prefab.hierarchy",
  description: "Read a Prefab's hierarchy without opening or modifying the Prefab Stage.",
  source: "built-in",
  category: "prefab",
  inputSchema: { type: "object", properties: { path: { type: "string" }, pageSize: { type: "integer" }, cursor: { type: "integer" } }, required: ["path"] },
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: { type: "object", properties: { prefab: { type: "object" }, items: { type: "array", items: { type: "object" } }, pageInfo: { type: "object" } } },
  handler: "bridge",
  cliCommand: "context read prefab.hierarchy"
};

const TOOLS_GROUPS: ContextMetadata = {
  name: "tools.groups",
  description: "Read built-in and available extension tool groups with counts and discovery commands from the local CLI registry; no Unity connection is required.",
  source: "built-in",
  category: "tools",
  inputSchema: {},
  refreshBehavior: "static",
  target: "editor",
  availabilityScope: "always",
  outputSchema: { type: "object", properties: { groups: { type: "array", items: { type: "object" } }, count: { type: "integer" } } },
  handler: "offline",
  cliCommand: "context read tools.groups"
};

const TOOLS_CUSTOM: ContextMetadata = {
  name: "tools.custom",
  description: "Read custom Unity project tools discovered through [UnityCliTool].",
  source: "built-in",
  category: "tools",
  inputSchema: {},
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: { type: "object", properties: { tools: { type: "array", items: { type: "object" } }, count: { type: "integer" } } },
  handler: "bridge",
  cliCommand: "context read tools.custom"
};

// ── Tests contexts ──

const TESTS_LIST: ContextMetadata = {
  name: "tests.list",
  description: "List all test fixtures and test methods in the project.",
  source: "built-in",
  category: "tests",
  inputSchema: {
    type: "object",
    properties: {
      mode: { type: "string", enum: ["EditMode", "PlayMode"], description: "Test platform filter." },
      pageSize: { type: "integer", description: "Maximum number of tests per page." },
      cursor: { type: "string", description: "Opaque pagination cursor." }
    }
  },
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  outputSchema: { type: "object", properties: { available: { type: "boolean" }, tests: { type: "array", items: { type: "object" } }, count: { type: "integer" }, pageInfo: { type: "object" }, diagnostics: { type: "string" } } },
  handler: "bridge",
  cliCommand: "context read tests.list"
};

// ── All built-in contexts ──

const BUILTIN_CONTEXTS: ContextMetadata[] = [
  PROJECT_INFO,
  PROJECT_TAGS,
  PROJECT_LAYERS,
  EDITOR_STATE,
  EDITOR_SELECTION,
  EDITOR_ACTIVE_TOOL,
  EDITOR_WINDOWS,
  EDITOR_TOOL_STATES,
  PREFAB_STAGE,
  EDITOR_MENU_ITEMS,
  SCENE_GAMEOBJECT,
  SCENE_GAMEOBJECT_COMPONENTS,
  SCENE_GAMEOBJECT_COMPONENT,
  SCENE_OPENED,
  SCENE_CAMERAS,
  SCENE_VOLUMES,
  SCENE_RENDERER_FEATURES,
  SCENE_RENDERING_STATS,
  PREFAB_INFO,
  PREFAB_HIERARCHY,
  TESTS_LIST,
  TOOLS_GROUPS,
  TOOLS_CUSTOM
];

/** 推导 Context 的 Play Mode 读取语义；Context 保持只读，不产生 Unity 副作用。 */
export function inferContextPlayModeSupport(context: ContextMetadata): ContextPlayModeSupport {
  if (context.target === "runtime" || context.handler === "bridge-runtime") return "runtime-only";
  if (context.availabilityScope === "always") return "supported";
  return "supported";
}

/** Normalize a context metadata entry to include runtime defaults. */
export function normalizeContextMetadata(context: ContextMetadata): ContextMetadata & { playModeSupport: ContextPlayModeSupport } {
  return {
    ...context,
    playModeSupport: context.playModeSupport ?? inferContextPlayModeSupport(context)
  };
}

/** Return all built-in context metadata entries. */
export function builtinContextMetadata(): ContextMetadata[] {
  return BUILTIN_CONTEXTS.map(normalizeContextMetadata);
}

/** Return distinct context category names from built-in metadata. */
export function builtinContextCategories(): string[] {
  const categories = new Set<string>();
  for (const ctx of BUILTIN_CONTEXTS) {
    categories.add(ctx.category);
  }
  return [...categories].sort();
}

/** Find a single built-in context by its dotted name. */
export function findContextByName(name: string): ContextMetadata | undefined {
  return BUILTIN_CONTEXTS.find((c) => c.name === name);
}

/** Filter contexts by category. */
export function filterContextsByCategory(category: string): ContextMetadata[] {
  return BUILTIN_CONTEXTS.filter((c) => c.category === category);
}

/** Search contexts by keyword across name, description, category. */
export function searchContexts(keyword: string): ContextMetadata[] {
  const lower = keyword.toLowerCase();
  return BUILTIN_CONTEXTS.filter((c) =>
    c.name.toLowerCase().includes(lower) ||
    c.description.toLowerCase().includes(lower) ||
    c.category.toLowerCase().includes(lower)
  );
}
