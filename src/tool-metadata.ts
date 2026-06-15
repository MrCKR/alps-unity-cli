/**
 * Static built-in tool metadata for the Alps Unity CLI.
 *
 * Covers every handler registered in bridgeCommandName() plus the
 * offline/job-only commands.  This data is shipped with the CLI/UnityPkg
 * release so that `tools describe` works without a live Unity instance.
 *
 * The schema is /schemas/tool-metadata.schema.json (ALP-164 frozen v1.0).
 */

export interface ToolExample {
  description: string;
  argv: string[];
  output?: string;
}

export interface ToolMetadata {
  name: string;
  title: string;
  description: string;
  group: string;
  inputSchema: Record<string, unknown>;
  outputSchema?: Record<string, unknown>;
  readOnlyHint: boolean;
  destructiveHint: boolean;
  longRunning?: boolean;
  requiresMainThread?: boolean;
  supportsDryRun?: boolean;
  timeoutMs?: number;
  permissions?: string[];
  examples?: ToolExample[];
  docsTopic?: string;
  handler?: "bridge" | "job" | "offline" | "hybrid";
  cliCommand?: string;
}

/** Return all built-in tool metadata entries. */
export function builtinToolMetadata(): ToolMetadata[] {
  return BUILTIN_TOOLS;
}

/** Return distinct group names from built-in metadata. */
export function builtinToolGroups(): string[] {
  const groups = new Set<string>();
  for (const tool of BUILTIN_TOOLS) {
    groups.add(tool.group);
  }
  return [...groups].sort();
}

/** Find a single tool by its dotted name. */
export function findToolByName(name: string): ToolMetadata | undefined {
  return BUILTIN_TOOLS.find((t) => t.name === name);
}

/** Filter tools by group. */
export function filterToolsByGroup(group: string): ToolMetadata[] {
  return BUILTIN_TOOLS.filter((t) => t.group === group);
}

/** Search tools by keyword across name, title, description, group. */
export function searchTools(keyword: string): ToolMetadata[] {
  const lower = keyword.toLowerCase();
  return BUILTIN_TOOLS.filter((t) =>
    t.name.toLowerCase().includes(lower) ||
    t.title.toLowerCase().includes(lower) ||
    t.description.toLowerCase().includes(lower) ||
    t.group.toLowerCase().includes(lower)
  ).sort((a, b) => {
    // Exact name match first, then prefix match, then substring
    const aScore = scoreSearch(a.name.toLowerCase(), lower) + scoreSearch(a.title.toLowerCase(), lower);
    const bScore = scoreSearch(b.name.toLowerCase(), lower) + scoreSearch(b.title.toLowerCase(), lower);
    return bScore - aScore;
  });
}

function scoreSearch(text: string, query: string): number {
  if (text === query) return 8;
  if (text.startsWith(query)) return 4;
  if (text.includes(query)) return 2;
  return 0;
}

/** Minimal JSON Schema helpers */
function objSchema(properties: Record<string, unknown>, required?: string[]): Record<string, unknown> {
  const schema: Record<string, unknown> = { type: "object", properties };
  if (required && required.length > 0) schema.required = required;
  return schema;
}

function pageSchema(itemSchema: Record<string, unknown>): Record<string, unknown> {
  return objSchema({
    items: { type: "array", items: itemSchema },
    pageInfo: objSchema({
      pageSize: { type: "integer" },
      cursor: { type: "string" },
      nextCursor: { type: "string" },
      total: { type: "integer" }
    }, ["pageSize", "cursor", "total"])
  }, ["items", "pageInfo"]);
}

// ── Scene ──────────────────────────────────────────────────

const SCENE_TOOLS: ToolMetadata[] = [
  {
    name: "scene.hierarchy",
    title: "Scene Hierarchy",
    description: "List the root GameObjects in the active scene with optional pagination.",
    group: "scene",
    inputSchema: objSchema({ pageSize: { type: "integer", default: 100 }, cursor: { type: "string" } }),
    outputSchema: pageSchema(objSchema({ name: { type: "string" }, path: { type: "string" }, childCount: { type: "integer" } })),
    readOnlyHint: true,
    destructiveHint: false,
    requiresMainThread: true,
    handler: "bridge",
    cliCommand: "scene hierarchy",
    docsTopic: "scene",
    examples: [
      { description: "List all root objects", argv: ["scene", "hierarchy"] },
      { description: "Paginate with cursor", argv: ["scene", "hierarchy", "--page-size", "50", "--cursor", "0"] }
    ]
  },
  {
    name: "scene.active",
    title: "Active Scene Info",
    description: "Return name, path and build index of the currently active scene.",
    group: "scene",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ name: { type: "string" }, path: { type: "string" }, buildIndex: { type: "integer" } }),
    readOnlyHint: true,
    destructiveHint: false,
    requiresMainThread: true,
    handler: "bridge",
    cliCommand: "scene active",
    docsTopic: "scene"
  },
  {
    name: "scene.build-settings",
    title: "Build Settings",
    description: "List scenes in the Editor build settings.",
    group: "scene",
    inputSchema: objSchema({}),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "scene build-settings",
    docsTopic: "scene"
  },
  {
    name: "scene.screenshot",
    title: "Scene Screenshot",
    description: "Capture a screenshot of the active scene viewport and save to a file.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string" }, width: { type: "integer", default: 1280 }, height: { type: "integer", default: 720 }, format: { type: "string", enum: ["png", "jpg"], default: "png" }, overwrite: { type: "boolean" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "scene screenshot",
    docsTopic: "scene",
    examples: [
      { description: "Capture PNG screenshot", argv: ["scene", "screenshot", "shot.png"] }
    ]
  },
  {
    name: "scene.create",
    title: "Create Scene",
    description: "Create a new empty scene.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string" }, name: { type: "string" } }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "scene create",
    docsTopic: "scene"
  },
  {
    name: "scene.load",
    title: "Load Scene",
    description: "Load a scene by path or build index.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string" }, name: { type: "string" } }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "scene load",
    docsTopic: "scene"
  },
  {
    name: "scene.save",
    title: "Save Scene",
    description: "Save the active scene.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string" }, name: { type: "string" } }),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "bridge",
    cliCommand: "scene save",
    docsTopic: "scene"
  }
];

// ── Camera ─────────────────────────────────────────────────

const CAMERA_TOOLS: ToolMetadata[] = [
  {
    name: "camera.list",
    title: "Camera List",
    description: "List all cameras in the active scene.",
    group: "camera",
    inputSchema: objSchema({}),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "camera list",
    docsTopic: "camera"
  },
  {
    name: "camera.info",
    title: "Camera Info",
    description: "Get position, rotation, FOV and other properties of a camera.",
    group: "camera",
    inputSchema: objSchema({ target: { type: "string" } }, ["target"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "camera info",
    docsTopic: "camera",
    examples: [
      { description: "Inspect main camera", argv: ["camera", "info", "Main Camera"] }
    ]
  },
  {
    name: "camera.screenshot",
    title: "Camera Screenshot",
    description: "Render a screenshot from a specific camera and save to file.",
    group: "camera",
    inputSchema: objSchema({ target: { type: "string" }, output: { type: "string" }, width: { type: "integer" }, height: { type: "integer" }, format: { type: "string" }, transparent: { type: "boolean" }, overwrite: { type: "boolean" } }, ["target", "output"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "camera screenshot",
    docsTopic: "camera",
    examples: [
      { description: "Capture from Main Camera", argv: ["camera", "screenshot", "Main Camera", "--output", "cam_shot.png"] }
    ]
  }
];

// ── GameObject ──────────────────────────────────────────────

const GO_TOOLS: ToolMetadata[] = [
  {
    name: "go.find",
    title: "Find GameObject",
    description: "Find a GameObject by name in the active scene hierarchy.",
    group: "go",
    inputSchema: objSchema({ name: { type: "string" } }, ["name"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "go find",
    docsTopic: "go",
    examples: [
      { description: "Find by name", argv: ["go", "find", "--name", "Player"] }
    ]
  },
  {
    name: "go.inspect",
    title: "Inspect GameObject",
    description: "Get components, children and transform data of a GameObject.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string" } }, ["target"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "go inspect",
    docsTopic: "go"
  },
  {
    name: "go.create",
    title: "Create GameObject",
    description: "Create a new empty GameObject in the active scene.",
    group: "go",
    inputSchema: objSchema({ name: { type: "string" }, parent: { type: "string" } }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "go create",
    docsTopic: "go"
  },
  {
    name: "go.modify",
    title: "Modify GameObject",
    description: "Modify transform or other properties of a GameObject.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string" } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "go modify",
    docsTopic: "go"
  },
  {
    name: "go.delete",
    title: "Delete GameObject",
    description: "Delete a GameObject from the scene. This action is irreversible.",
    group: "go",
    inputSchema: objSchema({ target: { type: "string" } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "bridge",
    cliCommand: "go delete",
    docsTopic: "go"
  }
];

// ── Component ──────────────────────────────────────────────

const COMPONENT_TOOLS: ToolMetadata[] = [
  {
    name: "component.add",
    title: "Add Component",
    description: "Add a component to a GameObject.",
    group: "component",
    inputSchema: objSchema({ target: { type: "string" }, component: { type: "string" } }, ["target", "component"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "component add",
    docsTopic: "component"
  },
  {
    name: "component.remove",
    title: "Remove Component",
    description: "Remove a component from a GameObject. Irreversible.",
    group: "component",
    inputSchema: objSchema({ target: { type: "string" }, component: { type: "string" } }, ["target", "component"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "bridge",
    cliCommand: "component remove",
    docsTopic: "component"
  },
  {
    name: "component.set",
    title: "Set Component Property",
    description: "Set a property value on a component attached to a GameObject.",
    group: "component",
    inputSchema: objSchema({ target: { type: "string" }, component: { type: "string" }, property: { type: "string" }, value: { type: "string" } }, ["target", "component", "property", "value"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "component set",
    docsTopic: "component"
  }
];

// ── Asset ──────────────────────────────────────────────────

const ASSET_TOOLS: ToolMetadata[] = [
  {
    name: "asset.search",
    title: "Search Assets",
    description: "Search the project asset database by name or filter.",
    group: "asset",
    inputSchema: objSchema({ query: { type: "string" }, filter: { type: "string" } }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "asset search",
    docsTopic: "asset"
  },
  {
    name: "asset.info",
    title: "Asset Info",
    description: "Get metadata and path information for an asset.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "asset info",
    docsTopic: "asset"
  },
  {
    name: "asset.mkdir",
    title: "Create Asset Folder",
    description: "Create a folder inside the Assets directory.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "asset mkdir",
    docsTopic: "asset"
  },
  {
    name: "asset.import",
    title: "Import Asset",
    description: "Import a file into the Unity asset database.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "asset import",
    docsTopic: "asset"
  },
  {
    name: "asset.move",
    title: "Move Asset",
    description: "Move an asset from one path to another.",
    group: "asset",
    inputSchema: objSchema({ source: { type: "string" }, destination: { type: "string" } }, ["source", "destination"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "asset move",
    docsTopic: "asset"
  },
  {
    name: "asset.delete",
    title: "Delete Asset",
    description: "Delete an asset permanently. Irreversible.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "bridge",
    cliCommand: "asset delete",
    docsTopic: "asset"
  }
];

// ── Prefab ─────────────────────────────────────────────────

const PREFAB_TOOLS: ToolMetadata[] = [
  {
    name: "prefab.info",
    title: "Prefab Info",
    description: "Get information about a prefab asset.",
    group: "prefab",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "prefab info",
    docsTopic: "prefab"
  },
  {
    name: "prefab.create",
    title: "Create Prefab",
    description: "Create a prefab from a source GameObject.",
    group: "prefab",
    inputSchema: objSchema({ source: { type: "string" }, path: { type: "string" }, overwrite: { type: "boolean" } }, ["source", "path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "prefab create",
    docsTopic: "prefab",
    examples: [
      { description: "Create prefab from Player", argv: ["prefab", "create", "Player", "--path", "Assets/Prefabs/Player.prefab"] }
    ]
  },
  {
    name: "prefab.stage-open",
    title: "Open Prefab Stage",
    description: "Open a prefab in isolation mode for editing.",
    group: "prefab",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "prefab stage-open",
    docsTopic: "prefab"
  },
  {
    name: "prefab.stage-save",
    title: "Save Prefab Stage",
    description: "Save modifications in the current prefab isolation stage.",
    group: "prefab",
    inputSchema: objSchema({ path: { type: "string" } }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "prefab stage-save",
    docsTopic: "prefab"
  },
  {
    name: "prefab.stage-close",
    title: "Close Prefab Stage",
    description: "Close the prefab isolation stage.",
    group: "prefab",
    inputSchema: objSchema({ path: { type: "string" }, save: { type: "boolean" } }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "prefab stage-close",
    docsTopic: "prefab"
  },
  {
    name: "prefab.hierarchy",
    title: "Prefab Hierarchy",
    description: "List the child hierarchy of a prefab asset.",
    group: "prefab",
    inputSchema: objSchema({ path: { type: "string" }, pageSize: { type: "integer" }, cursor: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "prefab hierarchy",
    docsTopic: "prefab"
  }
];

// ── Material ───────────────────────────────────────────────

const MATERIAL_TOOLS: ToolMetadata[] = [
  {
    name: "material.info",
    title: "Material Info",
    description: "Get shader, properties and texture references of a material.",
    group: "material",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "material info",
    docsTopic: "material"
  },
  {
    name: "material.create",
    title: "Create Material",
    description: "Create a new material asset with a specified shader.",
    group: "material",
    inputSchema: objSchema({ path: { type: "string" }, shader: { type: "string" }, overwrite: { type: "boolean" } }, ["path", "shader"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "material create",
    docsTopic: "material"
  },
  {
    name: "material.assign",
    title: "Assign Material",
    description: "Assign a material to a GameObject's renderer slot.",
    group: "material",
    inputSchema: objSchema({ target: { type: "string" }, material: { type: "string" }, slot: { type: "integer" } }, ["target", "material"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "material assign",
    docsTopic: "material"
  },
  {
    name: "material.set",
    title: "Set Material Property",
    description: "Set a named property on a material (color, float, vector, texture).",
    group: "material",
    inputSchema: objSchema({ path: { type: "string" }, property: { type: "string" }, value: { type: "string" }, type: { type: "string", enum: ["color", "float", "vector", "texture"] } }, ["path", "property", "value"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "material set",
    docsTopic: "material"
  }
];

// ── Graphics ───────────────────────────────────────────────

const GRAPHICS_TOOLS: ToolMetadata[] = [
  {
    name: "graphics.quality",
    title: "Graphics Quality Level",
    description: "Get or set the current quality level and VSync count.",
    group: "graphics",
    inputSchema: objSchema({
      qualityLevel: { type: "integer" },
      vSyncCount: { type: "integer" },
      antiAliasing: { type: "integer" },
      shadowDistance: { type: "number" },
      masterTextureLimit: { type: "integer" },
      lodBias: { type: "number" },
      maximumLODLevel: { type: "integer" },
      anisotropicFiltering: { type: "string" },
      shadows: { type: "string" },
      shadowResolution: { type: "string" },
      realtimeReflectionProbes: { type: "boolean" },
      softParticles: { type: "boolean" }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "graphics quality",
    docsTopic: "graphics"
  },
  {
    name: "graphics.settings",
    title: "Graphics Settings",
    description: "Get or set anti-aliasing and shadow distance.",
    group: "graphics",
    inputSchema: objSchema({
      antiAliasing: { type: "integer" },
      shadowDistance: { type: "number" },
      vSyncCount: { type: "integer" },
      masterTextureLimit: { type: "integer" },
      lodBias: { type: "number" },
      maximumLODLevel: { type: "integer" },
      renderPipeline: { type: "string" },
      property: { type: "string" },
      value: { type: "string" }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "graphics settings",
    docsTopic: "graphics"
  },
  {
    name: "graphics.pipeline",
    title: "Graphics Render Pipeline",
    description: "Query the active and default render pipeline summary.",
    group: "graphics",
    inputSchema: objSchema({}),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "graphics pipeline",
    docsTopic: "graphics"
  },
  {
    name: "graphics.volume",
    title: "Graphics Volume",
    description: "Query Volume components and assigned profiles in the active scene.",
    group: "graphics",
    inputSchema: objSchema({ target: { type: "string" }, pageSize: { type: "integer" }, cursor: { type: "string" } }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "graphics volume",
    docsTopic: "graphics"
  },
  {
    name: "graphics.screenshot",
    title: "Graphics Screenshot",
    description: "Capture a screenshot with optional GPU diagnostics overlay.",
    group: "graphics",
    inputSchema: objSchema({ path: { type: "string" }, includeDiagnostics: { type: "boolean" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "graphics screenshot",
    docsTopic: "graphics"
  }
];

// ── Physics ────────────────────────────────────────────────

const PHYSICS_TOOLS: ToolMetadata[] = [
  {
    name: "physics.settings",
    title: "Physics Settings",
    description: "Get or set physics gravity, mode and other global settings.",
    group: "physics",
    inputSchema: objSchema({
      mode: { type: "string", enum: ["3d", "2d"] },
      gravityX: { type: "number" },
      gravityY: { type: "number" },
      gravityZ: { type: "number" },
      gravity2dX: { type: "number" },
      gravity2dY: { type: "number" },
      defaultContactOffset: { type: "number" },
      bounceThreshold: { type: "number" },
      defaultSolverIterations: { type: "integer" },
      defaultSolverVelocityIterations: { type: "integer" },
      queriesHitTriggers: { type: "boolean" },
      queriesHitBackfaces: { type: "boolean" },
      queriesStartInColliders: { type: "boolean" },
      autoSimulation: { type: "boolean" },
      property: { type: "string" },
      value: { type: "string" }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "physics settings",
    docsTopic: "physics"
  },
  {
    name: "physics.raycast",
    title: "Physics Raycast",
    description: "Cast a ray and report the first hit.",
    group: "physics",
    inputSchema: objSchema({ originX: { type: "number" }, originY: { type: "number" }, originZ: { type: "number" }, directionX: { type: "number" }, directionY: { type: "number" }, directionZ: { type: "number" }, distance: { type: "number" }, mode: { type: "string", enum: ["3d", "2d"] }, layerMask: { type: "integer" }, all: { type: "boolean" } }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "physics raycast",
    docsTopic: "physics"
  },
  {
    name: "physics.simulation",
    title: "Physics Simulation",
    description: "Step the physics simulation forward.",
    group: "physics",
    inputSchema: objSchema({ steps: { type: "integer" }, deltaTime: { type: "number" }, simulate: { type: "boolean" }, mode: { type: "string", enum: ["3d", "2d"] } }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "physics simulation",
    docsTopic: "physics"
  }
];

// ── Profiler ───────────────────────────────────────────────

const PROFILER_TOOLS: ToolMetadata[] = [
  {
    name: "profiler.status",
    title: "Profiler Status",
    description: "Check whether the Unity profiler is currently active.",
    group: "profiler",
    inputSchema: objSchema({}),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "profiler status",
    docsTopic: "profiler"
  },
  {
    name: "profiler.start",
    title: "Start Profiler",
    description: "Begin profiler data collection with optional deep profiling.",
    group: "profiler",
    inputSchema: objSchema({ deepProfiling: { type: "boolean" } }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "profiler start",
    docsTopic: "profiler"
  },
  {
    name: "profiler.stop",
    title: "Stop Profiler",
    description: "Stop profiler data collection.",
    group: "profiler",
    inputSchema: objSchema({}),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "profiler stop",
    docsTopic: "profiler"
  },
  {
    name: "profiler.capture",
    title: "Capture Profiler Data",
    description: "Capture profiler frame data to a file. Can run as a long job.",
    group: "profiler",
    inputSchema: objSchema({ path: { type: "string" }, durationMs: { type: "integer" }, frames: { type: "integer" }, format: { type: "string", enum: ["json", "raw"] }, overwrite: { type: "boolean" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    longRunning: true,
    supportsDryRun: false,
    handler: "hybrid",
    cliCommand: "profiler capture",
    docsTopic: "profiler",
    examples: [
      { description: "Capture 1 second of profiler data", argv: ["profiler", "capture", "profile.json", "--duration-ms", "1000"] },
      { description: "Capture as job with wait", argv: ["profiler", "capture", "profile.json", "--job", "--wait"] }
    ]
  }
];

// ── ProBuilder ─────────────────────────────────────────────

const PROBUILDER_TOOLS: ToolMetadata[] = [
  {
    name: "probuilder.shape",
    title: "ProBuilder Shape",
    description: "Create a ProBuilder primitive shape in the scene.",
    group: "probuilder",
    inputSchema: objSchema({ shape: { type: "string", enum: ["cube", "sphere", "cylinder", "plane", "capsule"] }, name: { type: "string" }, parent: { type: "string" } }, ["shape"]),
    readOnlyHint: false,
    destructiveHint: false,
    longRunning: true,
    handler: "hybrid",
    cliCommand: "probuilder shape",
    docsTopic: "probuilder",
    examples: [
      { description: "Create a ProBuilder cube", argv: ["probuilder", "shape", "cube"] }
    ]
  },
  {
    name: "probuilder.create",
    title: "ProBuilder Create",
    description: "Create a ProBuilder mesh with a specified shape.",
    group: "probuilder",
    inputSchema: objSchema({ shape: { type: "string" }, name: { type: "string" } }, ["shape"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "probuilder create",
    docsTopic: "probuilder"
  },
  {
    name: "probuilder.edit",
    title: "ProBuilder Edit",
    description: "Edit a ProBuilder mesh element with an operation.",
    group: "probuilder",
    inputSchema: objSchema({ target: { type: "string" }, operation: { type: "string" }, value: { type: "string" } }, ["target", "operation"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "probuilder edit",
    docsTopic: "probuilder"
  },
  {
    name: "probuilder.csg",
    title: "ProBuilder CSG",
    description: "Boolean CSG operation between two ProBuilder meshes.",
    group: "probuilder",
    inputSchema: objSchema({ target: { type: "string" }, source: { type: "string" }, operation: { type: "string", enum: ["union", "subtract", "intersect"] } }, ["target", "source", "operation"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "hybrid",
    cliCommand: "probuilder csg",
    docsTopic: "probuilder"
  }
];

// ── Custom Tool ────────────────────────────────────────────

const CUSTOM_TOOL_TOOLS: ToolMetadata[] = [
  {
    name: "custom-tool.list",
    title: "List Custom Tools",
    description: "List locally-registered custom tools for the current project.",
    group: "custom-tool",
    inputSchema: objSchema({ project: { type: "string" }, instance: { type: "string" } }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "custom-tool list",
    docsTopic: "custom-tool"
  },
  {
    name: "custom-tool.run",
    title: "Run Custom Tool",
    description: "Execute a registered custom tool with input payload.",
    group: "custom-tool",
    inputSchema: objSchema({ name: { type: "string" }, input: { type: "object" }, timeoutMs: { type: "integer" } }, ["name"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "custom-tool run",
    docsTopic: "custom-tool"
  }
];

// ── Shader ─────────────────────────────────────────────────

const SHADER_TOOLS: ToolMetadata[] = [
  {
    name: "shader.info",
    title: "Shader Info",
    description: "Get properties and passes of a shader asset.",
    group: "shader",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "shader info",
    docsTopic: "shader"
  },
  {
    name: "shader.read",
    title: "Read Shader Source",
    description: "Read the source code of a shader file.",
    group: "shader",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "shader read",
    docsTopic: "shader"
  },
  {
    name: "shader.create",
    title: "Create Shader",
    description: "Create a new shader file.",
    group: "shader",
    inputSchema: objSchema({ path: { type: "string" }, source: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "shader create",
    docsTopic: "shader"
  },
  {
    name: "shader.update",
    title: "Update Shader",
    description: "Update the source code of an existing shader.",
    group: "shader",
    inputSchema: objSchema({ path: { type: "string" }, source: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "shader update",
    docsTopic: "shader"
  }
];

// ── Texture ────────────────────────────────────────────────

const TEXTURE_TOOLS: ToolMetadata[] = [
  {
    name: "texture.info",
    title: "Texture Info",
    description: "Get dimensions, format and metadata of a texture asset.",
    group: "texture",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "texture info",
    docsTopic: "texture"
  },
  {
    name: "texture.create",
    title: "Create Texture",
    description: "Create a new texture asset.",
    group: "texture",
    inputSchema: objSchema({ path: { type: "string" }, width: { type: "integer" }, height: { type: "integer" }, format: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "texture create",
    docsTopic: "texture"
  },
  {
    name: "texture.sprite",
    title: "Texture Sprite",
    description: "Create or update a sprite from a texture.",
    group: "texture",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "texture sprite",
    docsTopic: "texture"
  },
  {
    name: "texture.import-settings",
    title: "Texture Import Settings",
    description: "Get or set import settings for a texture asset.",
    group: "texture",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "texture import-settings",
    docsTopic: "texture"
  }
];

// ── Animation ──────────────────────────────────────────────

const ANIMATION_TOOLS: ToolMetadata[] = [
  {
    name: "animation.clip",
    title: "Animation Clip",
    description: "Get info on an animation clip, optionally create one.",
    group: "animation",
    inputSchema: objSchema({ path: { type: "string" }, action: { type: "string", enum: ["info", "create"] }, create: { type: "boolean" }, overwrite: { type: "boolean" }, frameRate: { type: "number" }, legacy: { type: "boolean" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "animation clip",
    docsTopic: "animation"
  },
  {
    name: "animation.state",
    title: "Animation State",
    description: "Query the current animation state on a target.",
    group: "animation",
    inputSchema: objSchema({ target: { type: "string" }, layer: { type: "integer" } }, ["target"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "animation state",
    docsTopic: "animation"
  },
  {
    name: "animation.play",
    title: "Play Animation",
    description: "Play an animation state on a target GameObject.",
    group: "animation",
    inputSchema: objSchema({ target: { type: "string" }, state: { type: "string" }, layer: { type: "integer" }, normalizedTime: { type: "number" } }, ["target", "state"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "animation play",
    docsTopic: "animation"
  },
  {
    name: "animation.stop",
    title: "Stop Animation",
    description: "Stop the currently playing animation on a target.",
    group: "animation",
    inputSchema: objSchema({ target: { type: "string" } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "animation stop",
    docsTopic: "animation"
  }
];

// ── VFX ────────────────────────────────────────────────────

const VFX_TOOLS: ToolMetadata[] = [
  {
    name: "vfx.graph",
    title: "VFX Graph",
    description: "Get info on a Visual Effect graph asset, optionally create one.",
    group: "vfx",
    inputSchema: objSchema({ path: { type: "string" }, action: { type: "string", enum: ["info", "create"] }, create: { type: "boolean" }, overwrite: { type: "boolean" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx graph",
    docsTopic: "vfx"
  },
  {
    name: "vfx.effect",
    title: "VFX Effect",
    description: "Manage a Visual Effect instance on a GameObject.",
    group: "vfx",
    inputSchema: objSchema({ target: { type: "string" }, graph: { type: "string" }, add: { type: "boolean" } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "vfx effect",
    docsTopic: "vfx"
  }
];

// ── UI ──────────────────────────────────────────────────────

const UI_TOOLS: ToolMetadata[] = [
  {
    name: "ui.canvas.create",
    title: "Create UI Canvas",
    description: "Create a new UI Canvas in the scene.",
    group: "ui",
    inputSchema: objSchema({ name: { type: "string" } }, ["name"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "ui canvas/create",
    docsTopic: "ui"
  },
  {
    name: "ui.text.create",
    title: "Create UI Text",
    description: "Create a UI Text element under a parent.",
    group: "ui",
    inputSchema: objSchema({ name: { type: "string" }, parent: { type: "string" }, text: { type: "string" }, width: { type: "integer" }, height: { type: "integer" } }, ["name", "parent"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "ui text/create",
    docsTopic: "ui"
  },
  {
    name: "ui.button.create",
    title: "Create UI Button",
    description: "Create a UI Button element under a parent.",
    group: "ui",
    inputSchema: objSchema({ name: { type: "string" }, parent: { type: "string" }, label: { type: "string" } }, ["name", "parent"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "ui button/create",
    docsTopic: "ui"
  },
  {
    name: "ui.image.create",
    title: "Create UI Image",
    description: "Create a UI Image element under a parent.",
    group: "ui",
    inputSchema: objSchema({ name: { type: "string" }, parent: { type: "string" }, sprite: { type: "string" } }, ["name", "parent"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "ui image/create",
    docsTopic: "ui"
  },
  {
    name: "ui.document.info",
    title: "UI Document Info",
    description: "Get info on a UI Document (UIToolkit) or read a UXML asset.",
    group: "ui",
    inputSchema: objSchema({ target: { type: "string" }, uss: { type: "string" } }, ["target"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "ui document/info",
    docsTopic: "ui"
  },
  {
    name: "ui.document.create",
    title: "Create UI Document",
    description: "Create a new UI Document from a UXML template.",
    group: "ui",
    inputSchema: objSchema({ uxml: { type: "string" }, uss: { type: "string" }, panelSettings: { type: "string" }, overwrite: { type: "boolean" } }, ["uxml"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "ui document/create",
    docsTopic: "ui"
  },
  {
    name: "ui.hierarchy",
    title: "UI Hierarchy",
    description: "List UI elements in a canvas or UI Document hierarchy.",
    group: "ui",
    inputSchema: objSchema({ target: { type: "string" }, pageSize: { type: "integer" }, cursor: { type: "string" } }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "ui hierarchy",
    docsTopic: "ui"
  }
];

// ── Script ─────────────────────────────────────────────────

const SCRIPT_TOOLS: ToolMetadata[] = [
  {
    name: "script.create",
    title: "Create Script",
    description: "Create a new C# script file in the project.",
    group: "script",
    inputSchema: objSchema({ path: { type: "string" }, contentFile: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "script create",
    docsTopic: "script"
  },
  {
    name: "script.read",
    title: "Read Script",
    description: "Read the content of a C# script file.",
    group: "script",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "script read",
    docsTopic: "script"
  },
  {
    name: "script.edit",
    title: "Edit Script",
    description: "Apply targeted edits to a C# script file.",
    group: "script",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "bridge",
    cliCommand: "script edit",
    docsTopic: "script"
  },
  {
    name: "script.validate",
    title: "Validate Script",
    description: "Validate a C# script without compiling.",
    group: "script",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "script validate",
    docsTopic: "script"
  }
];

// ── Editor ─────────────────────────────────────────────────

const EDITOR_TOOLS: ToolMetadata[] = [
  {
    name: "editor.console",
    title: "Editor Console Logs",
    description: "Read recent log entries from the Unity Editor console.",
    group: "editor",
    inputSchema: objSchema({}),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "editor console",
    docsTopic: "editor"
  },
  {
    name: "editor.state",
    title: "Editor State",
    description: "Query the current Editor state (play mode, pause, etc.).",
    group: "editor",
    inputSchema: objSchema({}),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "editor state",
    docsTopic: "editor"
  },
  {
    name: "editor.play",
    title: "Enter Play Mode",
    description: "Start the Unity Editor play mode.",
    group: "editor",
    inputSchema: objSchema({}),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "editor play",
    docsTopic: "editor"
  },
  {
    name: "editor.pause",
    title: "Pause Play Mode",
    description: "Pause the currently running play mode.",
    group: "editor",
    inputSchema: objSchema({}),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "editor pause",
    docsTopic: "editor"
  },
  {
    name: "editor.stop",
    title: "Stop Play Mode",
    description: "Stop play mode and return to edit mode.",
    group: "editor",
    inputSchema: objSchema({}),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "editor stop",
    docsTopic: "editor"
  },
  {
    name: "editor.menu",
    title: "Execute Editor Menu",
    description: "Invoke a Unity Editor menu item by path.",
    group: "editor",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "bridge",
    cliCommand: "editor menu",
    docsTopic: "editor"
  },
  {
    name: "editor.refresh",
    title: "Refresh Editor Assets",
    description: "Trigger asset refresh in the Unity Editor, optionally recompiling scripts. Runs as a Unity batch job.",
    group: "editor",
    inputSchema: objSchema({
      compile: { type: "boolean", default: false },
      project: { type: "string" },
      unity: { type: "string" },
      wait: { type: "boolean", default: true }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    longRunning: true,
    handler: "job",
    cliCommand: "editor refresh",
    docsTopic: "editor",
    examples: [
      { description: "Refresh assets without recompile", argv: ["editor", "refresh"] },
      { description: "Refresh and recompile, then wait", argv: ["editor", "refresh", "--compile", "--wait"] }
    ]
  }
];

// ── Diagnose ───────────────────────────────────────────────

const DIAGNOSE_TOOLS: ToolMetadata[] = [
  {
    name: "diagnose.preflight",
    title: "Preflight Diagnostics",
    description: "Run a quick health check on the Unity project and bridge setup.",
    group: "diagnose",
    inputSchema: objSchema({}),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "preflight",
    docsTopic: "diagnose"
  }
];

// ── Screenshot ─────────────────────────────────────────────

const SCREENSHOT_TOOLS: ToolMetadata[] = [
  {
    name: "screenshot.capture",
    title: "Capture Screenshot",
    description: "Capture a game-view screenshot and save to a path.",
    group: "screenshot",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "screenshot capture",
    docsTopic: "screenshot"
  }
];

// ── Code ───────────────────────────────────────────────────

const CODE_TOOLS: ToolMetadata[] = [
  {
    name: "code.exec",
    title: "Execute Live Code",
    description: "Execute C# code in the Unity Editor live or batchmode context.",
    group: "code",
    inputSchema: objSchema({
      file: { type: "string" },
      stdin: { type: "boolean" },
      code: { type: "string" },
      channel: { type: "string", enum: ["live", "batchmode"] },
      refs: { type: "array", items: { type: "string" } },
      define: { type: "array", items: { type: "string" } },
      args: { type: "object" },
      timeoutMs: { type: "integer" }
    }),
    readOnlyHint: false,
    destructiveHint: true,
    longRunning: true,
    handler: "hybrid",
    cliCommand: "code exec",
    docsTopic: "code",
    examples: [
      { description: "Execute a code file", argv: ["code", "exec", "--file", "script.cs"] },
      { description: "Execute inline code", argv: ["code", "exec", "--code", "Debug.Log(\"hello\");"] }
    ]
  }
];

// ── CLI-only commands (offline / job-based) ────────────────

const CLI_ONLY_TOOLS: ToolMetadata[] = [
  {
    name: "cli.init",
    title: "Init Project",
    description: "Deploy the Unity Bridge package into a project and optionally refresh the open editor.",
    group: "cli",
    inputSchema: objSchema({ project: { type: "string" }, unity: { type: "string" }, wait: { type: "boolean" } }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "init",
    docsTopic: "init",
    examples: [
      { description: "Init current directory", argv: ["init"] }
    ]
  },
  {
    name: "cli.package.list",
    title: "Package List",
    description: "List installed packages and their versions in the project manifest.",
    group: "cli",
    inputSchema: objSchema({ project: { type: "string" } }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "package list",
    docsTopic: "package"
  },
  {
    name: "cli.package.info",
    title: "Package Info",
    description: "Show detailed info for an installed package.",
    group: "cli",
    inputSchema: objSchema({ name: { type: "string" }, project: { type: "string" } }, ["name"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "package info",
    docsTopic: "package"
  },
  {
    name: "cli.package.search",
    title: "Package Search",
    description: "Search a Unity package registry by name.",
    group: "cli",
    inputSchema: objSchema({ query: { type: "string" }, registry: { type: "string" } }, ["query"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "package search",
    docsTopic: "package"
  },
  {
    name: "cli.package.install",
    title: "Package Install",
    description: "Install a package into the project manifest and optionally resolve.",
    group: "cli",
    inputSchema: objSchema({ name: { type: "string" }, version: { type: "string" }, project: { type: "string" }, unity: { type: "string" }, wait: { type: "boolean" } }, ["name"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "package install",
    docsTopic: "package"
  },
  {
    name: "cli.package.remove",
    title: "Package Remove",
    description: "Remove a package from the project manifest.",
    group: "cli",
    inputSchema: objSchema({ name: { type: "string" }, project: { type: "string" }, unity: { type: "string" }, wait: { type: "boolean" } }, ["name"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "hybrid",
    cliCommand: "package remove",
    docsTopic: "package"
  },
  {
    name: "cli.batch.run",
    title: "Batch Run",
    description: "Execute a JSON batch file containing multiple CLI commands.",
    group: "cli",
    inputSchema: objSchema({ file: { type: "string" }, preflight: { type: "boolean" }, failFast: { type: "boolean" }, parallel: { type: "integer" } }, ["file"]),
    readOnlyHint: false,
    destructiveHint: true,
    supportsDryRun: true,
    handler: "offline",
    cliCommand: "batch run",
    docsTopic: "batch",
    examples: [
      { description: "Run batch with preflight", argv: ["batch", "run", "batch.json", "--preflight"] }
    ]
  },
  {
    name: "cli.docs.get",
    title: "Docs Get",
    description: "Fetch offline Unity API documentation for a specific API name.",
    group: "cli",
    inputSchema: objSchema({ api: { type: "string" }, docsDir: { type: "string" }, unityVersion: { type: "string" }, format: { type: "string", enum: ["json", "text"] } }, ["api"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "docs get",
    docsTopic: "docs"
  },
  {
    name: "cli.docs.search",
    title: "Docs Search",
    description: "Search the offline Unity API documentation cache.",
    group: "cli",
    inputSchema: objSchema({ query: { type: "string" }, docsDir: { type: "string" }, pageSize: { type: "integer" }, cursor: { type: "string" }, format: { type: "string" } }, ["query"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "docs search",
    docsTopic: "docs"
  },
  {
    name: "cli.reflect.type",
    title: "Reflect Type",
    description: "Look up a C# type in the offline reflection cache.",
    group: "cli",
    inputSchema: objSchema({ name: { type: "string" }, reflectDir: { type: "string" }, unityVersion: { type: "string" } }, ["name"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "reflect type",
    docsTopic: "reflect"
  },
  {
    name: "cli.reflect.member",
    title: "Reflect Member",
    description: "Look up a member on a C# type in the offline reflection cache.",
    group: "cli",
    inputSchema: objSchema({ member: { type: "string" }, reflectDir: { type: "string" } }, ["member"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "reflect member",
    docsTopic: "reflect"
  },
  {
    name: "cli.reflect.search",
    title: "Reflect Search",
    description: "Search types and members in the offline reflection cache.",
    group: "cli",
    inputSchema: objSchema({ query: { type: "string" }, reflectDir: { type: "string" } }, ["query"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "reflect search",
    docsTopic: "reflect"
  },
  {
    name: "cli.package.resolve",
    title: "Package Resolve",
    description: "Resolve all packages in the project manifest by running Unity PackageManager. Runs as a batch job.",
    group: "cli",
    inputSchema: objSchema({
      project: { type: "string" },
      unity: { type: "string" },
      wait: { type: "boolean", default: true }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    longRunning: true,
    handler: "hybrid",
    cliCommand: "package resolve",
    docsTopic: "package",
    examples: [
      { description: "Resolve packages and wait", argv: ["package", "resolve", "--project", "MyProject", "--unity", "Unity.exe", "--wait"] }
    ]
  },
  {
    name: "cli.package.registry",
    title: "Package Registry",
    description: "Get, set, or clear the primary registry URL in the project manifest.",
    group: "cli",
    inputSchema: objSchema({
      action: { type: "string", enum: ["get", "set", "clear"] },
      url: { type: "string" },
      project: { type: "string" }
    }, ["action"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "package registry",
    docsTopic: "package",
    examples: [
      { description: "Get current registry", argv: ["package", "registry", "get"] },
      { description: "Set registry URL", argv: ["package", "registry", "set", "--url", "https://packages.unity.com"] }
    ]
  },
  {
    name: "cli.package.scoped-registry",
    title: "Package Scoped Registry",
    description: "List, add, or remove scoped registries in the project manifest.",
    group: "cli",
    inputSchema: objSchema({
      action: { type: "string", enum: ["list", "add", "remove"] },
      name: { type: "string" },
      url: { type: "string" },
      scope: { type: "string" },
      scopes: { type: "string" },
      project: { type: "string" }
    }, ["action"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "package scoped-registry",
    docsTopic: "package",
    examples: [
      { description: "List scoped registries", argv: ["package", "scoped-registry", "list"] },
      { description: "Add scoped registry", argv: ["package", "scoped-registry", "add", "--name", "MyRegistry", "--url", "https://my.registry.com", "--scope", "com.mycompany"] }
    ]
  },
  {
    name: "test.run",
    title: "Test Run",
    description: "Run Unity EditMode or PlayMode tests as a batch job. Returns a job ID that can be polled for status.",
    group: "test",
    inputSchema: objSchema({
      platform: { type: "string", enum: ["EditMode", "PlayMode"], default: "EditMode" },
      project: { type: "string" },
      unity: { type: "string" },
      wait: { type: "boolean" },
      timeoutMs: { type: "integer" }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    longRunning: true,
    handler: "job",
    cliCommand: "test run",
    docsTopic: "test",
    examples: [
      { description: "Run EditMode tests as job", argv: ["test", "run"] },
      { description: "Run PlayMode tests and wait", argv: ["test", "run", "--platform", "PlayMode", "--wait"] }
    ]
  },
  {
    name: "test.poll",
    title: "Test Poll",
    description: "Poll the status of a running test job. Equivalent to `job status --id <jobId>`.",
    group: "test",
    inputSchema: objSchema({ id: { type: "string" } }, ["id"]),
    outputSchema: objSchema({
      jobId: { type: "string" },
      kind: { type: "string" },
      status: { type: "string" },
      exitCode: { type: "integer" },
      logPath: { type: "string" }
    }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "test poll",
    docsTopic: "test",
    examples: [
      { description: "Poll test job status", argv: ["test", "poll", "--id", "job_abc123"] }
    ]
  },
  {
    name: "build.run",
    title: "Build Run",
    description: "Trigger a Unity project build as a batch job. Returns a job ID that can be polled for status.",
    group: "build",
    inputSchema: objSchema({
      target: { type: "string", enum: ["StandaloneWindows64", "StandaloneWindows", "StandaloneOSX", "StandaloneLinux64", "WebGL", "iOS", "Android"], default: "StandaloneWindows64" },
      output: { type: "string" },
      project: { type: "string" },
      unity: { type: "string" },
      wait: { type: "boolean" },
      timeoutMs: { type: "integer" }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    longRunning: true,
    handler: "job",
    cliCommand: "build run",
    docsTopic: "build",
    examples: [
      { description: "Build for Windows64 as job", argv: ["build", "run"] },
      { description: "Build and wait for completion", argv: ["build", "run", "--target", "StandaloneWindows64", "--wait"] }
    ]
  },
  {
    name: "build.status",
    title: "Build Status",
    description: "Check the status of a running build job. Equivalent to `job status --id <jobId>`.",
    group: "build",
    inputSchema: objSchema({ id: { type: "string" } }, ["id"]),
    outputSchema: objSchema({
      jobId: { type: "string" },
      kind: { type: "string" },
      status: { type: "string" },
      exitCode: { type: "integer" },
      logPath: { type: "string" }
    }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "build status",
    docsTopic: "build",
    examples: [
      { description: "Check build job status", argv: ["build", "status", "--id", "job_abc123"] }
    ]
  },
  {
    name: "build.cancel",
    title: "Build Cancel",
    description: "Cancel a running build job. Equivalent to `job cancel --id <jobId>`.",
    group: "build",
    inputSchema: objSchema({ id: { type: "string" } }, ["id"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "offline",
    cliCommand: "build cancel",
    docsTopic: "build",
    examples: [
      { description: "Cancel a build job", argv: ["build", "cancel", "--id", "job_abc123"] }
    ]
  }
];

// ── Aggregate all built-in tools ───────────────────────────

const BUILTIN_TOOLS: ToolMetadata[] = [
  ...SCENE_TOOLS,
  ...CAMERA_TOOLS,
  ...GO_TOOLS,
  ...COMPONENT_TOOLS,
  ...ASSET_TOOLS,
  ...PREFAB_TOOLS,
  ...MATERIAL_TOOLS,
  ...GRAPHICS_TOOLS,
  ...PHYSICS_TOOLS,
  ...PROFILER_TOOLS,
  ...PROBUILDER_TOOLS,
  ...CUSTOM_TOOL_TOOLS,
  ...SHADER_TOOLS,
  ...TEXTURE_TOOLS,
  ...ANIMATION_TOOLS,
  ...VFX_TOOLS,
  ...UI_TOOLS,
  ...SCRIPT_TOOLS,
  ...EDITOR_TOOLS,
  ...DIAGNOSE_TOOLS,
  ...SCREENSHOT_TOOLS,
  ...CODE_TOOLS,
  ...CLI_ONLY_TOOLS
];