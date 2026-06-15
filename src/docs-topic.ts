/**
 * CLI command: docs-topic <topic>
 *
 * Returns built-in documentation content for a given topic key.
 * Topic keys are derived from tool metadata `docsTopic` fields
 * and the offline docs/reflect caches.
 */

import { getString } from "./args.js";
import { builtinToolGroups, builtinToolMetadata } from "./tools/metadata/index.js";
import { failure, success } from "./output.js";
import type { CliResult } from "./types.js";

const TOPIC_DOCS: Record<string, DocTopic> = {
  scene: {
    title: "Scene Commands",
    summary: "Query and modify Unity scenes: hierarchy, active scene, build settings, screenshots, create/load/save.",
    tools: [
      "scene.hierarchy — List root GameObjects in the active scene",
      "scene.screenshot — Capture viewport screenshot to file",
      "scene.active — 规划中: get name, path and build index of the active scene",
      "scene.build-settings — 规划中: list scenes in build settings",
      "scene.create — 规划中: create a new empty scene",
      "scene.load — 规划中: load a scene by path or build index",
      "scene.save — 规划中: save the active scene (destructive: overwrites file)"
    ],
    guide: "Start with `scene hierarchy` to list objects. Use `scene screenshot` for visual verification."
  },
  camera: {
    title: "Camera Commands",
    summary: "Inspect cameras and capture camera-specific screenshots.",
    tools: [
      "camera.list — List all cameras in the scene",
      "camera.info — Get camera properties (position, FOV, etc.)",
      "camera.screenshot — Render from a specific camera to file"
    ],
    guide: "Use `camera list` to find camera names, then `camera info <target>` for details."
  },
  go: {
    title: "GameObject Commands",
    summary: "Find, inspect, create, modify and delete GameObjects in the scene hierarchy.",
    tools: [
      "go.find — Find a GameObject by name",
      "go.inspect — 规划中: get components and transform data",
      "go.create — 规划中: create a new empty GameObject",
      "go.modify — 规划中: modify transform or properties",
      "go.delete — 规划中: delete a GameObject (⚠ destructive)"
    ],
    guide: "Use `go find --name <name>` to locate objects in the active scene hierarchy."
  },
  component: {
    title: "Component Commands",
    summary: "Add, remove and set properties on Unity components.",
    tools: [
      "component.add — 规划中: add a component to a GameObject",
      "component.remove — 规划中: remove a component (⚠ destructive)",
      "component.set — 规划中: set a component property value"
    ]
  },
  asset: {
    title: "Asset Commands",
    summary: "Search, inspect and manage project assets.",
    tools: [
      "asset.search — Search the asset database",
      "asset.info — Get asset metadata",
      "asset.mkdir — Create an asset folder",
      "asset.import — Import a file into the project",
      "asset.move — Move an asset",
      "asset.delete — Delete an asset (⚠ destructive)"
    ]
  },
  prefab: {
    title: "Prefab Commands",
    summary: "Create, inspect and edit Unity prefabs.",
    tools: [
      "prefab.info — Get prefab metadata",
      "prefab.create — Create prefab from a GameObject",
      "prefab.stage-open — Open prefab for editing",
      "prefab.stage-save — Save prefab edits",
      "prefab.stage-close — Close prefab editing stage",
      "prefab.hierarchy — List prefab child hierarchy"
    ]
  },
  material: {
    title: "Material Commands",
    summary: "Create, inspect and modify materials.",
    tools: [
      "material.info — Get shader and properties",
      "material.create — Create a new material",
      "material.assign — Assign material to renderer",
      "material.set — Set a material property"
    ]
  },
  graphics: {
    title: "Graphics Commands",
    summary: "Query and modify graphics quality and rendering settings.",
    tools: [
      "graphics.quality — Get/set quality level and VSync",
      "graphics.settings — Get/set anti-aliasing and shadows",
      "graphics.screenshot — Capture with diagnostics overlay"
    ]
  },
  physics: {
    title: "Physics Commands",
    summary: "Query physics settings, perform raycasts and step simulation.",
    tools: [
      "physics.settings — Get/set physics parameters",
      "physics.raycast — Cast a ray into the scene",
      "physics.simulation — Step physics forward"
    ]
  },
  profiler: {
    title: "Profiler Commands",
    summary: "Control the Unity profiler: start, stop, capture frame data.",
    tools: [
      "profiler.status — Check if profiler is active",
      "profiler.start — Begin profiling",
      "profiler.stop — Stop profiling",
      "profiler.capture — Capture profiler data to file (long-running)"
    ],
    guide: "Start with `profiler status`, then `profiler start`, run your workload, and `profiler capture <path>`."
  },
  probuilder: {
    title: "ProBuilder Commands",
    summary: "Create and edit ProBuilder meshes, including CSG boolean operations. Runs as a Unity batch job.",
    tools: [
      "probuilder.shape — Create a ProBuilder primitive",
      "probuilder.create — Create a ProBuilder mesh",
      "probuilder.edit — Edit a ProBuilder mesh",
      "probuilder.csg — CSG boolean operation (⚠ destructive)"
    ],
    guide: "Use `--job --wait` for ProBuilder commands that require Unity batch mode."
  },
  "custom-tool": {
    title: "Custom Tool Commands",
    summary: "List and run project-specific custom tools registered with the Unity CLI Bridge.",
    tools: [
      "custom-tool.list — List custom tools",
      "custom-tool.run — Execute a custom tool"
    ]
  },
  shader: {
    title: "Shader Commands",
    summary: "Inspect, create and update shader assets.",
    tools: [
      "shader.info — Get shader properties",
      "shader.read — Read shader source",
      "shader.create — Create a new shader",
      "shader.update — Update shader source"
    ]
  },
  texture: {
    title: "Texture Commands",
    summary: "Inspect, create and configure texture assets.",
    tools: [
      "texture.info — Get texture dimensions and format",
      "texture.create — Create a new texture",
      "texture.sprite — Create/update sprite from texture",
      "texture.import-settings — Get/set texture import settings"
    ]
  },
  animation: {
    title: "Animation Commands",
    summary: "Query and control animation clips and states.",
    tools: [
      "animation.clip — Get/create animation clip",
      "animation.state — Query current animation state",
      "animation.play — Play an animation state",
      "animation.stop — Stop playing animation"
    ]
  },
  vfx: {
    title: "VFX Commands",
    summary: "Manage Visual Effect graphs and instances.",
    tools: [
      "vfx.graph — Get/create VFX graph",
      "vfx.effect — Manage VFX instance on a GameObject"
    ]
  },
  ui: {
    title: "UI Commands",
    summary: "Create and inspect UI elements (Canvas, Text, Button, Image, UI Document).",
    tools: [
      "ui.canvas/create — Create a Canvas",
      "ui.text/create — Create a Text element",
      "ui.button/create — Create a Button element",
      "ui.image/create — Create an Image element",
      "ui.document/info — Inspect a UI Document",
      "ui.document/create — Create a UI Document",
      "ui.hierarchy — List UI hierarchy"
    ]
  },
  script: {
    title: "Script Commands",
    summary: "Create, read, edit and validate C# script files.",
    tools: [
      "script.create — 规划中: create a C# script",
      "script.read — 规划中: read script content",
      "script.edit — 规划中: edit a script (⚠ destructive)",
      "script.validate — 规划中: validate script syntax"
    ]
  },
  editor: {
    title: "Editor Commands",
    summary: "Control the Unity Editor: play mode, console, menu items.",
    tools: [
      "editor.console — Read console logs",
      "editor.state — Query Editor state",
      "editor.play — Enter play mode",
      "editor.pause — Pause play mode",
      "editor.stop — Stop play mode",
      "editor.menu — Execute a menu item (⚠ destructive)"
    ]
  },
  diagnose: {
    title: "Diagnose Commands",
    summary: "Run project and bridge health checks.",
    tools: [
      "diagnose.preflight — Run preflight diagnostics"
    ]
  },
  screenshot: {
    title: "Screenshot Commands",
    summary: "Capture game-view screenshots.",
    tools: [
      "screenshot.capture — Capture a screenshot to file"
    ]
  },
  code: {
    title: "Code Execution",
    summary: "Execute C# code in the Unity Editor. Supports live and batch mode channels.",
    tools: [
      "code.exec — Execute C# code (⚠ destructive, long-running)"
    ],
    guide: "Use `--channel live` for quick snippets, `--channel batchmode` for larger scripts. Always pass `--timeout-ms` for safety."
  },
  cli: {
    title: "CLI Management Commands",
    summary: "Project initialization, package management, batch execution and offline documentation.",
    tools: [
      "init — Deploy bridge and initialize project",
      "package list/info/search/install/remove — Package management",
      "batch run — Execute a batch of commands",
      "docs get/search — Offline Unity API docs",
      "reflect type/member/search — Offline reflection cache"
    ]
  },
  init: {
    title: "Project Initialization",
    summary: "Deploy the Unity CLI Bridge package into a project.",
    tools: ["init — Initialize a project with the bridge package"]
  },
  package: {
    title: "Package Management",
    summary: "List, search, install and remove Unity packages from the CLI.",
    tools: [
      "package list — List installed packages",
      "package info — Show package details",
      "package search — Search registry",
      "package install — Install a package",
      "package remove — Remove a package"
    ]
  },
  batch: {
    title: "Batch Execution",
    summary: "Execute a JSON file containing multiple CLI commands in sequence or parallel.",
    tools: ["batch run — Run a batch file", "batch schema — Show batch JSON schema"]
  },
  docs: {
    title: "Offline Documentation",
    summary: "Query and search the offline Unity API documentation cache.",
    tools: [
      "docs get — Fetch documentation for an API",
      "docs search — Search documentation"
    ],
    guide: "Populate the docs cache first, then query with `docs get <api>` or `docs search <query>`."
  },
  reflect: {
    title: "Offline Reflection",
    summary: "Query the offline C# reflection cache for types and members.",
    tools: [
      "reflect type — Look up a type",
      "reflect member — Look up a member",
      "reflect search — Search types and members"
    ],
    guide: "Populate the reflect cache first, then query with `reflect type <name>` or `reflect search <query>`."
  }
};

interface DocTopic {
  title: string;
  summary: string;
  tools: string[];
  guide?: string;
}

export function handleDocsTopicCommand(
  topic: string | undefined,
  flags: Record<string, string | boolean>,
  requestId: string
): CliResult {
  if (!topic) {
    return listTopics(flags, requestId);
  }

  const doc = TOPIC_DOCS[topic];
  if (!doc) {
    const available = Object.keys(TOPIC_DOCS).sort();
    return failure(requestId, "DOCS_TOPIC_NOT_FOUND", `Documentation topic not found: ${topic}.`, [
      `Available topics: ${available.join(", ")}.`,
      "Run unity_cli tools list to see tools grouped by topic."
    ]);
  }

  const format = getString(flags, "format");
  const data: Record<string, unknown> = {
    command: "docs-topic",
    topic,
    title: doc.title,
    summary: doc.summary,
    tools: doc.tools,
    ...(doc.guide ? { guide: doc.guide } : {})
  };

  if (format === "text") {
    const lines = [
      `# ${doc.title}`,
      "",
      doc.summary,
      "",
      "Tools:",
      ...doc.tools.map((t) => `  - ${t}`)
    ];
    if (doc.guide) {
      lines.push("", "Guide:", `  ${doc.guide}`);
    }
    data.text = lines.join("\n");
  }

  return success(requestId, data);
}

function listTopics(flags: Record<string, string | boolean>, requestId: string): CliResult {
  const topics = Object.entries(TOPIC_DOCS)
    .map(([key, doc]) => ({ key, title: doc.title, summary: doc.summary, toolCount: doc.tools.length }))
    .sort((a, b) => a.key.localeCompare(b.key));

  const format = getString(flags, "format");
  const data: Record<string, unknown> = {
    command: "docs-topic",
    count: topics.length,
    topics
  };

  if (format === "text") {
    const lines = topics.map((t) => `${t.key} (${t.toolCount} tools) — ${t.title}: ${t.summary}`);
    data.text = lines.join("\n");
  }

  return success(requestId, data);
}