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
 * Context reads are routed through the "context.read" bridge command.
 */

import type { ContextDescriptor } from "./types.js";

const CONTEXT_COMMON = {
  source: "built-in" as const,
  target: "editor" as const,
  availabilityScope: "editorSession" as const,
  playModeSupport: "supported" as const,
  available: true,
};

// ── Project ──────────────────────────────────────────────────────────────

const PROJECT_INFO: ContextDescriptor = {
  name: "project.info",
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
  ...CONTEXT_COMMON,
};

const PROJECT_TAGS: ContextDescriptor = {
  name: "project.tags",
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
  ...CONTEXT_COMMON,
};

const PROJECT_LAYERS: ContextDescriptor = {
  name: "project.layers",
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
        description: "List of layer entries.",
      }
    },
    required: ["layers"]
  },
  ...CONTEXT_COMMON,
};

// ── Editor ───────────────────────────────────────────────────────────────

const EDITOR_STATE: ContextDescriptor = {
  name: "editor.state",
  description: "Read-only snapshot of current Unity Editor state: Play Mode, compile/import/build busy flags, active scene, Prefab Stage, Bridge build stamp, and runtime session status. Use this before live tools to avoid acting while Unity is busy. Pass waitReady=true to block on the main thread until Unity is no longer busy (compiling/updating/building/playmode-switching/domain-reloading), reducing AI polling round-trips; on timeout it returns the latest snapshot with timedOut=true instead of erroring.",
  category: "editor",
  cliCommand: "context read editor.state",
  inputSchema: {
    type: "object",
    properties: {
      waitReady: { type: "boolean", description: "If true, block on the Unity main thread until the editor is no longer busy (compile/import/build/playmode/domain-reload), then return the idle snapshot. Defaults to false (immediate snapshot)." },
      timeoutMs: { type: "integer", default: 60000, description: "Maximum milliseconds to wait when waitReady=true. On timeout the latest snapshot is returned with timedOut=true." }
    }
  },
  outputSchema: {
    type: "object",
    properties: {
      schemaVersion: { type: "string", description: "Context schema version." },
      observedAt: { type: "string", description: "UTC observation timestamp." },
      timedOut: { type: "boolean", description: "Present and true only when waitReady timed out before reaching idle; the returned state is the latest snapshot." },
      diagnostics: { type: "string", description: "Present only when timedOut=true, explaining the wait-ready timeout." },
      state: {
        type: "object",
        properties: {
          playMode: { type: "object", description: "Play Mode state." },
          busy: { type: "object", description: "Compilation/import/build/domain reload busy flags." },
          activeScene: { type: "object", description: "Active scene summary." },
          prefabStage: { type: "object", description: "Current Prefab Stage summary." },
          bridge: { type: "object", description: "Bridge build/runtime metadata." },
          runtime: { type: "object", description: "Runtime Play Mode session state." }
        }
      }
    },
    required: ["state"]
  },
  ...CONTEXT_COMMON,
};

const EDITOR_SELECTION: ContextDescriptor = {
  name: "editor.selection",
  description: "Read the current Unity Editor selection in Hierarchy or Project Browser. Returns legacy name arrays plus reusable object details: instanceId, type, asset path/GUID, hierarchy path, GameObject reference, transform, and component summaries. It does not select, focus, ping, or modify anything.",
  category: "editor",
  cliCommand: "context read editor.selection",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      activeObject: { type: ["string", "null"], description: "Name of the actively selected object." },
      activeGameObject: { type: ["string", "null"], description: "Name of the active GameObject." },
      activeTransform: { type: ["string", "null"], description: "Name of the active Transform, if any." },
      activeObjectDetail: { type: ["object", "null"], description: "Reusable detail for the active object: instanceId, type, asset path/GUID, hierarchy path, GameObject reference, transform, and component summaries when applicable." },
      activeGameObjectDetail: { type: ["object", "null"], description: "Reusable detail for the active GameObject." },
      activeTransformDetail: { type: ["object", "null"], description: "Reusable detail for the active Transform." },
      objects: { type: "array", items: { type: ["string", "null"] }, description: "Names of all selected objects, preserved for compatibility." },
      objectDetails: { type: "array", items: { type: ["object", "null"] }, description: "Detail records for all selected objects." },
      gameObjects: { type: "array", items: { type: "string" }, description: "Names of selected GameObjects, preserved for compatibility." },
      gameObjectDetails: { type: "array", items: { type: "object" }, description: "Detail records for selected GameObjects." },
      assetGUIDs: { type: "array", items: { type: ["string", "null"] }, description: "Asset GUIDs for selected project assets; scene objects return null." }
    },
    required: ["objects", "objectDetails", "gameObjects", "gameObjectDetails", "assetGUIDs"]
  },
  ...CONTEXT_COMMON,
};

const EDITOR_ACTIVE_TOOL: ContextDescriptor = {
  name: "editor.active-tool",
  description: "Read the currently active Unity Editor transform/tool handle plus pivot mode, pivot rotation, handle position, and handle rotation. Does not change the active tool.",
  category: "editor",
  cliCommand: "context read editor.active-tool",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      activeTool: { type: "string", description: "Name of the active Unity Editor tool." },
      isCustom: { type: "boolean", description: "Whether the active tool is a custom tool." },
      pivotMode: { type: "string", description: "Current pivot mode." },
      pivotRotation: { type: "string", description: "Current pivot rotation mode." },
      handlePosition: { type: "object", description: "Current handle position." },
      handleRotation: { type: "object", description: "Current handle rotation." }
    },
    required: ["activeTool", "pivotMode", "pivotRotation"]
  },
  ...CONTEXT_COMMON,
};

const EDITOR_WINDOWS: ContextDescriptor = {
  name: "editor.windows",
  description: "Read currently open Unity EditorWindow instances with title, type names, focused state, and instance IDs. This observes editor UI state without opening, closing, focusing, or moving windows.",
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
            typeName: { type: "string", description: "Window class type name." },
            fullName: { type: "string", description: "Full window class name." },
            isFocused: { type: "boolean", description: "Whether this window is currently focused." },
            instanceID: { type: "integer", description: "Unity instance ID for the EditorWindow." }
          },
          required: ["title", "typeName"]
        },
        description: "List of open editor windows.",
      }
    },
    required: ["windows"]
  },
  ...CONTEXT_COMMON,
};

const EDITOR_TOOL_STATES: ContextDescriptor = {
  name: "editor.tool-states",
  description: "Read Unity Editor transform tool settings, pivot mode/rotation, snap settings, and grid availability. This is not MCP tool discovery; it observes editor tool configuration only.",
  category: "editor",
  cliCommand: "context read editor.tool-states",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      transform: { type: "object", description: "Active tool, pivot mode, and pivot rotation." },
      snap: { type: "object", description: "Move/rotation/scale snap settings." },
      gridAvailable: { type: "boolean", description: "Whether Unity snap/grid settings are available through this Unity version." }
    },
    required: ["transform", "snap", "gridAvailable"]
  },
  ...CONTEXT_COMMON,
};

const EDITOR_MENU_ITEMS: ContextDescriptor = {
  name: "editor.menu-items",
  description: "Attempt to enumerate Unity Editor menu items as read-only discovery. Unity has no stable public API for full menu enumeration, so this context may return an empty items array with diagnostics; executing a menu path remains the destructive Tool editor.menu.",
  category: "editor",
  cliCommand: "context read editor.menu-items",
  inputSchema: {
    type: "object",
    properties: {
      search: { type: "string", description: "Optional menu path/title substring filter if a Unity version exposes enumerable menu data." }
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
        description: "List of menu items, often empty because Unity has no stable public menu enumeration API.",
      },
      count: { type: "integer", description: "Number of returned menu items." },
      diagnostics: { type: "string", description: "Explanation when menu enumeration is unavailable." }
    },
    required: ["items", "count"]
  },
  ...CONTEXT_COMMON,
};

const EDITOR_PREFAB_STAGE: ContextDescriptor = {
  name: "prefab.stage",
  description: "Read current Prefab editing stage state: whether a prefab is being edited, its path, and dirty status. This observes Prefab Stage only and does not open, save, close, or modify it.",
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
  ...CONTEXT_COMMON,
};

// ── Scene ────────────────────────────────────────────────────────────────

const SCENE_GAMEOBJECT: ContextDescriptor = {
  name: "scene.gameobject",
  description: "Read one GameObject's lightweight scene summary by id, hierarchy path, or name. Use this before go.inspect, component.get, object.get-data, or mutation tools to confirm the stable path/id. Does not modify the scene; for full component fields use component.get.",
  category: "scene",
  cliCommand: "context read scene.gameobject",
  inputSchema: {
    type: "object",
    properties: {
      instanceId: { type: "integer", description: "Instance ID of a specific GameObject." },
      target: { type: "string", description: "GameObject name or hierarchy path. If ambiguous, pass path or id." },
      path: { type: "string", description: "Hierarchy path, e.g. Main Camera." },
      includeComponents: { type: "boolean", default: true, description: "Include lightweight component summaries." }
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
  ...CONTEXT_COMMON,
};

const SCENE_GAMEOBJECT_COMPONENTS: ContextDescriptor = {
  name: "scene.gameobject.components",
  description: "Lightweight list of components attached to a GameObject: type names, enabled state and optional readable property preview. Use this before component.get/component.modify to choose component name or index. Does NOT return full fields/props; for deep component data use `tools run component.get`.",
  category: "scene",
  cliCommand: "context read scene.gameobject.components",
  inputSchema: {
    type: "object",
    properties: {
      target: { type: "string", description: "GameObject name or hierarchy path." },
      id: { type: "integer", description: "GameObject instance ID." },
      path: { type: "string", description: "Hierarchy path, e.g. 'Canvas/Button'." },
      includeProperties: { type: "boolean", default: true, description: "Include shallow readable properties for each component; set false for a compact type list." }
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
  ...CONTEXT_COMMON,
};

const SCENE_GAMEOBJECT_COMPONENT: ContextDescriptor = {
  name: "scene.gameobject.component",
  description: "Lightweight preview of one component attached to a GameObject. Use scene.gameobject.components first when unsure which component name exists. Does NOT replace deep reads; for complete fields/props use `tools run component.get`.",
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
  ...CONTEXT_COMMON,
};

const SCENE_OPENED: ContextDescriptor = {
  name: "scene.opened",
  description: "List currently loaded scenes in the Editor with name, path, build index, loaded state, dirty state, and active scene. Read-only; does not load, unload, save, or switch scenes.",
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
      activeScene: { type: "string" },
      logPath: { type: "string" }
    },
    required: ["scenes", "count"]
  },
  ...CONTEXT_COMMON,
};

const SCENE_CAMERAS: ContextDescriptor = {
  name: "scene.cameras",
  description: "List all active scene cameras with stable GameObject path, tag, enabled state, projection, FOV/orthographic size, depth, target texture, and pixel size. Read-only camera inventory.",
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
            id: { type: "integer", description: "Camera component instance ID." },
            name: { type: "string", description: "Camera GameObject name." },
            path: { type: "string", description: "Hierarchy path." },
            tag: { type: "string", description: "GameObject tag." },
            enabled: { type: "boolean", description: "Whether the camera component is enabled." },
            projection: { type: "string", description: "perspective or orthographic." },
            fieldOfView: { type: "number", description: "Perspective field of view." },
            orthographicSize: { type: "number", description: "Orthographic size." },
            depth: { type: "number", description: "Camera depth." },
            targetTexture: { type: "string", description: "Target texture name, if any." },
            pixelWidth: { type: "integer", description: "Camera pixel width." },
            pixelHeight: { type: "integer", description: "Camera pixel height." }
          },
          required: ["id", "name", "path"]
        },
        description: "List of cameras in the scene.",
      },
      count: { type: "integer", description: "Number of cameras." }
    },
    required: ["cameras", "count"]
  },
  ...CONTEXT_COMMON,
};

const SCENE_VOLUMES: ContextDescriptor = {
  name: "scene.volumes",
  description: "List Volume components in the active scene with target GameObject, enabled state, global flag, priority, weight, profile reference, and pagination metadata. Returns an empty list when Volume type is unavailable.",
  category: "scene",
  cliCommand: "context read scene.volumes",
  inputSchema: { type: "object", properties: { target: { type: "string", description: "Optional hierarchy path or name filter." }, pageSize: { type: "integer", description: "Reported page size." }, cursor: { type: "string", description: "Reserved cursor for future pagination." } } },
  outputSchema: {
    type: "object",
    properties: {
      volumes: {
        type: "array",
        items: {
          type: "object",
          properties: {
            target: { type: "object", description: "Volume GameObject target summary." },
            enabled: { type: "boolean", description: "Whether the Volume component is enabled." },
            priority: { type: "number", description: "Volume priority." },
            weight: { type: "number", description: "Volume blend weight." },
            isGlobal: { type: "boolean", description: "Whether the volume is global." },
            profile: { type: "object", description: "Profile asset reference." }
          },
          required: ["target"]
        },
        description: "List of volumes.",
      },
      count: { type: "integer", description: "Number of volumes." },
      pageInfo: { type: "object", description: "Pagination metadata." }
    },
    required: ["volumes", "count", "pageInfo"]
  },
  ...CONTEXT_COMMON,
};

const SCENE_RENDERER_FEATURES: ContextDescriptor = {
  name: "scene.renderer-features",
  description: "Read URP renderer feature availability and configured feature names for the active/default render pipeline. If URP or a pipeline asset is unavailable, returns available=false with diagnostics and next_actions.",
  category: "scene",
  cliCommand: "context read scene.renderer-features",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      available: { type: "boolean", description: "Whether renderer feature data is available." },
      renderer: { type: "string", description: "Name of the active/default render pipeline asset." },
      features: { type: "array", items: { type: "string" }, description: "List of renderer feature or renderer data names." },
      count: { type: "integer", description: "Number of returned feature entries." },
      diagnostics: { type: "string", description: "Unavailable reason when available=false." },
      next_actions: { type: "array", items: { type: "string" }, description: "Suggested recovery actions when unavailable." }
    },
    required: ["available", "features"]
  },
  ...CONTEXT_COMMON,
};

const SCENE_RENDERING_STATS: ContextDescriptor = {
  name: "scene.rendering-stats",
  description: "Read current rendering statistics from UnityStats when available. If Unity's internal stats API is unavailable, returns available=false with diagnostics and next_actions.",
  category: "scene",
  cliCommand: "context read scene.rendering-stats",
  inputSchema: { type: "object", properties: {} },
  outputSchema: {
    type: "object",
    properties: {
      available: { type: "boolean", description: "Whether UnityStats was available." },
      stats: { type: "object", properties: { batches: { type: "string" }, triangles: { type: "string" }, vertices: { type: "string" }, screenResolution: { type: "string" } }, description: "Rendering stats strings from UnityStats." },
      diagnostics: { type: "string", description: "Unavailable reason when available=false." },
      next_actions: { type: "array", items: { type: "string" }, description: "Suggested recovery actions when unavailable." }
    },
    required: ["available"]
  },
  ...CONTEXT_COMMON,
};

// ── Prefab ───────────────────────────────────────────────────────────────

const PREFAB_INFO: ContextDescriptor = {
  name: "prefab.info",
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
  ...CONTEXT_COMMON,
};

const PREFAB_HIERARCHY: ContextDescriptor = {
  name: "prefab.hierarchy",
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
  ...CONTEXT_COMMON,
};

// ── Tools ────────────────────────────────────────────────────────────────

const TOOLS_GROUPS: ContextDescriptor = {
  name: "tools.groups",
  description: "Read built-in and available extension tool groups with counts and discovery commands from the local CLI registry; no Unity connection is required.",
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
  ...CONTEXT_COMMON,
  availabilityScope: "always",
};

const TOOLS_CUSTOM: ContextDescriptor = {
  name: "tools.custom",
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
  ...CONTEXT_COMMON,
};

// ── Tests ────────────────────────────────────────────────────────────────

const TESTS_LIST: ContextDescriptor = {
  name: "tests.list",
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
      available: { type: "boolean", description: "Whether Unity Test Framework enumeration is available." },
      tests: { type: "array", items: { type: "object" }, description: "Discovered test entries." },
      count: { type: "integer", description: "Number of returned tests." },
      pageInfo: { type: "object", description: "Pagination metadata when available." },
      diagnostics: { type: "string", description: "Availability or enumeration diagnostics." },
      next_actions: { type: "array", items: { type: "string" }, description: "Suggested actions when unavailable." }
    },
    required: ["available", "tests", "count"]
  },
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
