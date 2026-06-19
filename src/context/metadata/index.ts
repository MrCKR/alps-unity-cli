/**
 * Static built-in context metadata for the Unity CLI.
 *
 * P1 baseline: skeleton registry with placeholder built-in contexts
 * that map to unity-mcp Resources. P2 will populate C# handlers.
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
  readOnlyHint: true,
  refreshBehavior: "static",
  target: "editor",
  availabilityScope: "editorSession",
  equivalentUnityMcp: "project/info",
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
  readOnlyHint: true,
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  equivalentUnityMcp: "project/tags",
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
  readOnlyHint: true,
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  equivalentUnityMcp: "project/layers",
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
  readOnlyHint: true,
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  equivalentUnityMcp: "editor/state",
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
  readOnlyHint: true,
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  equivalentUnityMcp: "editor/selection",
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
  readOnlyHint: true,
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  equivalentUnityMcp: "editor/active-tool",
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
  readOnlyHint: true,
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  equivalentUnityMcp: "editor/windows",
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
  readOnlyHint: true,
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  equivalentUnityMcp: "editor/tool-states",
  handler: "bridge",
  cliCommand: "context read editor.tool-states"
};

const EDITOR_PREFAB_STAGE: ContextMetadata = {
  name: "editor.prefab-stage",
  title: "Prefab Stage",
  description: "Current Prefab Mode / Prefab Stage state if a prefab is being edited.",
  kind: "context",
  source: "built-in",
  category: "editor",
  inputSchema: {},
  readOnlyHint: true,
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  equivalentUnityMcp: "editor/prefab-stage",
  handler: "bridge",
  cliCommand: "context read editor.prefab-stage"
};

const EDITOR_MENU_ITEMS: ContextMetadata = {
  name: "editor.menu-items",
  title: "Menu Items",
  description: "Enumerate available menu items in the Unity Editor.",
  kind: "context",
  source: "built-in",
  category: "editor",
  inputSchema: { search: { type: "string", description: "Optional filter substring." } },
  readOnlyHint: true,
  refreshBehavior: "static",
  target: "editor",
  availabilityScope: "editorSession",
  equivalentUnityMcp: "editor/menu-items",
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
  inputSchema: { target: { type: "string", description: "GameObject name or path." } },
  readOnlyHint: true,
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  equivalentUnityMcp: "scene/gameobject",
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
  readOnlyHint: true,
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  equivalentUnityMcp: "scene/cameras",
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
  inputSchema: {},
  readOnlyHint: true,
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  equivalentUnityMcp: "scene/volumes",
  handler: "bridge",
  cliCommand: "context read scene.volumes"
};

const SCENE_RENDERER_FEATURES: ContextMetadata = {
  name: "scene.renderer-features",
  title: "Renderer Features",
  description: "List URP renderer features configured in the project.",
  kind: "context",
  source: "extension",
  category: "scene",
  inputSchema: {},
  readOnlyHint: true,
  refreshBehavior: "static",
  target: "editor",
  availabilityScope: "editorSession",
  equivalentUnityMcp: "scene/renderer-features",
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
  readOnlyHint: true,
  refreshBehavior: "live",
  target: "editor",
  availabilityScope: "editorSession",
  equivalentUnityMcp: "scene/rendering-stats",
  handler: "bridge",
  cliCommand: "context read scene.rendering-stats"
};

// ── Tests contexts ──

const TESTS_LIST: ContextMetadata = {
  name: "tests.list",
  title: "Test List",
  description: "List all test fixtures and test methods in the project.",
  kind: "context",
  source: "built-in",
  category: "tests",
  inputSchema: { platform: { type: "string", enum: ["EditMode", "PlayMode"], description: "Test platform filter." } },
  readOnlyHint: true,
  refreshBehavior: "on-demand",
  target: "editor",
  availabilityScope: "editorSession",
  equivalentUnityMcp: "tests",
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
  EDITOR_PREFAB_STAGE,
  EDITOR_MENU_ITEMS,
  SCENE_GAMEOBJECT,
  SCENE_CAMERAS,
  SCENE_VOLUMES,
  SCENE_RENDERER_FEATURES,
  SCENE_RENDERING_STATS,
  TESTS_LIST
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
