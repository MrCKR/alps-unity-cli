/**
 * Built-in context descriptors for the Unity CLI.
 *
 * Covers the built-in contexts aligned with unity-mcp reference project Resources:
 *   project/info, project/tags, project/layers,
 *   editor/state, editor/selection, editor/active-tool, editor/windows,
 *   editor/tool-states, editor/menu-items, prefab.stage,
 *   scene/gameobject, scene/gameobject/components, scene/gameobject/component,
 *   scene/cameras, scene/volumes, scene/renderer-features,
 *   scene/rendering-stats, prefab/info, prefab/hierarchy,
 *   tests/list, instances/list, tools/groups, tools/custom
 *
 * All contexts are read-only (readOnlyHint=true, destructiveHint=false).
 * Context reads are routed through the "context.read" bridge command.
 */

import type { ContextDescriptor } from "./types.js";

const CONTEXT_COMMON = {
  source: "built-in" as const,
  target: "editor" as const,
  availabilityScope: "editorSession" as const,
  requiresUnity: true,
  readOnlyHint: true as const,
  destructiveHint: false as const,
  bridgeCommand: "context.read",
  available: true,
};

// ── Project ──────────────────────────────────────────────────────────────

const PROJECT_INFO: ContextDescriptor = {
  name: "project.info",
  title: "Project Info",
  description: "Basic project metadata: name, root path, Unity version, platform, and assets path.",
  category: "project",
  cliCommand: "context read project.info",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      projectRoot: { type: "string", description: "Absolute path to the project root." },
      projectName: { type: "string", description: "Project name derived from the root directory." },
      unityVersion: { type: "string", description: "Unity Editor version string." },
      platform: { type: "string", description: "Active build target platform." },
      assetsPath: { type: "string", description: "Path to the Assets folder." }
    },
    required: ["projectRoot", "projectName", "unityVersion"]
  },
  docsTopic: "project",
  ...CONTEXT_COMMON,
};

const PROJECT_TAGS: ContextDescriptor = {
  name: "project.tags",
  title: "Project Tags",
  description: "All tags defined in the project's Tag Manager.",
  category: "project",
  cliCommand: "context read project.tags",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      tags: { type: "array", items: { type: "string" }, description: "List of tag names." }
    },
    required: ["tags"]
  },
  docsTopic: "project",
  ...CONTEXT_COMMON,
};

const PROJECT_LAYERS: ContextDescriptor = {
  name: "project.layers",
  title: "Project Layers",
  description: "All layers defined in the project's Tag Manager, including built-in and user layers.",
  category: "project",
  cliCommand: "context read project.layers",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      layers: {
        type: "array",
        items: {
          type: "object",
          properties: {
            index: { type: "integer", description: "Layer index (0-31)." },
            name: { type: "string", description: "Layer name." }
          },
          required: ["index", "name"]
        },
        description: "List of layer entries."
      }
    },
    required: ["layers"]
  },
  docsTopic: "project",
  ...CONTEXT_COMMON,
};

// ── Editor ───────────────────────────────────────────────────────────────

const EDITOR_STATE: ContextDescriptor = {
  name: "editor.state",
  title: "Editor State",
  description: "Comprehensive editor state snapshot: play mode, compilation, asset import, domain reload status, and activity.",
  category: "editor",
  cliCommand: "context read editor.state",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      isPlaying: { type: "boolean", description: "Whether the editor is in Play Mode." },
      isCompiling: { type: "boolean", description: "Whether scripts are currently compiling." },
      isImporting: { type: "boolean", description: "Whether assets are being imported." },
      isDomainReloading: { type: "boolean", description: "Whether a domain reload is in progress." },
      isPaused: { type: "boolean", description: "Whether Play Mode is paused." },
      currentScene: { type: "string", description: "Path of the currently active scene." },
      unityVersion: { type: "string", description: "Unity Editor version." }
    },
    required: ["isPlaying", "isCompiling"]
  },
  docsTopic: "editor",
  ...CONTEXT_COMMON,
};

const EDITOR_SELECTION: ContextDescriptor = {
  name: "editor.selection",
  title: "Editor Selection",
  description: "Currently selected objects in the editor: active GameObject, selection count, and selected object paths.",
  category: "editor",
  cliCommand: "context read editor.selection",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      activeObject: { type: "string", description: "Name of the actively selected object." },
      activeGameObject: { type: "string", description: "Name of the active GameObject." },
      count: { type: "integer", description: "Number of selected objects." },
      objects: { type: "array", items: { type: "string" }, description: "Names of all selected objects." }
    },
    required: ["count"]
  },
  docsTopic: "editor",
  ...CONTEXT_COMMON,
};

const EDITOR_ACTIVE_TOOL: ContextDescriptor = {
  name: "editor.active-tool",
  title: "Editor Active Tool",
  description: "The currently active editor tool (e.g. Move, Rotate, Scale, Rect, Custom).",
  category: "editor",
  cliCommand: "context read editor.active-tool",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      toolName: { type: "string", description: "Name of the active tool." },
      toolType: { type: "string", description: "Tool type identifier." }
    },
    required: ["toolName"]
  },
  docsTopic: "editor",
  ...CONTEXT_COMMON,
};

const EDITOR_WINDOWS: ContextDescriptor = {
  name: "editor.windows",
  title: "Editor Windows",
  description: "List of currently open editor windows with their types and titles.",
  category: "editor",
  cliCommand: "context read editor.windows",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      windows: {
        type: "array",
        items: {
          type: "object",
          properties: {
            title: { type: "string", description: "Window title." },
            type: { type: "string", description: "Window class type name." }
          },
          required: ["title", "type"]
        },
        description: "List of open editor windows."
      }
    },
    required: ["windows"]
  },
  docsTopic: "editor",
  ...CONTEXT_COMMON,
};

const EDITOR_TOOL_STATES: ContextDescriptor = {
  name: "editor.tool-states",
  title: "Editor Tool States",
  description: "State of all editor tools: active tool, tool mode, and available tool options.",
  category: "editor",
  cliCommand: "context read editor.tool-states",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      activeTool: { type: "string", description: "Name of the active tool." },
      tools: {
        type: "array",
        items: {
          type: "object",
          properties: {
            name: { type: "string" },
            active: { type: "boolean" },
            available: { type: "boolean" }
          },
          required: ["name", "active"]
        },
        description: "State of each editor tool."
      }
    },
    required: ["activeTool"]
  },
  docsTopic: "editor",
  ...CONTEXT_COMMON,
};

const EDITOR_MENU_ITEMS: ContextDescriptor = {
  name: "editor.menu-items",
  title: "Editor Menu Items",
  description: "Available Unity editor menu items and their paths, for discovery of editor functionality.",
  category: "menu",
  cliCommand: "context read editor.menu-items",
  inputSchema: {
    type: "object",
    properties: {
      filter: { type: "string", description: "Filter menu items by path substring." }
    }
  },
  outputSchema: {
    type: "object",
    properties: {
      items: {
        type: "array",
        items: {
          type: "object",
          properties: {
            path: { type: "string", description: "Full menu path, e.g. 'File/Save Scene'." },
            label: { type: "string", description: "Menu item label." },
            validated: { type: "boolean", description: "Whether the menu item is currently enabled." }
          },
          required: ["path", "label"]
        },
        description: "List of menu items."
      }
    },
    required: ["items"]
  },
  docsTopic: "editor",
  ...CONTEXT_COMMON,
};

const EDITOR_PREFAB_STAGE: ContextDescriptor = {
  name: "prefab.stage",
  title: "Prefab Stage",
  description: "Current Prefab editing stage state: whether a prefab is being edited, its path, and dirty status.",
  category: "prefab",
  cliCommand: "context read prefab.stage",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      isOpen: { type: "boolean", description: "Whether a prefab stage is currently open." },
      prefabPath: { type: "string", description: "Asset path of the prefab being edited." },
      isDirty: { type: "boolean", description: "Whether the prefab has unsaved changes." }
    },
    required: ["isOpen"]
  },
  docsTopic: "prefab",
  ...CONTEXT_COMMON,
};

// ── Scene ────────────────────────────────────────────────────────────────

const SCENE_GAMEOBJECT: ContextDescriptor = {
  name: "scene.gameobject",
  title: "Scene GameObject",
  description: "GameObject(s) in the active scene. Supports querying by instance ID or getting the hierarchy root objects.",
  category: "scene",
  cliCommand: "context read scene.gameobject",
  inputSchema: {
    type: "object",
    properties: {
      instanceId: { type: "integer", description: "Instance ID of a specific GameObject." },
      includeComponents: { type: "boolean", description: "Include component type list in output." }
    }
  },
  outputSchema: {
    type: "object",
    properties: {
      name: { type: "string", description: "GameObject name." },
      instanceId: { type: "integer", description: "Instance ID." },
      tag: { type: "string", description: "Tag name." },
      layer: { type: "integer", description: "Layer index." },
      active: { type: "boolean", description: "Whether the GameObject is active." },
      path: { type: "string", description: "Hierarchy path." },
      children: { type: "array", items: { type: "integer" }, description: "Child instance IDs." },
      componentTypes: { type: "array", items: { type: "string" }, description: "Component type names." }
    },
    required: ["name", "instanceId", "active"]
  },
  docsTopic: "scene",
  ...CONTEXT_COMMON,
};

const SCENE_GAMEOBJECT_COMPONENTS: ContextDescriptor = {
  name: "scene.gameobject.components",
  title: "Scene GameObject Components",
  description: "Lightweight list of components attached to a GameObject: type names, enabled state and a short summary. Does NOT return full fields/props. For deep component data use `tools run component.get`.",
  category: "scene",
  cliCommand: "context read scene.gameobject.components",
  inputSchema: {
    type: "object",
    properties: {
      target: { type: "string", description: "GameObject name or hierarchy path." },
      id: { type: "integer", description: "GameObject instance ID." },
      path: { type: "string", description: "Hierarchy path, e.g. 'Canvas/Button'." },
      includeProperties: { type: "boolean", description: "Include readable public component properties." }
    }
  },
  outputSchema: {
    type: "object",
    properties: {
      gameObject: { type: "object", description: "Resolved GameObject summary." },
      components: { type: "array", items: { type: "object" }, description: "Attached component summaries." },
      count: { type: "integer", description: "Number of components." }
    },
    required: ["gameObject", "components", "count"]
  },
  docsTopic: "scene",
  resourceUri: "unity-cli://scene/gameobject/{instance_id}/components",
  equivalentUnityMcp: "mcpforunity://scene/gameobject/{id}/components",
  ...CONTEXT_COMMON,
};

const SCENE_GAMEOBJECT_COMPONENT: ContextDescriptor = {
  name: "scene.gameobject.component",
  title: "Scene GameObject Component",
  description: "Lightweight preview of one component attached to a GameObject. Does NOT replace deep reads. For complete fields/props use `tools run component.get`.",
  category: "scene",
  cliCommand: "context read scene.gameobject.component",
  inputSchema: {
    type: "object",
    properties: {
      target: { type: "string", description: "GameObject name or hierarchy path." },
      id: { type: "integer", description: "GameObject instance ID." },
      path: { type: "string", description: "Hierarchy path, e.g. 'Canvas/Button'." },
      component: { type: "string", description: "Component type name, e.g. Camera or UnityEngine.Camera." }
    },
    required: ["component"]
  },
  outputSchema: {
    type: "object",
    properties: {
      gameObject: { type: "object", description: "Resolved GameObject summary." },
      component: { type: "object", description: "Component summary and readable properties." }
    },
    required: ["gameObject", "component"]
  },
  docsTopic: "scene",
  resourceUri: "unity-cli://scene/gameobject/{instance_id}/component/{name}",
  equivalentUnityMcp: "mcpforunity://scene/gameobject/{id}/component/{name}",
  ...CONTEXT_COMMON,
};

const SCENE_OPENED: ContextDescriptor = {
  name: "scene.opened",
  title: "Opened Scenes",
  description: "List currently loaded (opened) scenes in the Editor, with name, path, build index, isLoaded and isDirty. Read-only state surface for multi-scene observation.",
  category: "scene",
  cliCommand: "context read scene.opened",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      scenes: {
        type: "array",
        items: {
          type: "object",
          properties: {
            name: { type: "string" },
            path: { type: "string" },
            buildIndex: { type: "integer" },
            isLoaded: { type: "boolean" },
            isDirty: { type: "boolean" }
          }
        }
      },
      count: { type: "integer" },
      activeScene: { type: "string" }
    },
    required: ["scenes", "count"]
  },
  docsTopic: "scene",
  resourceUri: "unity-cli://scene/opened",
  equivalentUnityMcp: "mcpforunity://scene/opened",
  ...CONTEXT_COMMON,
};

const SCENE_CAMERAS: ContextDescriptor = {
  name: "scene.cameras",
  title: "Scene Cameras",
  description: "All cameras in the active scene with their properties and positions.",
  category: "scene",
  cliCommand: "context read scene.cameras",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      cameras: {
        type: "array",
        items: {
          type: "object",
          properties: {
            name: { type: "string", description: "Camera GameObject name." },
            instanceId: { type: "integer", description: "Instance ID." },
            isMain: { type: "boolean", description: "Whether this is the main camera." },
            enabled: { type: "boolean", description: "Whether the camera component is enabled." }
          },
          required: ["name", "instanceId"]
        },
        description: "List of cameras in the scene."
      }
    },
    required: ["cameras"]
  },
  docsTopic: "scene",
  ...CONTEXT_COMMON,
};

const SCENE_VOLUMES: ContextDescriptor = {
  name: "scene.volumes",
  title: "Scene Volumes",
  description: "Volume components in the active scene (URP/HDRP), their profiles and priorities.",
  category: "scene",
  cliCommand: "context read scene.volumes",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      volumes: {
        type: "array",
        items: {
          type: "object",
          properties: {
            name: { type: "string", description: "Volume GameObject name." },
            priority: { type: "number", description: "Volume priority." },
            weight: { type: "number", description: "Volume blend weight." },
            isGlobal: { type: "boolean", description: "Whether the volume is global." },
            profilePath: { type: "string", description: "Asset path of the volume profile." }
          },
          required: ["name"]
        },
        description: "List of volumes."
      }
    },
    required: ["volumes"]
  },
  docsTopic: "graphics",
  ...CONTEXT_COMMON,
};

const SCENE_RENDERER_FEATURES: ContextDescriptor = {
  name: "scene.renderer-features",
  title: "Renderer Features",
  description: "URP renderer features and their enabled states for the active render pipeline asset.",
  category: "scene",
  cliCommand: "context read scene.renderer-features",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      rendererName: { type: "string", description: "Name of the URP renderer." },
      features: {
        type: "array",
        items: {
          type: "object",
          properties: {
            name: { type: "string", description: "Feature name." },
            type: { type: "string", description: "Feature class type name." },
            active: { type: "boolean", description: "Whether the feature is enabled." }
          },
          required: ["name", "type", "active"]
        },
        description: "List of renderer features."
      }
    },
    required: ["features"]
  },
  docsTopic: "graphics",
  ...CONTEXT_COMMON,
};

const SCENE_RENDERING_STATS: ContextDescriptor = {
  name: "scene.rendering-stats",
  title: "Rendering Stats",
  description: "Current rendering statistics: draw calls, triangles, vertices, and frame timing.",
  category: "scene",
  cliCommand: "context read scene.rendering-stats",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      drawCalls: { type: "integer", description: "Number of draw calls." },
      triangles: { type: "integer", description: "Number of triangles rendered." },
      vertices: { type: "integer", description: "Number of vertices rendered." },
      fps: { type: "number", description: "Current frames per second." },
      usedTextureMemory: { type: "number", description: "Texture memory usage in bytes." },
      usedHeapSize: { type: "number", description: "Heap size in bytes." }
    },
    required: ["drawCalls", "triangles", "vertices"]
  },
  docsTopic: "graphics",
  ...CONTEXT_COMMON,
};

// ── Prefab ───────────────────────────────────────────────────────────────

const PREFAB_INFO: ContextDescriptor = {
  name: "prefab.info",
  title: "Prefab Info",
  description: "Read Prefab asset metadata, dependencies, variant information, and nested prefab references.",
  category: "prefab",
  cliCommand: "context read prefab.info",
  inputSchema: {
    type: "object",
    properties: {
      path: { type: "string", description: "Assets/*.prefab path." }
    },
    required: ["path"]
  },
  outputSchema: {
    type: "object",
    properties: {
      prefab: { type: "object", description: "Prefab asset summary." }
    },
    required: ["prefab"]
  },
  docsTopic: "prefab",
  resourceUri: "unity-cli://prefab/{path}",
  equivalentUnityMcp: "mcpforunity://prefab/{path}",
  ...CONTEXT_COMMON,
};

const PREFAB_HIERARCHY: ContextDescriptor = {
  name: "prefab.hierarchy",
  title: "Prefab Hierarchy",
  description: "Read a Prefab's hierarchy without opening or modifying the Prefab Stage.",
  category: "prefab",
  cliCommand: "context read prefab.hierarchy",
  inputSchema: {
    type: "object",
    properties: {
      path: { type: "string", description: "Assets/*.prefab path." },
      pageSize: { type: "integer", description: "Maximum items per page." },
      cursor: { type: "integer", description: "Pagination cursor." }
    },
    required: ["path"]
  },
  outputSchema: {
    type: "object",
    properties: {
      prefab: { type: "object", description: "Prefab asset summary." },
      items: { type: "array", items: { type: "object" }, description: "Hierarchy item summaries." },
      pageInfo: { type: "object", description: "Pagination metadata." }
    },
    required: ["prefab", "items", "pageInfo"]
  },
  docsTopic: "prefab",
  resourceUri: "unity-cli://prefab/{path}/hierarchy",
  equivalentUnityMcp: "mcpforunity://prefab/{path}/hierarchy",
  ...CONTEXT_COMMON,
};

// ── Tools ────────────────────────────────────────────────────────────────

const TOOLS_GROUPS: ContextDescriptor = {
  name: "tools.groups",
  title: "Tool Groups",
  description: "Read built-in and available extension tool groups with counts and discovery commands.",
  category: "tools",
  cliCommand: "context read tools.groups",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      groups: { type: "array", items: { type: "object" }, description: "Tool groups." },
      count: { type: "integer", description: "Number of groups." }
    },
    required: ["groups", "count"]
  },
  docsTopic: "tools",
  resourceUri: "unity-cli://tools/groups",
  equivalentUnityMcp: "mcpforunity://tool-groups",
  ...CONTEXT_COMMON,
  requiresUnity: false,
  availabilityScope: "always",
};

const TOOLS_CUSTOM: ContextDescriptor = {
  name: "tools.custom",
  title: "Custom Tools",
  description: "Read custom Unity project tools discovered through [UnityCliTool].",
  category: "tools",
  cliCommand: "context read tools.custom",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      tools: { type: "array", items: { type: "object" }, description: "Project custom tools." },
      count: { type: "integer", description: "Number of custom tools." }
    },
    required: ["tools", "count"]
  },
  docsTopic: "custom-tools",
  resourceUri: "unity-cli://tools/custom",
  equivalentUnityMcp: "mcpforunity://custom-tools",
  ...CONTEXT_COMMON,
};

// ── Tests ────────────────────────────────────────────────────────────────

const TESTS_LIST: ContextDescriptor = {
  name: "tests.list",
  title: "Test List",
  description: "Available test assemblies, classes, and methods for EditMode and PlayMode tests.",
  category: "tests",
  cliCommand: "context read tests.list",
  inputSchema: {
    type: "object",
    properties: {
      mode: { type: "string", enum: ["EditMode", "PlayMode"], description: "Test mode filter." },
      pageSize: { type: "integer", description: "Maximum items per page." },
      cursor: { type: "integer", description: "Pagination cursor." }
    }
  },
  outputSchema: {
    type: "object",
    properties: {
      items: {
        type: "array",
        items: {
          type: "object",
          properties: {
            assembly: { type: "string", description: "Test assembly name." },
            className: { type: "string", description: "Test class name." },
            methodName: { type: "string", description: "Test method name." },
            mode: { type: "string", enum: ["EditMode", "PlayMode"], description: "Test mode." }
          },
          required: ["assembly", "className", "methodName", "mode"]
        }
      },
      totalCount: { type: "integer", description: "Total number of tests." },
      cursor: { type: "integer", description: "Current page cursor." },
      hasMore: { type: "boolean", description: "Whether more results are available." }
    },
    required: ["items", "totalCount"]
  },
  docsTopic: "test",
  ...CONTEXT_COMMON,
};

// ── Aggregate ────────────────────────────────────────────────────────────

export const BUILTIN_CONTEXTS: ContextDescriptor[] = [
  PROJECT_INFO,
  PROJECT_TAGS,
  PROJECT_LAYERS,
  EDITOR_STATE,
  EDITOR_SELECTION,
  EDITOR_ACTIVE_TOOL,
  EDITOR_WINDOWS,
  EDITOR_TOOL_STATES,
  EDITOR_MENU_ITEMS,
  EDITOR_PREFAB_STAGE,
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
  TOOLS_CUSTOM,
];
