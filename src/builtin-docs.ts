import { getString } from "./args.js";
import { allRegisteredToolMetadata, supportStatusForTool, toolCliCommand, toolDocsTopic, type ToolMetadata } from "./tools/metadata/index.js";
import { failure, success } from "./output.js";
import type { CliResult } from "./types.js";

export interface BuiltinDocsRuntime {
  projectTools?: ToolMetadata[];
  fetch?: (url: string) => Promise<{ ok: boolean; status: number; text: () => Promise<string> }>;
}

interface DocTopic {
  key: string;
  title: string;
  summary: string;
  tools: string[];
  guide?: string;
}

const STATIC_TOPIC_DOCS: Record<string, Omit<DocTopic, "key" | "tools"> & { tools?: string[] }> = {
  "target-and-instance": {
    title: "Targets and Instances",
    summary: "Discover Unity instances, select a target, and understand instance lifecycle. This is the first step in the discovery path: instances → select target → tools discovery → tools run → verify. Do not read registration files or ping endpoints manually — always use CLI commands.",
    guide: "Start with `instances list` to discover running Unity Editors. Use `target set <id|alias>` to select a target before executing any Bridge tool. When multiple instances are ready, the CLI returns E_TARGET_AMBIGUOUS instead of guessing. Never manually read registration files or ping bridge endpoints."
  },
  "tool-discovery": {
    title: "Tool Discovery",
    summary: "Group-first tool discovery surface. All tools — built-in and project-local — share the same descriptor schema. Use `tools describe <name>` before executing any tool. Descriptor includes `target`, `availabilityScope`, `supportStatus`, and `playModeSupport` for runtime-aware routing.",
    guide: "Run `tools group` to list all tool groups, then `tools group list <group>` to see tools in a group. Use `tools search <keyword>` when you know the domain. Always `tools describe <name>` before executing."
  },
  "tool-execution": {
    title: "Tool Execution",
    summary: "Execute tools via `tools run <name>` — the unified entry point. Execution channels: bridge (live Editor), bridge-runtime (Play Mode runtime), hybrid (bridge + job), job (batch), or offline (no Unity). Target=editor|runtime|both determines internal routing; runtime is not a separate command group.",
    guide: "Use `tools run <name>` to execute any tool. Read the `handler`, `supportStatus`, `target`, and `availabilityScope` from `tools describe` to know the execution channel. Use `--dry-run` for destructive tools that support it. Verify results by re-calling read tools or checking `plannedEffects`."
  },
  "availability-differences": {
    title: "Availability Differences",
    summary: "Why different Unity instances return different tool availability: package dependencies, project tool registration, Bridge reachability, PlayMode state, and domain reload timing.",
    guide: "Always resolve target first. Use `tools group` then `tools group list <group> --include-custom` against the specific instance. After package installs or domain reload, re-discover tools. Handle package-not-found and E_RUNTIME_NOT_READY errors using `nextActions`."
  },
  "error-codes": {
    title: "Error Codes and nextActions",
    summary: "Structured error taxonomy with machine-readable codes, transient hints, `retryAfterMs`, and `nextActions` for automated recovery. Error JSON fields use camelCase and match error-code.schema.json. Every error follows the unified response envelope.",
    guide: "Read `error.code` for the error category. Check `transient` — if true, retry after `retryAfterMs`. Follow `nextActions` for recovery. Never guess; use the suggested commands."
  },
  "risk-and-side-effects": {
    title: "Risk and Side Effects",
    summary: "Risk model expressed through unified tool descriptors: destructiveHint, supportsDryRun, --yes confirmation gate, plannedEffects preview, and fingerprint integrity.",
    guide: "Always `tools describe` first. For destructive tools, use `--dry-run` if supported, then `--yes`. Check `doctor` for fingerprint status."
  },
  scene: {
    title: "Scene Commands",
    summary: "Query and modify Unity scenes: hierarchy, active scene, build settings, screenshots, create/load/save. Handler: bridge. Target: editor.",
    guide: "Start with `scene hierarchy` to list objects. Use `scene screenshot` for visual verification."
  },
  camera: {
    title: "Camera Commands",
    summary: "Inspect cameras and capture camera-specific screenshots. Handler: bridge. Target: editor.",
    guide: "Use `tools run camera.list` to find camera names, then `tools run camera.info --input '{\"target\":\"<camera>\"}'` for details."
  },
  go: {
    title: "GameObject Commands",
    summary: "Find, inspect, create, modify and delete GameObjects in the current scene/session. Handler: bridge. Target: editor.",
    guide: "Use `go find --name <name>` to locate objects in the active scene hierarchy."
  },
  component: {
    title: "Component Commands",
    summary: "Add, remove and set properties on Unity components in the current scene/session. Handler: bridge. Target: editor.",
  },
  asset: {
    title: "Asset Commands",
    summary: "Search, inspect and manage project assets. Handler: bridge. Target: editor. `asset.delete` is destructive (supports --dry-run, requires --yes).",
  },
  prefab: {
    title: "Prefab Commands",
    summary: "Create, inspect and edit Unity prefabs. Handler: bridge. Target: editor.",
  },
  material: {
    title: "Material Commands",
    summary: "Create, inspect and modify materials. Handler: bridge. Target: editor.",
  },
  graphics: {
    title: "Graphics Commands",
    summary: "Query and modify graphics quality and rendering settings. Handler: bridge. Target: editor.",
  },
  physics: {
    title: "Physics Commands",
    summary: "Query physics settings, perform raycasts and step simulation. Handler: bridge. Target: editor/runtime.",
  },
  profiler: {
    title: "Profiler Commands",
    summary: "Control the Unity profiler, read counters, query memory and export frame data. Handler: bridge + hybrid (capture). Target: editor.",
    guide: "Start with `profiler status`, then `profiler start`, run your workload, inspect `profiler counter` / `profiler memory`, and `profiler export <path>`."
  },
  probuilder: {
    title: "ProBuilder Commands",
    summary: "Create and edit ProBuilder meshes, including CSG boolean operations. Handler: hybrid (bridge + job). Target: editor. Requires com.unity.probuilder package. Availability depends on target instance packages.",
    guide: "Use `--job --wait` when you want durable batch/job execution for supported ProBuilder commands."
  },
  shader: {
    title: "Shader Commands",
    summary: "Inspect, create and update shader assets. Handler: bridge. Target: editor.",
  },
  texture: {
    title: "Texture Commands",
    summary: "Inspect, create and configure texture assets. Handler: bridge. Target: editor.",
  },
  animation: {
    title: "Animation Commands",
    summary: "Query and control animation clips and states. Handler: bridge. Target: editor/runtime.",
  },
  vfx: {
    title: "VFX Commands",
    summary: "Manage Visual Effect graphs and instances. Handler: bridge. Target: editor. Requires com.unity.visualeffectgraph package. Availability depends on target instance packages.",
  },
  ui: {
    title: "UI Commands",
    summary: "Create and inspect UI elements (Canvas, Text, Button, Image, UI Document). Handler: bridge + offline fallback. Target: editor.",
  },
  editor: {
    title: "Editor Commands",
    summary: "Control the Unity Editor: play mode, console, menu items, live compile requests, compile diagnostics, refresh jobs, and Windows launch-dialog dismiss. Handlers: bridge (state/play/pause/stop/menu/console/compile), hybrid (compile-diagnostics), job (refresh), offline (launch-dialog.dismiss). Target: editor. `editor.menu` is destructive (requires --yes).",
  },
  diagnose: {
    title: "Diagnose Commands",
    summary: "Run project and bridge health checks. Handler: bridge. Target: editor.",
  },
  screenshot: {
    title: "Screenshot Commands",
    summary: "Capture game-view screenshots. Handler: bridge. Target: editor.",
  },
  code: {
    title: "Code Execution",
    summary: "Execute C# code in the Unity Editor. Handler: hybrid (live bridge + batch/job). Target: editor|runtime|both. Destructive (supports --dry-run, requires --yes). Channel: --channel live|batchmode.",
    guide: "Use `--channel live` for quick snippets, `--channel batchmode` for larger scripts. Always pass `--timeout-ms` for intentionally long-running code."
  },
  cli: {
    title: "CLI Management Commands",
    summary: "Project initialization, package management, batch execution, and offline tools. Handler: offline. Target: none (no Unity required).",
  },
  init: {
    title: "Project Initialization",
    summary: "Deploy the Unity CLI Bridge package into a project. Handler: offline. Target: none.",
  },
  package: {
    title: "Package Management",
    summary: "List, search, install, remove, deploy and restore Unity packages from the CLI. Handlers: offline (list/info/search/registry/deploy/restore), hybrid (install/remove/resolve). Target: editor for resolve and refresh. `package.install`, `package.remove`, `package.deploy`, `package.restore`, and `package.bridge-remove` are destructive; deploy/restore support --dry-run.",
  },
  batch: {
    title: "Batch Execution",
    summary: "Execute a JSON file containing multiple CLI commands in sequence or parallel. Handler: offline. Target: routed per-step. `batch run` is destructive (supports --preflight dry-run, requires --yes).",
  },
  docs: {
    title: "Built-in and Offline Documentation",
    summary: "Read built-in CLI topic docs, or query/search the offline Unity API documentation cache with `tools run docs.get` / `tools run docs.search`. Handler: offline. Target: none.",
    guide: "Run `unity_cli tools run docs.list` to list built-in topics. Use `unity_cli tools run docs.get --input '{\"api\":\"UnityEngine.Camera\",\"docsDir\":\"<path>\"}'` when querying a populated Unity API cache."
  },
  reflect: {
    title: "Offline Reflection",
    summary: "Query the offline C# reflection cache for types and members. Handler: offline. Target: none.",
    guide: "Populate the reflect cache first, then query with `reflect type <name>` or `reflect search <query>`."
  },
  test: {
    title: "Test Commands",
    summary: "Run Unity tests as jobs. Inspect or cancel them with top-level `jobs status/logs/cancel`. Handler: job (run). Target: editor (batch job).",
  },
  build: {
    title: "Build Commands",
    summary: "Run Unity builds as jobs. Inspect or cancel them with top-level `jobs status/logs/cancel`. Handler: job (run). Target: editor (batch job).",
  },
  "project-tool": {
    title: "Project Tools (Custom)",
    summary: "Tools registered by Unity project C# code via IUnityCliTool<TReq,TRes>. Discovered through `tools group` and `tools group list <group> --include-custom`. Executed via `tools run <name>`. Handler: bridge or bridge-runtime. Target: editor or runtime. Availability depends on target instance and PlayMode state. Fingerprint-protected.",
    guide: "Use `tools group` to list groups, then `tools group list <group> --include-custom` to discover project tools. Use `tools describe <name>` for full schema, risk hints, and target/availability. Use `tools run <name>` to execute."
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
  const topics = buildTopics(runtime.projectTools)
    .map((topic) => ({ key: topic.key, title: topic.title, summary: topic.summary, toolCount: topic.tools.length }))
    .sort((a, b) => a.key.localeCompare(b.key));
  const format = getString(flags, "format");
  const data: Record<string, unknown> = {
    command: "docs.list",
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
    return failure(requestId, "DOCS_TOPIC_REQUIRED", "Pass a topic: unity_cli tools run docs.topic <topic>.", ["Run unity_cli tools run docs.list to list built-in topics."]);
  }

  const doc = buildTopics(runtime.projectTools).find((candidate) => candidate.key === topic);
  if (!doc) {
    const available = buildTopics(runtime.projectTools).map((candidate) => candidate.key).sort();
    return failure(requestId, "DOCS_TOPIC_NOT_FOUND", `Documentation topic not found: ${topic}.`, [
      `Available topics: ${available.join(", ")}.`,
      "Run unity_cli tools group to see tool groups, then tools group list <group> to see tools in a group."
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

export function buildTopics(projectTools: ToolMetadata[] = []): DocTopic[] {
  const byTopic = new Map<string, ToolMetadata[]>();
  for (const tool of [...allRegisteredToolMetadata(), ...projectTools]) {
    const topic = toolDocsTopic(tool);
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
  const status = supportStatusForTool(tool);
  const target = tool.target ?? "editor";
  const notes = [
    `supportStatus:${status}`,
    tool.destructiveHint ? "destructive" : undefined,
    `target:${target}`
  ].filter(Boolean).join(", ");
  return `${tool.name} (${toolCliCommand(tool)}) - ${tool.summary}; ${notes}.`;
}

function titleFromKey(key: string): string {
  return key.split(/[-_.]/).filter(Boolean).map((part) => `${part[0]?.toUpperCase() ?? ""}${part.slice(1)}`).join(" ") || key;
}
