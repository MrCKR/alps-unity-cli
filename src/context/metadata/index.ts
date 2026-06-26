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
  title: "Project Info",
  description: "Project metadata: name, path, Unity version, and overall project structure.",
  kind: "context",
  source: "built-in",
  category: "project",
  inputSchema: {},
  refreshBehavior: "static",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://project/info",
  outputSchema: { type: "object", properties: { projectRoot: { type: "string" }, projectName: { type: "string" }, unityVersion: { type: "string" }, platform: { type: "string" }, assetsPath: { type: "string" } } },
  equivalentUnityMcp: "mcpforunity://project/info",
  handler: "bridge",
  cliCommand: "context read project.info"
};

const PROJECT_TAGS: ContextMetadata = {
  name: "project.tags",
  title: "Project Tags",
  description: "List all tags defined in the project's Tag Manager.",
  kind: "context",
  source: "built-in",
  category: "project",
  inputSchema: {},
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://project/tags",
  outputSchema: { type: "object", properties: { tags: { type: "array", items: { type: "string" } }, count: { type: "integer" } } },
  equivalentUnityMcp: "mcpforunity://project/tags",
  handler: "bridge",
  cliCommand: "context read project.tags"
};

const PROJECT_LAYERS: ContextMetadata = {
  name: "project.layers",
  title: "Project Layers",
  description: "List all layers defined in the project's Tag Manager.",
  kind: "context",
  source: "built-in",
  category: "project",
  inputSchema: {},
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://project/layers",
  outputSchema: { type: "object", properties: { layers: { type: "array", items: { type: "object", properties: { index: { type: "integer" }, name: { type: "string" } } } }, count: { type: "integer" } } },
  equivalentUnityMcp: "mcpforunity://project/layers",
  handler: "bridge",
  cliCommand: "context read project.layers"
};

// ── Editor contexts ──

const EDITOR_STATE: ContextMetadata = {
  name: "editor.state",
  title: "Editor State",
  description: "Read-only snapshot of current Unity Editor state: Play Mode, compile/import/build busy flags, active scene, Prefab Stage, Bridge build stamp, and runtime session status. Use this before live tools to avoid acting while Unity is busy.",
  kind: "context",
  source: "built-in",
  category: "editor",
  inputSchema: {},
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://editor/state",
  outputSchema: { type: "object", properties: { schemaVersion: { type: "string" }, observedAt: { type: "string" }, state: { type: "object", properties: { playMode: { type: "object" }, busy: { type: "object" }, activeScene: { type: "object" }, prefabStage: { type: "object" }, bridge: { type: "object" }, runtime: { type: "object" } } } } },
  equivalentUnityMcp: "mcpforunity://editor/state",
  handler: "bridge",
  cliCommand: "context read editor.state"
};

const EDITOR_SELECTION: ContextMetadata = {
  name: "editor.selection",
  title: "Editor Selection",
  description: "Read the current Unity Editor selection in Hierarchy or Project Browser. Returns object/GameObject names and asset GUIDs only; it does not select, focus, ping, or modify anything.",
  kind: "context",
  source: "built-in",
  category: "editor",
  inputSchema: {},
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://editor/selection",
  outputSchema: { type: "object", properties: { activeObject: { type: ["string", "null"] }, activeGameObject: { type: ["string", "null"] }, activeTransform: { type: ["string", "null"] }, objects: { type: "array", items: { type: "string" } }, gameObjects: { type: "array", items: { type: "string" } }, assetGUIDs: { type: "array", items: { type: "string" } } } },
  equivalentUnityMcp: "mcpforunity://editor/selection",
  handler: "bridge",
  cliCommand: "context read editor.selection"
};

const EDITOR_ACTIVE_TOOL: ContextMetadata = {
  name: "editor.active-tool",
  title: "Active Tool",
  description: "Read the currently active Unity Editor transform/tool handle plus pivot mode, pivot rotation, handle position, and handle rotation. Does not change the active tool.",
  kind: "context",
  source: "built-in",
  category: "editor",
  inputSchema: {},
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://editor/active-tool",
  outputSchema: { type: "object", properties: { activeTool: { type: "string" }, isCustom: { type: "boolean" }, pivotMode: { type: "string" }, pivotRotation: { type: "string" }, handlePosition: { type: "object" }, handleRotation: { type: "object" } } },
  equivalentUnityMcp: "mcpforunity://editor/active-tool",
  handler: "bridge",
  cliCommand: "context read editor.active-tool"
};

const EDITOR_WINDOWS: ContextMetadata = {
  name: "editor.windows",
  title: "Editor Windows",
  description: "Read currently open Unity EditorWindow instances with title, type names, focused state, and instance IDs. This observes editor UI state without opening, closing, focusing, or moving windows.",
  kind: "context",
  source: "built-in",
  category: "editor",
  inputSchema: {},
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://editor/windows",
  outputSchema: { type: "object", properties: { windows: { type: "array", items: { type: "object", properties: { title: { type: "string" }, typeName: { type: "string" }, isFocused: { type: "boolean" }, position: { type: "object" }, instanceID: { type: "integer" } } } }, count: { type: "integer" } } },
  equivalentUnityMcp: "mcpforunity://editor/windows",
  handler: "bridge",
  cliCommand: "context read editor.windows"
};

const EDITOR_TOOL_STATES: ContextMetadata = {
  name: "editor.tool-states",
  title: "Tool States",
  description: "Read Unity Editor transform tool settings, pivot mode/rotation, snap settings, and grid availability. This is not MCP tool discovery; it observes editor tool configuration only.",
  kind: "context",
  source: "built-in",
  category: "editor",
  inputSchema: {},
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://editor/tool-states",
  outputSchema: { type: "object", properties: { transform: { type: "object" }, snap: { type: "object" }, gridAvailable: { type: "boolean" } } },
  equivalentUnityMcp: "none",
  equivalentUnityMcpNote: "unity-mcp 'get_tool_states' (mcpforunity://editor/tool-states) returns MCP tool discovery/enabled state (which MCP tools are available). This context returns Unity Editor tool settings (move/rotate/scale, pivot mode, snap, grid). No direct unity-mcp equivalent exists.",
  handler: "bridge",
  cliCommand: "context read editor.tool-states"
};

const PREFAB_STAGE: ContextMetadata = {
  name: "prefab.stage",
  title: "Prefab Stage",
  description: "Current Prefab Mode / Prefab Stage state if a prefab is being edited.",
  kind: "context",
  source: "built-in",
  category: "prefab",
  inputSchema: {},
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://prefab/stage",
  outputSchema: { type: "object", properties: { isOpen: { type: "boolean" }, assetPath: { type: "string" }, prefabRootName: { type: "string" }, mode: { type: "string" }, isDirty: { type: "boolean" } } },
  equivalentUnityMcp: "mcpforunity://editor/prefab-stage",
  handler: "bridge",
  cliCommand: "context read prefab.stage"
};

const EDITOR_MENU_ITEMS: ContextMetadata = {
  name: "editor.menu-items",
  title: "Menu Items",
  description: "Attempt to enumerate Unity Editor menu items as read-only discovery. Unity has no stable public API for full menu enumeration, so this context may return an empty `items` array with diagnostics; executing a menu path remains the destructive Tool `editor.menu`.",
  kind: "context",
  source: "built-in",
  category: "editor",
  inputSchema: { type: "object", properties: { search: { type: "string", description: "Optional menu path/title substring filter if a Unity version exposes enumerable menu data." } } },
  refreshBehavior: "static",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://editor/menu-items",
  outputSchema: { type: "object", properties: { items: { type: "array", items: { type: "object", properties: { path: { type: "string" }, label: { type: "string" } } } }, count: { type: "integer" }, diagnostics: { type: "string" } } },
  equivalentUnityMcp: "mcpforunity://menu-items",
  handler: "bridge",
  cliCommand: "context read editor.menu-items"
};

// ── Scene contexts ──

const SCENE_GAMEOBJECT: ContextMetadata = {
  name: "scene.gameobject",
  title: "Scene GameObject",
  description: "Read one GameObject's lightweight scene summary by id, hierarchy path, or name. Use this before go.inspect, component.get, object.get-data, or mutation tools to confirm the stable path/id. Does not modify the scene; for full component fields use component.get.",
  kind: "context",
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
  resourceUri: "unity-cli://scene/gameobject/{instance_id}",
  outputSchema: { type: "object", properties: { gameObject: { type: "object", properties: { name: { type: "string" }, path: { type: "string" }, instanceID: { type: "integer" }, active: { type: "boolean" }, components: { type: "array" } } } } },
  equivalentUnityMcp: "mcpforunity://scene/gameobject/{instance_id}",
  handler: "bridge",
  cliCommand: "context read scene.gameobject"
};

const SCENE_CAMERAS: ContextMetadata = {
  name: "scene.cameras",
  title: "Scene Cameras",
  description: "List all active scene cameras with stable GameObject path, tag, enabled state, projection, FOV/orthographic size, depth, target texture, and pixel size. Read-only camera inventory.",
  kind: "context",
  source: "built-in",
  category: "scene",
  inputSchema: {},
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://scene/cameras",
  outputSchema: { type: "object", properties: { cameras: { type: "array", items: { type: "object" } }, count: { type: "integer" } } },
  equivalentUnityMcp: "mcpforunity://scene/cameras",
  handler: "bridge",
  cliCommand: "context read scene.cameras"
};

const SCENE_VOLUMES: ContextMetadata = {
  name: "scene.volumes",
  title: "Scene Volumes",
  description: "List Volume components in the active scene with target GameObject, enabled state, global flag, priority, weight, profile reference, and pagination metadata. Returns an empty list when Volume type is unavailable.",
  kind: "context",
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
  resourceUri: "unity-cli://scene/volumes",
  outputSchema: { type: "object", properties: { volumes: { type: "array", items: { type: "object" } }, count: { type: "integer" }, pageInfo: { type: "object" } } },
  equivalentUnityMcp: "mcpforunity://scene/volumes",
  handler: "bridge",
  cliCommand: "context read scene.volumes"
};

const SCENE_RENDERER_FEATURES: ContextMetadata = {
  name: "scene.renderer-features",
  title: "Renderer Features",
  description: "Read URP renderer feature availability and configured feature names for the active/default render pipeline. If URP or a pipeline asset is unavailable, returns available=false with diagnostics and next_actions.",
  kind: "context",
  source: "built-in",
  category: "scene",
  inputSchema: {},
  refreshBehavior: "static",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://scene/renderer-features",
  outputSchema: { type: "object", properties: { available: { type: "boolean" }, renderer: { type: "string" }, features: { type: "array", items: { type: "string" } }, count: { type: "integer" }, diagnostics: { type: "string" }, next_actions: { type: "array", items: { type: "string" } } } },
  equivalentUnityMcp: "mcpforunity://pipeline/renderer-features",
  requiresPackages: ["com.unity.render-pipelines.universal"],
  handler: "bridge",
  cliCommand: "context read scene.renderer-features"
};

const SCENE_RENDERING_STATS: ContextMetadata = {
  name: "scene.rendering-stats",
  title: "Rendering Stats",
  description: "Read current rendering statistics from UnityStats when available. If Unity's internal stats API is unavailable, returns available=false with diagnostics and next_actions.",
  kind: "context",
  source: "built-in",
  category: "scene",
  inputSchema: {},
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://scene/rendering-stats",
  outputSchema: { type: "object", properties: { available: { type: "boolean" }, stats: { type: "object", properties: { batches: { type: "string" }, triangles: { type: "string" }, vertices: { type: "string" }, screenResolution: { type: "string" } } }, diagnostics: { type: "string" }, next_actions: { type: "array", items: { type: "string" } } } },
  equivalentUnityMcp: "mcpforunity://rendering/stats",
  handler: "bridge",
  cliCommand: "context read scene.rendering-stats"
};

// ── Additional scene/prefab/tool contexts ──

const SCENE_GAMEOBJECT_COMPONENTS: ContextMetadata = {
  name: "scene.gameobject.components",
  title: "Scene GameObject Components",
  description: "Lightweight list of components attached to a GameObject: type names, enabled state and optional readable property preview. Use this before component.get/component.modify to choose component name or index. Does NOT return full fields/props; for deep component data use `tools run component.get`.",
  kind: "context",
  source: "built-in",
  category: "scene",
  inputSchema: { type: "object", properties: { target: { type: "string", description: "GameObject name or hierarchy path. If ambiguous, pass path or id." }, id: { type: "integer", description: "GameObject instance ID." }, path: { type: "string", description: "Hierarchy path, e.g. Main Camera." }, includeProperties: { type: "boolean", default: true, description: "Include shallow readable properties for each component; set false for a compact type list." } } },
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  playModeSupport: "supported",
  resourceUri: "unity-cli://scene/gameobject/{instance_id}/components",
  outputSchema: { type: "object", properties: { gameObject: { type: "object" }, components: { type: "array", items: { type: "object" } }, count: { type: "integer" } } },
  equivalentUnityMcp: "mcpforunity://scene/gameobject/{id}/components",
  handler: "bridge",
  cliCommand: "context read scene.gameobject.components"
};

const SCENE_GAMEOBJECT_COMPONENT: ContextMetadata = {
  name: "scene.gameobject.component",
  title: "Scene GameObject Component",
  description: "Lightweight preview of one component attached to a GameObject. Use scene.gameobject.components first when unsure which component name exists. Does NOT replace deep reads; for complete fields/props use `tools run component.get`.",
  kind: "context",
  source: "built-in",
  category: "scene",
  inputSchema: { type: "object", properties: { target: { type: "string", description: "GameObject name or hierarchy path." }, id: { type: "integer", description: "GameObject instance ID." }, path: { type: "string", description: "Hierarchy path, e.g. Main Camera." }, component: { type: "string", description: "Component type name, e.g. Camera, Transform, or UnityEngine.Camera." } }, required: ["component"] },
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  playModeSupport: "supported",
  resourceUri: "unity-cli://scene/gameobject/{instance_id}/component/{name}",
  outputSchema: { type: "object", properties: { gameObject: { type: "object" }, component: { type: "object" } } },
  equivalentUnityMcp: "mcpforunity://scene/gameobject/{id}/component/{name}",
  handler: "bridge",
  cliCommand: "context read scene.gameobject.component"
};

const SCENE_OPENED: ContextMetadata = {
  name: "scene.opened",
  title: "Opened Scenes",
  description: "List currently loaded scenes in the Editor with name, path, build index, loaded state, dirty state, and active scene. Read-only; does not load, unload, save, or switch scenes.",
  kind: "context",
  source: "built-in",
  category: "scene",
  inputSchema: { type: "object", properties: {} },
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://scene/opened",
  outputSchema: { type: "object", properties: { scenes: { type: "array", items: { type: "object", properties: { name: { type: "string" }, path: { type: "string" }, buildIndex: { type: "integer" }, isLoaded: { type: "boolean" }, isDirty: { type: "boolean" } } } }, count: { type: "integer" }, activeScene: { type: "string" }, logPath: { type: "string" } } },
  equivalentUnityMcp: "mcpforunity://scene/opened",
  handler: "bridge",
  cliCommand: "context read scene.opened"
};

const PREFAB_INFO: ContextMetadata = {
  name: "prefab.info",
  title: "Prefab Info",
  description: "Read Prefab asset metadata, dependencies, variant information, and nested prefab references.",
  kind: "context",
  source: "built-in",
  category: "prefab",
  inputSchema: { type: "object", properties: { path: { type: "string" } }, required: ["path"] },
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://prefab/{path}",
  outputSchema: { type: "object", properties: { prefab: { type: "object" } } },
  equivalentUnityMcp: "mcpforunity://prefab/{path}",
  handler: "bridge",
  cliCommand: "context read prefab.info"
};

const PREFAB_HIERARCHY: ContextMetadata = {
  name: "prefab.hierarchy",
  title: "Prefab Hierarchy",
  description: "Read a Prefab's hierarchy without opening or modifying the Prefab Stage.",
  kind: "context",
  source: "built-in",
  category: "prefab",
  inputSchema: { type: "object", properties: { path: { type: "string" }, pageSize: { type: "integer" }, cursor: { type: "integer" } }, required: ["path"] },
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://prefab/{path}/hierarchy",
  outputSchema: { type: "object", properties: { prefab: { type: "object" }, items: { type: "array", items: { type: "object" } }, pageInfo: { type: "object" } } },
  equivalentUnityMcp: "mcpforunity://prefab/{path}/hierarchy",
  handler: "bridge",
  cliCommand: "context read prefab.hierarchy"
};

const TOOLS_GROUPS: ContextMetadata = {
  name: "tools.groups",
  title: "Tool Groups",
  description: "Read built-in and available extension tool groups with counts and discovery commands from the local CLI registry; no Unity connection is required.",
  kind: "context",
  source: "built-in",
  category: "tools",
  inputSchema: {},
  refreshBehavior: "static",
  target: "editor",
  availabilityScope: "always",
  resourceUri: "unity-cli://tools/groups",
  outputSchema: { type: "object", properties: { groups: { type: "array", items: { type: "object" } }, count: { type: "integer" } } },
  equivalentUnityMcp: "mcpforunity://tool-groups",
  handler: "offline",
  cliCommand: "context read tools.groups"
};

const TOOLS_CUSTOM: ContextMetadata = {
  name: "tools.custom",
  title: "Custom Tools",
  description: "Read custom Unity project tools discovered through [UnityCliTool].",
  kind: "context",
  source: "built-in",
  category: "tools",
  inputSchema: {},
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://tools/custom",
  outputSchema: { type: "object", properties: { tools: { type: "array", items: { type: "object" } }, count: { type: "integer" } } },
  equivalentUnityMcp: "mcpforunity://custom-tools",
  handler: "bridge",
  cliCommand: "context read tools.custom"
};

// ── Tests contexts ──

const TESTS_LIST: ContextMetadata = {
  name: "tests.list",
  title: "Test List",
  description: "List all test fixtures and test methods in the project.",
  kind: "context",
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
  resourceUri: "unity-cli://tests/list",
  outputSchema: { type: "object", properties: { available: { type: "boolean" }, tests: { type: "array", items: { type: "object" } }, count: { type: "integer" }, pageInfo: { type: "object" }, diagnostics: { type: "string" } } },
  equivalentUnityMcp: "mcpforunity://tests",
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

/** Search contexts by keyword across name, title, description, category. */
export function searchContexts(keyword: string): ContextMetadata[] {
  const lower = keyword.toLowerCase();
  return BUILTIN_CONTEXTS.filter((c) =>
    c.name.toLowerCase().includes(lower) ||
    c.title.toLowerCase().includes(lower) ||
    c.description.toLowerCase().includes(lower) ||
    c.category.toLowerCase().includes(lower)
  );
}
