import { discoverProjectTools, shouldDiscoverProjectTools } from "../../project-tool-discovery.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";
import {
  EXTENSION_GROUPS,
  builtinToolGroups,
  builtinToolMetadata,
  extensionToolMetadata,
  toolSource,
  type ToolMetadata
} from "../metadata/index.js";

export interface ToolsGroupRequest {
  includeCustom?: boolean;
  bridgeEndpoint?: string;
  target?: string;
  instance?: string;
  runtime?: boolean;
}

export interface ToolsGroupItem {
  name: string;
  summary: string;
  toolCount: number;
  source: string;
  cliCommand: string;
}

export interface ToolsGroupResult {
  command: string;
  count: number;
  groups: ToolsGroupItem[];
  nextActions: string[];
}

type DiscoveryFlags = Record<string, string | boolean>;

const GROUP_DESCRIPTIONS: Record<string, string> = {
  scene: "Scene operations: hierarchy, active/open scenes, build settings, create/load/save/unload, validate/validation, object moves, and view framing.",
  camera: "Camera operations: list/info, create, aim, lens settings, screenshots, and multiview capture.",
  go: "GameObject operations: find/inspect, create/modify/duplicate/delete, parenting, relative moves, and look-at transforms.",
  component: "Component operations: add, remove, get, modify, set properties, and list component types.",
  asset: "Asset operations: search/info, create/import/modify/delete, duplicate/move/rename, refresh, preview, deep data, and component reads.",
  prefab: "Prefab operations: info, hierarchy, create/modify, instantiate, unpack, and Prefab Stage open/save/close.",
  material: "Material assets and renderer assignment: info, create, assign, shader properties, and renderer color.",
  graphics: "Graphics and rendering: quality, pipeline, lighting bake, probes, skybox, screenshots, stats, and diagnostics.",
  physics: "Physics operations: settings, materials, Rigidbody/Collider, joints, forces, ray/shape queries, simulation, and validation.",
  profiler: "Unity Profiler: status, recording, counters, frame timing, memory snapshots, and Frame Debugger.",
  animator: "Animator runtime controls: inspect state, read/set parameters, crossfade, speed, and enabled state.",
  controller: "AnimatorController asset editing: create controllers/states/layers/blend trees, add/read transitions, parameters; existing transition edits need generic object paths.",
  probuilder: "ProBuilder 3D modeling: shape creation, mesh editing, face/edge/vertex operations, smoothing, pivots, and repair.",
  cinemachine: "Cinemachine cameras: virtual camera creation, targets, lens, priority, blends, body/aim/noise, brain, and extensions.",
  urp: "URP rendering: renderer features, volume components, and Universal Render Pipeline asset settings.",
  hdrp: "HDRP rendering: volume components and High Definition Render Pipeline asset settings.",
  shader: "Shader assets: list/info, source read, structured data, create, update, and delete.",
  texture: "Texture assets: info, create, modify, delete, sprite setup, import settings, and generate/generation of procedural images.",
  animation: "Animation clips and simple playback: inspect/create/modify clips, query Animator state, play, and stop.",
  vfx: "Visual effects: VFX Graph assets/instances, exposed parameters, events, ParticleSystem modules, LineRenderer, and TrailRenderer.",
  ui: "UI Toolkit and uGUI: create controls, manage UIDocuments, inspect/modify visual trees, stylesheets, panel settings, and previews.",
  script: "Script management: create, read, edit, validate.",
  editor: "Editor session control: state, PlayMode, undo/redo, compile/refresh, menu, selection, resolution, dialogs, and active tools.",
  console: "Unity Editor console: read and clear logs.",
  package: "Unity package operations: list, inspect, install, remove, resolve, registries, and embed packages.",
  project: "Project settings operations: tags and layers.",
  test: "Unity test execution through CLI jobs.",
  build: "Build discovery and job control: scenes, platform, profiles, settings, run, status, and cancel.",
  docs: "Built-in CLI docs and offline Unity API documentation lookup/search.",
  reflect: "Offline reflection cache: search C# and Unity types/members before writing code or live reflection calls.",
  tools: "Tool discovery and execution meta-tools: groups, group lists, search, describe, and run.",
  diagnose: "Diagnostics: preflight checks and capability reporting for CLI, target, Bridge, and environment readiness.",
  screenshot: "Screenshot capture: active source, Game View, Scene View, isolated objects, orbit, and surround contact sheets.",
  code: "C# code execution in Unity Editor or batchmode; powerful fallback for one-off operations.",
  object: "Generic Unity Object fallback: deep read/modify serialized member trees with paths, pathPatches, and jsonPatch.",
  reflection: "Live Unity reflection: find and call C# methods in the open Editor.",
  type: "Live C# / Unity type schema generation for planning object/component modifications.",
  "scriptable-object": "ScriptableObject operations: create assets, read info, and modify serialized fields.",
  "cli-only": "CLI-only commands: init, package, batch, docs, reflect, test, build, and job management."
};

// 构造 project custom tool discovery 需要的公开 CLI flags 形态。
function buildDiscoveryFlags(request: ToolsGroupRequest): DiscoveryFlags {
  const flags: DiscoveryFlags = {};
  if (request.includeCustom !== undefined) flags["include-custom"] = request.includeCustom;
  if (request.bridgeEndpoint !== undefined) flags["bridge-endpoint"] = request.bridgeEndpoint;
  if (request.target !== undefined) flags.target = request.target;
  if (request.instance !== undefined) flags.instance = request.instance;
  if (request.runtime !== undefined) flags.runtime = request.runtime;
  return flags;
}

// 按工具名去重，custom tool 排在后面时覆盖同名静态工具。
function uniqueToolsByName(tools: ToolMetadata[]): ToolMetadata[] {
  const map = new Map<string, ToolMetadata>();
  for (const tool of tools) {
    map.set(tool.name, tool);
  }
  return Array.from(map.values());
}

// 将 project custom discovery 失败转成 defineTool 可识别的结构化错误。
function throwDiscoveryError(result: { error?: { code: string; message: string; nextActions?: string[]; transient?: boolean; retryAfterMs?: number } }): never {
  const error = result.error ?? {
    code: "PROJECT_TOOL_DISCOVERY_FAILED",
    message: "Project custom tool discovery failed.",
    nextActions: ["Retry without includeCustom or check the Unity bridge target."]
  };
  throw new StructuredToolError(
    error.code,
    error.message,
    error.nextActions ?? [],
    { transient: error.transient, retryAfterMs: error.retryAfterMs }
  );
}

// 汇总 group 计数和来源，保持旧 tools.group 输出形态。
function summarizeGroups(tools: ToolMetadata[], builtinGroups: string[]): ToolsGroupItem[] {
  const seen = new Set<string>();
  const groupCounts: Record<string, number> = {};
  for (const tool of tools) {
    if (!seen.has(tool.group)) {
      seen.add(tool.group);
    }
    groupCounts[tool.group] = (groupCounts[tool.group] ?? 0) + 1;
  }
  return [...seen].sort().map((group) => {
    const groupTools = tools.filter((tool) => tool.group === group);
    const explicitSource = groupTools.find((tool) => tool.source === "built-in")?.source
      ?? groupTools.find((tool) => tool.source === "extension")?.source
      ?? groupTools.find((tool) => tool.source === "project")?.source;
    const source = explicitSource ?? (EXTENSION_GROUPS.has(group) ? "extension" : builtinGroups.includes(group) ? "built-in" : "project");
    return {
      name: group,
      summary: GROUP_DESCRIPTIONS[group] ?? `${group} tools.`,
      toolCount: groupCounts[group] ?? 0,
      source,
      cliCommand: `tools group list ${group}`
    };
  });
}

export default defineTool<ToolsGroupRequest, ToolsGroupResult>({
  name: "tools.group",
  summary: "List the available tool groups with counts.",
  description: "List the available tool groups with counts. Primary discovery entry point in the group-first model; run this first to find the group containing a capability, then tools group list <group> and tools describe <tool>. Pass includeCustom with a target, instance, or bridgeEndpoint to include project custom tool groups.",
  group: "tools",
  handler: "offline",
  supportStatus: "cli-only",
  destructiveHint: false,
  sideEffects: [],
  playModeSupport: "supported",
  examples: [
    { description: "List all tool groups", argv: ["tools", "group"] },
    { description: "List tool groups including project custom tools", argv: ["tools", "run", "tools.group", "--input", "{\"includeCustom\":true,\"bridgeEndpoint\":\"http://127.0.0.1:47395\"}"] }
  ],
  async execute(request, context) {
    const flags = buildDiscoveryFlags(request);
    const builtinGroups = builtinToolGroups();
    let customTools: ToolMetadata[] = [];

    if (shouldDiscoverProjectTools(flags, context)) {
      const discovery = await discoverProjectTools(flags, {
        projectTools: context.projectTools,
        bridgeExecute: context.bridgeExecute,
        bridgeEndpoint: request.bridgeEndpoint ?? context.bridgeEndpoint
      }, context.requestId, { requireBridge: request.includeCustom === true });
      if (discovery.ok === false) {
        throwDiscoveryError(discovery);
      }
      customTools = discovery.tools;
    }

    const allTools = uniqueToolsByName([...builtinToolMetadata(), ...extensionToolMetadata(), ...customTools]);
    const groups = summarizeGroups(allTools, builtinGroups);
    return {
      command: "tools.group",
      count: groups.length,
      groups,
      nextActions: [
        "Run unity_cli tools group list <group> to see tools in a group.",
        "Run unity_cli tools describe <tool> for full metadata."
      ]
    };
  }
});
