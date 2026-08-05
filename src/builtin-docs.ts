import { getString } from "./args.js";
import { staticBridgeToolMetadata, uniqueByName } from "./tools/catalog.js";
import { allRegisteredToolMetadata, toolDocsTopic, type ToolMetadata } from "./tools/metadata/index.js";
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
    guide: "Start with `instances list` to discover canonical Unity project identities. Bind every Bridge-backed command with `--instance <full-id|canonical-project-path|alias>` or run from the target project directory. A project claimed by multiple Unity Editor processes or sessions returns E_INSTANCE_CONFLICT instead of guessing. Never read registry files or invoke Bridge transport directly."
  },
  "tool-discovery": {
    title: "Tool Discovery",
    summary: "Group-first tool discovery surface. All tools — built-in and project-local — share the same descriptor schema. Use `tools describe <name>` before executing any tool. Descriptor includes schemas, side effects, `target`, `availabilityScope`, and `playModeSupport` for runtime-aware routing.",
    guide: "Run `tools group` to list all tool groups, then `tools group list <group>` to see tools in a group. Use `tools search <keyword>` when you know the domain. Always `tools describe <name>` before executing."
  },
  "tool-execution": {
    title: "Tool Execution",
    summary: "Execute tools via `tools run <name>` — the unified entry point. Unity Tools connect only to an open Editor and return final business data in the same direct call. Target=editor|runtime|both determines routing; runtime is not a separate command group.",
    guide: "Use `tools run <name>` to execute any tool. Read `target`, `availabilityScope`, schemas, and side effects from `tools describe`. Use `--dry-run` for destructive tools that support it. Verify results by re-calling read tools or checking `plannedEffects`."
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
    summary: "Risk model expressed through unified tool descriptors: destructiveHint, supportsDryRun, permissions, and plannedEffects preview.",
    guide: "Always `tools describe` first. Use `--dry-run` when a destructive tool supports a planned-effects preview. Use `doctor` for diagnostics."
  },
  scene: {
    title: "Scene Commands",
    summary: "Query and modify hierarchy, active scene, build settings, and loaded scenes in the selected open Unity Editor. Target: editor.",
    guide: "Start with `tools run scene.hierarchy --input '{\"pageSize\":50}'` and use the returned business readback to verify later scene edits."
  },
  camera: {
    title: "Camera Commands",
    summary: "Inspect cameras and capture camera-specific screenshots in the selected open Unity Editor. Target: editor.",
    guide: "Use `tools run camera.list` to find camera names, then `tools run camera.info --input '{\"target\":\"<camera>\"}'` for details."
  },
  go: {
    title: "GameObject Commands",
    summary: "Find, inspect, create, modify and delete GameObjects in the selected scene/session. Target: editor.",
    guide: "Use `tools run go.find --input '{\"name\":\"<name>\"}'` to locate objects in the active scene hierarchy."
  },
  component: {
    title: "Component Commands",
    summary: "Add, remove and set properties on Unity components in the selected scene/session. Target: editor.",
  },
  asset: {
    title: "Asset Commands",
    summary: "Search, inspect and manage assets through the selected open Unity Editor. Target: editor. `asset.delete` is destructive and supports --dry-run.",
  },
  prefab: {
    title: "Prefab Commands",
    summary: "Create, inspect and edit Unity prefabs through the selected open Unity Editor. Target: editor.",
  },
  material: {
    title: "Material Commands",
    summary: "Create, inspect and modify materials through the selected open Unity Editor. Target: editor.",
  },
  graphics: {
    title: "Graphics Commands",
    summary: "Query and modify graphics quality and rendering settings through the selected open Unity Editor. Target: editor.",
  },
  physics: {
    title: "Physics Commands",
    summary: "Query physics settings, perform raycasts and step simulation through the selected open Unity Editor. Target: editor/runtime.",
  },
  profiler: {
    title: "Profiler Commands",
    summary: "Control the Unity profiler, read counters, query memory and export frame data through the selected open Unity Editor. Target: editor.",
    guide: "Start with `tools run profiler.status --input '{}'`, then use `tools run profiler.start --input '{}'`, inspect counters or memory with their named Tools, and finish with `tools run profiler.export --input '{\"path\":\"<path>\"}'`."
  },
  probuilder: {
    title: "ProBuilder Commands",
    summary: "Create and edit ProBuilder meshes, including CSG boolean operations. Runs direct-final against an open Unity Editor. Target: editor. Requires com.unity.probuilder package. Availability depends on target instance packages.",
    guide: "Run ProBuilder tools against an open Unity Editor and use their terminal mesh readback to verify the requested edit."
  },
  shader: {
    title: "Shader Commands",
    summary: "Inspect, create and update shader assets through the selected open Unity Editor. Target: editor.",
  },
  texture: {
    title: "Texture Commands",
    summary: "Inspect, create and configure texture assets through the selected open Unity Editor. Target: editor.",
  },
  animation: {
    title: "Animation Commands",
    summary: "Query and control animation clips and states through the selected open Unity Editor. Target: editor/runtime.",
  },
  vfx: {
    title: "VFX Commands",
    summary: "Manage Visual Effect graphs and instances through the selected open Unity Editor. Target: editor. Requires com.unity.visualeffectgraph package. Availability depends on target instance packages.",
  },
  ui: {
    title: "UI Commands",
    summary: "Create and inspect UI elements and UI document assets through the selected open Unity Editor. Target: editor.",
  },
  editor: {
    title: "Editor Commands",
    summary: "Control and observe the Unity Editor: play mode, console, menu items, compile, refresh, and current desktop dialogs. Use `editor.dialogs` to read the current dialog and `editor.dialog.click` with its returned dialogId and enabled button label to act on it. Unity actions run direct-final against the open Editor. Target: editor. `editor.menu` is destructive.",
  },
  diagnose: {
    title: "Diagnose Commands",
    summary: "Run project and selected Editor health checks. Target: editor.",
  },
  screenshot: {
    title: "Screenshot Commands",
    summary: "Capture game-view screenshots from the selected open Unity Editor. Target: editor.",
  },
  code: {
    title: "Code Execution",
    summary: "Execute C# code in the selected open Unity Editor. Target: editor|runtime|both. Destructive and supports --dry-run.",
    guide: "Pass `--timeout-ms` for intentionally long-running code. completionProbe is optional; provide it with a read-only business-state assertion when an Editor state change needs proof."
  },
  cli: {
    title: "CLI Management Commands",
    summary: "Project initialization, package management, and offline tools. Target: none when no Unity action is required.",
  },
  init: {
    title: "Project Initialization",
    summary: "Deploy the Unity CLI package into a project from the local CLI. Target: none.",
  },
  package: {
    title: "Package Management",
    summary: "List, search, install, remove, deploy and restore Unity packages from the CLI. Package actions that refresh Unity run direct-final against the open Editor. `package.install`, `package.remove`, `package.deploy`, `package.restore`, and `package.bridge-remove` are destructive; deploy/restore support --dry-run.",
  },
  docs: {
    title: "Built-in and Offline Documentation",
    summary: "Read built-in CLI topic docs, or query/search the offline Unity API documentation cache with `tools run docs.get` / `tools run docs.search`. Target: none.",
    guide: "Run `unity_cli tools run docs.list` to list built-in topics. Use `unity_cli tools run docs.get --input '{\"api\":\"UnityEngine.Camera\",\"docsDir\":\"<path>\"}'` when querying a populated Unity API cache."
  },
  reflect: {
    title: "Offline Reflection",
    summary: "Query the offline C# reflection cache for types and members. Target: none.",
    guide: "Populate the reflection cache first, then query it with `tools run reflect.type --input '{\"name\":\"<name>\"}'` or `tools run reflect.search --input '{\"query\":\"<query>\"}'`."
  },
  test: {
    title: "Test Commands",
    summary: "Run Unity tests direct-final against an open Unity Editor. The same call returns the final test business result.",
  },
  build: {
    title: "Build Commands",
    summary: "Run Unity builds direct-final against an open Unity Editor. The same call returns the final build business result.",
  },
  "project-tool": {
    title: "Project Tools (Custom)",
    summary: "Tools registered by Unity project C# code via IUnityCliTool<TReq,TRes>. Discovered through `tools group` and `tools group list <group> --include-custom`. Executed direct-final via `tools run <name>` against an open Editor. Target: editor or runtime. Availability depends on target instance and PlayMode state.",
    guide: "Use `tools group` to list groups, then `tools group list <group> --include-custom` to discover project tools. Use `tools describe <name>` for schemas, side effects, and target/availability. Use `tools run <name>` to execute."
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

/** 合并离线可发现的 TS/C#/Extension catalog 与当前项目 Tool，确保 topic inventory 自足且按名称唯一。 */
export function buildTopics(projectTools: ToolMetadata[] = []): DocTopic[] {
  const byTopic = new Map<string, ToolMetadata[]>();
  for (const tool of uniqueByName([...allRegisteredToolMetadata(), ...staticBridgeToolMetadata(), ...projectTools])) {
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

/** Topic 只列 Tool 名称；详细 descriptor 由 tools describe 单独按需读取。 */
function toolLine(tool: ToolMetadata): string {
  return tool.name;
}

function titleFromKey(key: string): string {
  return key.split(/[-_.]/).filter(Boolean).map((part) => `${part[0]?.toUpperCase() ?? ""}${part.slice(1)}`).join(" ") || key;
}
