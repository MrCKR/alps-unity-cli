import { getString } from "./args.js";
import { builtinToolMetadata, supportStatusDescription, supportStatusForTool, supportStatusLabel, type ToolMetadata } from "./tools/metadata/index.js";
import { failure, success } from "./output.js";
import type { CliResult } from "./types.js";

export interface BuiltinDocsRuntime {
  customTools?: ToolMetadata[];
}

interface DocTopic {
  key: string;
  title: string;
  summary: string;
  tools: string[];
  guide?: string;
}

const STATIC_TOPIC_DOCS: Record<string, Omit<DocTopic, "key" | "tools"> & { tools?: string[] }> = {
  scene: {
    title: "Scene Commands",
    summary: "Query and modify Unity scenes: hierarchy, active scene, build settings, screenshots, create/load/save.",
    guide: "Start with `scene hierarchy` to list objects. Use `scene screenshot` for visual verification."
  },
  camera: {
    title: "Camera Commands",
    summary: "Inspect cameras and capture camera-specific screenshots.",
    guide: "Use `camera list` to find camera names, then `camera info <target>` for details."
  },
  go: {
    title: "GameObject Commands",
    summary: "Find, inspect, create, modify and delete GameObjects in the scene hierarchy.",
    guide: "Use `go find --name <name>` to locate objects in the active scene hierarchy."
  },
  component: {
    title: "Component Commands",
    summary: "Add, remove and set properties on Unity components."
  },
  asset: {
    title: "Asset Commands",
    summary: "Search, inspect and manage project assets."
  },
  prefab: {
    title: "Prefab Commands",
    summary: "Create, inspect and edit Unity prefabs."
  },
  material: {
    title: "Material Commands",
    summary: "Create, inspect and modify materials."
  },
  graphics: {
    title: "Graphics Commands",
    summary: "Query and modify graphics quality and rendering settings."
  },
  physics: {
    title: "Physics Commands",
    summary: "Query physics settings, perform raycasts and step simulation."
  },
  profiler: {
    title: "Profiler Commands",
    summary: "Control the Unity profiler, read counters, query memory and export frame data. Architecture-sensitive ALP-165-Q4 frozen scope: session, counter, memory, frame debugger and export must remain fully covered.",
    guide: "Start with `profiler status`, then `profiler start`, run your workload, inspect `profiler counter` / `profiler memory`, and `profiler export <path>`. Do not downgrade this domain to optional without architecture review because ALP-165-Q4 freezes Profiler as complete coverage."
  },
  probuilder: {
    title: "ProBuilder Commands",
    summary: "Create and edit ProBuilder meshes, including CSG boolean operations. Runs through live Bridge and optional Unity job paths.",
    guide: "Use `--job --wait` when you want durable batch/job execution for supported ProBuilder commands."
  },
  "custom-tool": {
    title: "Custom Tool Commands",
    summary: "List and run project-specific custom tools registered with the Unity CLI Bridge.",
    guide: "Use `custom-tool list` for raw project descriptors, or `tools list --include-custom` / `tools describe <custom.name>` for unified discovery with built-ins."
  },
  shader: {
    title: "Shader Commands",
    summary: "Inspect, create and update shader assets."
  },
  texture: {
    title: "Texture Commands",
    summary: "Inspect, create and configure texture assets."
  },
  animation: {
    title: "Animation Commands",
    summary: "Query and control animation clips and states."
  },
  vfx: {
    title: "VFX Commands",
    summary: "Manage Visual Effect graphs and instances."
  },
  ui: {
    title: "UI Commands",
    summary: "Create and inspect UI elements (Canvas, Text, Button, Image, UI Document)."
  },
  script: {
    title: "Script Commands",
    summary: "Create, read, edit and validate C# script files."
  },
  editor: {
    title: "Editor Commands",
    summary: "Control the Unity Editor: play mode, console, menu items and refresh jobs."
  },
  diagnose: {
    title: "Diagnose Commands",
    summary: "Run project and bridge health checks."
  },
  screenshot: {
    title: "Screenshot Commands",
    summary: "Capture game-view screenshots."
  },
  code: {
    title: "Code Execution",
    summary: "Execute C# code in the Unity Editor. Supports live and batch mode channels.",
    guide: "Use `--channel live` for quick snippets, `--channel batchmode` for larger scripts. Always pass `--timeout-ms` for intentionally long-running code."
  },
  cli: {
    title: "CLI Management Commands",
    summary: "Project initialization, package management, batch execution and offline documentation."
  },
  init: {
    title: "Project Initialization",
    summary: "Deploy the Unity CLI Bridge package into a project."
  },
  package: {
    title: "Package Management",
    summary: "List, search, install and remove Unity packages from the CLI."
  },
  batch: {
    title: "Batch Execution",
    summary: "Execute a JSON file containing multiple CLI commands in sequence or parallel."
  },
  docs: {
    title: "Built-in and Offline Documentation",
    summary: "Read built-in CLI topic docs, or query/search the offline Unity API documentation cache with `docs get` / `docs search`.",
    guide: "Run `unity_cli docs` to list built-in topics. Use `docs get <api> --docs-dir <path>` when querying a populated Unity API cache."
  },
  reflect: {
    title: "Offline Reflection",
    summary: "Query the offline C# reflection cache for types and members.",
    guide: "Populate the reflect cache first, then query with `reflect type <name>` or `reflect search <query>`."
  },
  test: {
    title: "Test Commands",
    summary: "Run Unity tests as jobs and inspect status/logs."
  },
  build: {
    title: "Build Commands",
    summary: "Run Unity builds as jobs and inspect status/logs."
  }
};

export function handleBuiltinDocsCommand(
  command: string | undefined,
  args: string[],
  flags: Record<string, string | boolean>,
  runtime: BuiltinDocsRuntime,
  requestId: string
): CliResult | undefined {
  if (!command || command === "topics" || command === "list") {
    return listBuiltinDocs(flags, runtime, requestId);
  }

  if (command === "topic") {
    return getBuiltinDocTopic(args[0] ?? getString(flags, "topic"), flags, runtime, requestId);
  }

  return undefined;
}

export function listBuiltinDocs(
  flags: Record<string, string | boolean>,
  runtime: BuiltinDocsRuntime,
  requestId: string
): CliResult {
  const topics = buildTopics(runtime.customTools)
    .map((topic) => ({ key: topic.key, title: topic.title, summary: topic.summary, toolCount: topic.tools.length }))
    .sort((a, b) => a.key.localeCompare(b.key));
  const format = getString(flags, "format");
  const data: Record<string, unknown> = {
    command: "docs.topics",
    mode: "builtin",
    requiresUnity: false,
    count: topics.length,
    topics
  };
  if (format === "text") {
    data.text = topics.map((t) => `${t.key} (${t.toolCount} tools) — ${t.title}: ${t.summary}`).join("\n");
  }
  return success(requestId, data);
}

export function getBuiltinDocTopic(
  topic: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: BuiltinDocsRuntime,
  requestId: string
): CliResult {
  if (!topic) {
    return failure(requestId, "DOCS_TOPIC_REQUIRED", "Pass a topic: unity_cli docs topic <topic>.", ["Run unity_cli docs to list built-in topics."]);
  }

  const doc = buildTopics(runtime.customTools).find((candidate) => candidate.key === topic);
  if (!doc) {
    const available = buildTopics(runtime.customTools).map((candidate) => candidate.key).sort();
    return failure(requestId, "DOCS_TOPIC_NOT_FOUND", `Documentation topic not found: ${topic}.`, [
      `Available topics: ${available.join(", ")}.`,
      "Run unity_cli tools list to see tools grouped by topic."
    ]);
  }

  const format = getString(flags, "format");
  const data: Record<string, unknown> = {
    command: "docs.topic",
    mode: "builtin",
    requiresUnity: false,
    topic: doc.key,
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

export function buildTopics(customTools: ToolMetadata[] = []): DocTopic[] {
  const byTopic = new Map<string, ToolMetadata[]>();
  for (const tool of [...builtinToolMetadata(), ...customTools]) {
    const topic = tool.docsTopic ?? tool.group;
    const tools = byTopic.get(topic) ?? [];
    tools.push(tool);
    byTopic.set(topic, tools);
  }

  const keys = new Set([...Object.keys(STATIC_TOPIC_DOCS), ...byTopic.keys()]);
  return [...keys].map((key) => {
    const staticDoc = STATIC_TOPIC_DOCS[key];
    const tools = byTopic.get(key) ?? [];
    const title = staticDoc?.title ?? titleFromKey(key);
    const summary = staticDoc?.summary ?? `${title} registered by Unity project custom tools.`;
    return {
      key,
      title,
      summary,
      tools: staticDoc?.tools ?? tools.map(toolLine),
      guide: staticDoc?.guide
    };
  }).sort((a, b) => a.key.localeCompare(b.key));
}

function toolLine(tool: ToolMetadata): string {
  const status = supportStatusLabel(supportStatusForTool(tool));
  const notes = [
    status,
    tool.readOnlyHint ? "read-only" : "write",
    tool.destructiveHint ? "⚠ destructive" : undefined,
    tool.architectureSensitive ? `architecture-sensitive${tool.frozenDecision ? ` ${tool.frozenDecision}` : ""}` : undefined
  ].filter(Boolean).join(", ");
  return `${tool.name}${tool.cliCommand ? ` (${tool.cliCommand})` : ""} — ${tool.title}; ${notes}. ${supportStatusDescription(supportStatusForTool(tool))}`;
}

function titleFromKey(key: string): string {
  return key.split(/[-_.]/).filter(Boolean).map((part) => `${part[0]?.toUpperCase() ?? ""}${part.slice(1)}`).join(" ") || key;
}
