/**
 * Static built-in context metadata for the Unity CLI.
 *
 * Built-in contexts map Unity project and editor state into read-only descriptors.
 *
 * Context is always read-only. If a capability has side effects,
 * it must be a Tool, not a Context.
 */

import type { ContextMetadata, ContextSource } from "./types.js";

export type { ContextMetadata, ContextSource } from "./types.js";

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
  description: "Current editor state: compilation state, play mode, and general editor status.",
  kind: "context",
  source: "built-in",
  category: "editor",
  inputSchema: {},
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://editor/state",
  outputSchema: { type: "object", properties: { playMode: { type: "string" }, busy: { type: "boolean" }, activeScene: { type: "string" }, prefabStage: { type: "object" }, bridge: { type: "object" }, runtime: { type: "object" }, schemaVersion: { type: "string" }, observedAt: { type: "string" }, availability: { type: "object" }, next_actions: { type: "array" } } },
  equivalentUnityMcp: "mcpforunity://editor/state",
  handler: "bridge",
  cliCommand: "context read editor.state"
};

const EDITOR_SELECTION: ContextMetadata = {
  name: "editor.selection",
  title: "Editor Selection",
  description: "Currently selected objects in the Unity Editor hierarchy or project browser.",
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
  description: "Currently active editor tool (move, rotate, scale, rect, etc.).",
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
  description: "List currently open editor windows and their types.",
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
  description: "Current state of editor tools (enabled/disabled, snap, grid settings).",
  kind: "context",
  source: "built-in",
  category: "editor",
  inputSchema: {},
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://editor/tool-states",
  outputSchema: { type: "object", properties: { transform: { type: "object" }, snap: { type: "object" }, grid: { type: "object" }, activeTool: { type: "string" } } },
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
  description: "Enumerate available menu items in the Unity Editor.",
  kind: "context",
  source: "built-in",
  category: "editor",
  inputSchema: { type: "object", properties: { search: { type: "string", description: "Optional filter substring." } } },
  refreshBehavior: "static",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://editor/menu-items",
  outputSchema: { type: "object", properties: { items: { type: "array", items: { type: "object", properties: { path: { type: "string" }, label: { type: "string" } } } }, count: { type: "integer" } } },
  equivalentUnityMcp: "mcpforunity://menu-items",
  handler: "bridge",
  cliCommand: "context read editor.menu-items"
};

// ── Scene contexts ──

const SCENE_GAMEOBJECT: ContextMetadata = {
  name: "scene.gameobject",
  title: "Scene GameObject",
  description: "Read a specific GameObject's properties from the active scene.",
  kind: "context",
  source: "built-in",
  category: "scene",
  inputSchema: {
    type: "object",
    properties: {
      target: { type: "string", description: "GameObject name or path." },
      id: { type: "integer", description: "GameObject instance ID (takes priority over target/path)." },
      path: { type: "string", description: "Hierarchy path, e.g. 'Main Camera/Directional Light'." },
      includeComponents: { type: "boolean", description: "Include component summaries in the output." }
    }
  },
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://scene/gameobject/{instance_id}",
  outputSchema: { type: "object", properties: { gameObject: { type: "object", properties: { name: { type: "string" }, path: { type: "string" }, instanceID: { type: "integer" }, active: { type: "boolean" }, components: { type: "array" } } } } },
  equivalentUnityMcp: "mcpforunity://scene/gameobject/{instance_id}",
  handler: "bridge",
  cliCommand: "context read scene.gameobject"
};

const SCENE_CAMERAS: ContextMetadata = {
  name: "scene.cameras",
  title: "Scene Cameras",
  description: "List all cameras in the active scene with their key properties.",
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
  description: "List all volume components in the active scene.",
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
  description: "List URP renderer features configured in the project.",
  kind: "context",
  source: "built-in",
  category: "scene",
  inputSchema: {},
  refreshBehavior: "static",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://scene/renderer-features",
  outputSchema: { type: "object", properties: { available: { type: "boolean" }, renderer: { type: "object" }, features: { type: "array", items: { type: "object" } }, diagnostics: { type: "string" } } },
  equivalentUnityMcp: "mcpforunity://pipeline/renderer-features",
  requiresPackages: ["com.unity.render-pipelines.universal"],
  handler: "bridge",
  cliCommand: "context read scene.renderer-features"
};

const SCENE_RENDERING_STATS: ContextMetadata = {
  name: "scene.rendering-stats",
  title: "Rendering Stats",
  description: "Current rendering statistics from the active scene camera.",
  kind: "context",
  source: "built-in",
  category: "scene",
  inputSchema: {},
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://scene/rendering-stats",
  outputSchema: { type: "object", properties: { available: { type: "boolean" }, stats: { type: "object" }, diagnostics: { type: "string" } } },
  equivalentUnityMcp: "mcpforunity://rendering/stats",
  handler: "bridge",
  cliCommand: "context read scene.rendering-stats"
};

// ── Additional scene/prefab/tool contexts ──

const SCENE_GAMEOBJECT_COMPONENTS: ContextMetadata = {
  name: "scene.gameobject.components",
  title: "Scene GameObject Components",
  description: "Lightweight list of components attached to a GameObject: type names, enabled state and a short summary. Does NOT return full fields/props. For deep component data use `tools run component.get`.",
  kind: "context",
  source: "built-in",
  category: "scene",
  inputSchema: { type: "object", properties: { target: { type: "string" }, id: { type: "integer" }, path: { type: "string" }, includeProperties: { type: "boolean" } } },
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://scene/gameobject/{instance_id}/components",
  outputSchema: { type: "object", properties: { gameObject: { type: "object" }, components: { type: "array", items: { type: "object" } }, count: { type: "integer" } } },
  equivalentUnityMcp: "mcpforunity://scene/gameobject/{id}/components",
  handler: "bridge",
  cliCommand: "context read scene.gameobject.components"
};

const SCENE_GAMEOBJECT_COMPONENT: ContextMetadata = {
  name: "scene.gameobject.component",
  title: "Scene GameObject Component",
  description: "Lightweight preview of one component attached to a GameObject. Does NOT replace deep reads. For complete fields/props use `tools run component.get`.",
  kind: "context",
  source: "built-in",
  category: "scene",
  inputSchema: { type: "object", properties: { target: { type: "string" }, id: { type: "integer" }, path: { type: "string" }, component: { type: "string" } }, required: ["component"] },
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://scene/gameobject/{instance_id}/component/{name}",
  outputSchema: { type: "object", properties: { gameObject: { type: "object" }, component: { type: "object" } } },
  equivalentUnityMcp: "mcpforunity://scene/gameobject/{id}/component/{name}",
  handler: "bridge",
  cliCommand: "context read scene.gameobject.component"
};

const SCENE_OPENED: ContextMetadata = {
  name: "scene.opened",
  title: "Opened Scenes",
  description: "List currently loaded (opened) scenes in the Editor, with name, path, build index, isLoaded and isDirty. Read-only state surface for multi-scene observation.",
  kind: "context",
  source: "built-in",
  category: "scene",
  inputSchema: { type: "object", properties: {} },
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  resourceUri: "unity-cli://scene/opened",
  outputSchema: { type: "object", properties: { scenes: { type: "array", items: { type: "object", properties: { name: { type: "string" }, path: { type: "string" }, buildIndex: { type: "integer" }, isLoaded: { type: "boolean" }, isDirty: { type: "boolean" } } } }, count: { type: "integer" }, activeScene: { type: "string" } } },
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
  description: "Read built-in and available extension tool groups with counts and discovery commands.",
  kind: "context",
  source: "built-in",
  category: "tools",
  inputSchema: {},
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "always",
  resourceUri: "unity-cli://tools/groups",
  outputSchema: { type: "object", properties: { groups: { type: "array", items: { type: "object" } }, count: { type: "integer" } } },
  equivalentUnityMcp: "mcpforunity://tool-groups",
  handler: "bridge",
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

/** Return all built-in context metadata entries. */
export function builtinContextMetadata(): ContextMetadata[] {
  return BUILTIN_CONTEXTS;
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
