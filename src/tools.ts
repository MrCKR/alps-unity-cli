/**
 * CLI sub-commands: tools group, tools group list, tools search, tools describe, tools run.
 *
 * Group-first discovery model:
 *   tools group               → list tool groups with counts
 *   tools group list <group>  → list tools in a group (short summary)
 *   tools describe <name>     → full metadata for a tool
 *   tools run <name>          → execute a tool
 *   tools search <keyword>    → auxiliary search across tools
 */

import { getBoolean, getString } from "./args.js";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { discoverProjectTools, discoverRuntimeTools, shouldDiscoverProjectTools, type ProjectToolRuntime } from "./project-tool-discovery.js";
import { bridgeCommandRegistry, executeBridgeCommand, executeBridgePayload, startBridgeCommandJob } from "./bridge/index.js";
import { checkToolExtensionAvailability } from "./extension-availability.js";
import {
  builtinToolMetadata,
  extensionToolMetadata,
  builtinToolGroups,
  findToolByName,
  searchTools,
  EXTENSION_GROUPS,
  type ToolMetadata
} from "./tools/metadata/index.js";
import { failure, success } from "./output.js";

import { confirmDestructiveExecution } from "./security.js";
import { inferToolRiskLevel, isLongRunningTool, supportStatusForTool, toolAvailabilityScope, toolCancellation, toolCliCommand, toolHandler, toolSource } from "./tools/metadata/index.js";
import type { CliResult } from "./types.js";
import { handleBuild } from "./cli-commands/build.js";
import { handleEditor } from "./cli-commands/editor.js";
import { handleTest } from "./cli-commands/test.js";
import { handlePackage } from "./cli-commands/package.js";
import { handleCapabilitiesCommand } from "./capabilities.js";
import { handleDocsCommand, handleReflectCommand } from "./docs-reflect.js";
import { handleCode } from "./runtime-code.js";
import type { Runtime } from "./cli-commands/types.js";

export type ToolsRuntime = ProjectToolRuntime;

export async function handleToolsCommand(
  command: string | undefined,
  positional: string[],
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  if (command === "run") {
    return handleToolsRun(positional, flags, runtime, requestId);
  }

  // `tools group` lists all groups; `tools group list <group>` lists tools in a group.
  if (command === "group") {
    const subCommand = positional[0];
    if (subCommand === "list") {
      return handleToolsGroupList(positional.slice(1), flags, runtime, requestId);
    }
    return handleToolsGroup(flags, runtime, requestId);
  }

  if (command === "search") {
    const keyword = positional[0] ?? getString(flags, "keyword") ?? getString(flags, "query");
    return handleToolsSearch(keyword, flags, runtime, requestId);
  }

  if (command === "describe") {
    const toolName = positional[0] ?? getString(flags, "name") ?? getString(flags, "tool");
    return handleToolsDescribe(toolName, flags, runtime, requestId);
  }

  return failure(requestId, "UNKNOWN_TOOLS_COMMAND", "Expected tools group, tools group list <group>, tools describe <tool>, tools run <tool>, or tools search <keyword>.", [
    "Run unity_cli tools group to see tool categories.",
    "Run unity_cli tools group list <group> to see tools in a group.",
    "Run unity_cli tools describe <tool> for full metadata.",
    "Run unity_cli tools run <tool> to execute a tool."
  ]);
}

/**
 * `tools group` — list all tool groups with short descriptions and tool counts.
 * This is the primary discovery entry point.
 */
async function handleToolsGroup(
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  const builtinGroups = builtinToolGroups();
  const includeCustom = shouldDiscoverProjectTools(flags, runtime);
  const customDiscovery = includeCustom ? await discoverProjectTools(flags, runtime, requestId) : { ok: true as const, tools: [] as ToolMetadata[], mode: "skipped" as const };
  if (customDiscovery.ok === false) return customDiscovery;

  // 扩展工具必须出现在标准 group-first 发现链；实际可用性由 requiresPackages/describe/run 校验。
  const allTools = uniqueToolsByName([...builtinToolMetadata(), ...extensionToolMetadata(), ...customDiscovery.tools]);
  const groupCounts: Record<string, number> = {};
  const groupDescriptions: Record<string, string> = {
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

  const seen = new Set<string>();
  for (const tool of allTools) {
    const group = tool.group;
    if (!seen.has(group)) {
      seen.add(group);
    }
    groupCounts[group] = (groupCounts[group] ?? 0) + 1;
  }

  const groups = [...seen].sort().map((group) => ({
    name: group,
    summary: groupDescriptions[group] ?? `${group} tools.`,
    toolCount: groupCounts[group] ?? 0,
    source: EXTENSION_GROUPS.has(group) ? "extension" : builtinGroups.includes(group) ? "built-in" : "project",
    cliCommand: `tools group list ${group}`
  }));

  const data: Record<string, unknown> = {
    command: "tools.group",
    count: groups.length,
    groups,
    nextActions: [
      "Run unity_cli tools group list <group> to see tools in a group.",
      "Run unity_cli tools describe <tool> for full metadata."
    ]
  };

  return success(requestId, data);
}

/**
 * `tools group list <group>` — list tools in a specific group with short summaries.
 * Only shows name, summary, destructive hint, source, availability.
 * Does NOT expand full schema.
 */
async function handleToolsGroupList(
  positional: string[],
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  const groupName = positional[0] ?? getString(flags, "group");
  if (!groupName) {
    return failure(requestId, "TOOLS_GROUP_LIST_NAME_REQUIRED", "Pass a group name: unity_cli tools group list <group>.", [
      "Run unity_cli tools group to see all tool groups."
    ]);
  }

  const builtinTools = builtinToolMetadata().filter((t) => t.group === groupName);
  const extensionTools = extensionToolMetadata().filter((t) => t.group === groupName);
  const includeCustom = shouldDiscoverProjectTools(flags, runtime);
  const customDiscovery = includeCustom ? await discoverProjectTools(flags, runtime, requestId) : { ok: true as const, tools: [] as ToolMetadata[], mode: "skipped" as const };
  if (customDiscovery.ok === false) return customDiscovery;
  const customTools = customDiscovery.tools.filter((t: ToolMetadata) => t.group === groupName);

  const allTools = uniqueToolsByName([...builtinTools, ...extensionTools, ...customTools]);
  if (allTools.length === 0) {
    return failure(requestId, "E_TOOL_GROUP_NOT_FOUND", `Tool group not found: ${groupName}.`, [
      "Run unity_cli tools group to see all tool groups."
    ]);
  }

  const summaries = allTools.map((t) => {
    const supportStatus = supportStatusForTool(t);
    return {
      name: t.name,
      summary: t.summary,
      group: t.group,
      source: toolSource(t, customTools.includes(t) ? "project" : "built-in"),
      destructiveHint: t.destructiveHint,
      playModeSupport: t.playModeSupport,
      supportStatus,
      target: t.target ?? "editor",
      requiresPackages: t.requiresPackages,
      cliCommand: toolCliCommand(t)
    };
  });

  const data: Record<string, unknown> = {
    command: "tools.group.list",
    group: groupName,
    count: summaries.length,
    tools: summaries,
    nextActions: [
      "Run unity_cli tools describe <tool> for full metadata including schema.",
      "Run unity_cli tools run <tool> to execute a tool."
    ]
  };

  return success(requestId, data);
}

async function handleToolsSearch(
  keyword: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  if (!keyword) {
    return failure(requestId, "TOOLS_SEARCH_KEYWORD_REQUIRED", "Pass a keyword: unity_cli tools search <keyword>.", [
      "Run unity_cli tools group to see tool groups."
    ]);
  }

  let results: ToolMetadata[] = searchTools(keyword);

  const includeCustom = getBoolean(flags, "include-custom") || shouldDiscoverProjectTools(flags, runtime);
  if (includeCustom) {
    const customTools = await discoverProjectTools(flags, runtime, requestId, { requireBridge: getBoolean(flags, "include-custom") });
    if (customTools.ok === false) return customTools;
    const customResults = customTools.tools.filter((t: ToolMetadata) =>
      t.name.toLowerCase().includes(keyword.toLowerCase()) ||
      t.summary.toLowerCase().includes(keyword.toLowerCase()) ||
      t.description.toLowerCase().includes(keyword.toLowerCase()) ||
      t.group.toLowerCase().includes(keyword.toLowerCase())
    );
    results = [...results, ...customResults];
  }

  results = uniqueToolsByName(results);

  const summaries = results.map((t) => {
    const supportStatus = supportStatusForTool(t);
    return {
      name: t.name,
      summary: t.summary,
      group: t.group,
      source: toolSource(t),
      destructiveHint: t.destructiveHint,
      playModeSupport: t.playModeSupport,
      supportStatus,
      cliCommand: toolCliCommand(t),
      target: t.target ?? "editor",
      availabilityScope: toolAvailabilityScope(t),
      requiresPackages: t.requiresPackages
    };
  });

  const format = getString(flags, "format");
  const data: Record<string, unknown> = {
    command: "tools.search",
    keyword,
    count: summaries.length,
    tools: summaries
  };

  if (format === "text") {
    const lines = summaries.map((s) => `${s.name} [${s.group}] ${s.supportStatus} - ${s.summary}`);
    data.text = lines.join("\n");
  }

  return success(requestId, data);
}

async function handleToolsDescribe(
  toolName: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  if (!toolName) {
    return failure(requestId, "TOOLS_DESCRIBE_NAME_REQUIRED", "Pass a tool name: unity_cli tools describe <tool>.", [
      "Run unity_cli tools group to see tool groups."
    ]);
  }

  const builtinTool = findToolByName(toolName);
  const tool = builtinTool ?? await findProjectToolByName(toolName, flags, runtime, requestId);
  if (!tool) {
    return failure(requestId, "E_TOOL_NOT_FOUND_FOR_TARGET", `Tool not found for target: ${toolName}.`, [
      "Run unity_cli tools group to see tool groups.",
      "Run unity_cli tools group list <group> to see tools in a group.",
      "Run unity_cli tools group to see all tool groups.",
      "Run unity_cli context read tools.custom to inspect Unity project custom tools and their groups.",
      "Run unity_cli tools group list <group> --include-custom for the custom tool's actual group.",
      "Run unity_cli tools search <keyword> --include-custom to find related custom tools."
    ]);
  }

  // ── Extension availability check ──
  // If the tool requires optional Unity packages, check whether they are
  // installed in the target project. If unavailable, return the descriptor
  // with an explicit unavailable status + nextActions rather than pretending
  // the tool is usable.
  const extensionCheck = await checkToolExtensionAvailability(tool, flags);

  const supportStatus = supportStatusForTool(tool);
  const data: Record<string, unknown> = publicToolDescriptor(tool, supportStatus, {
    command: "tools.describe"
  });

  if (!extensionCheck.available) {
    data.extensionAvailability = "unavailable";
    data.extensionId = extensionCheck.extensionId;
    data.missingPackageIds = extensionCheck.availabilityResult?.missingPackageIds ?? tool.requiresPackages ?? [];
    data.nextActions = extensionCheck.nextActions;
  } else if (extensionCheck.extensionId) {
    data.extensionAvailability = extensionCheck.availabilityResult?.status ?? "available";
    data.extensionId = extensionCheck.extensionId;
  }

  return success(requestId, data);
}

function uniqueToolsByName(tools: ToolMetadata[]): ToolMetadata[] {
  const seen = new Set<string>();
  const unique: ToolMetadata[] = [];
  for (const tool of tools) {
    if (seen.has(tool.name)) continue;
    seen.add(tool.name);
    unique.push(tool);
  }
  return unique;
}

function publicToolDescriptor(tool: ToolMetadata, supportStatus: ReturnType<typeof supportStatusForTool>, extra: Record<string, unknown> = {}): Record<string, unknown> {
  const {
    handler: _handler,
    timeoutMs: _timeoutMs,
    available: _available,
    ...publicTool
  } = tool;
  const longRunning = isLongRunningTool(tool);
  const cancellation = toolCancellation(tool);
  return {
    ...extra,
    ...publicTool,
    riskLevel: inferToolRiskLevel(tool),
    ...(tool.permissions?.length ? { permissions: tool.permissions } : {}),
    ...(longRunning ? { longRunning } : {}),
    ...(cancellation !== "none" ? { cancellation } : {}),
    cliCommand: toolCliCommand(tool),
    target: tool.target ?? "editor",
    availabilityScope: toolAvailabilityScope(tool),
    supportStatus
  };
}

async function handleToolsRun(
  positional: string[],
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  const toolName = positional[0] ?? getString(flags, "name") ?? getString(flags, "tool");
  if (!toolName) {
    return failure(requestId, "E_TOOL_NOT_FOUND_FOR_TARGET", "Tool name is required for tools run.", [
      "Run unity_cli tools group to see tool groups.",
      "Run unity_cli tools run <name> [--input <json>] [--instance <id|alias>]."
    ]);
  }

  // Discover the tool (builtin metadata + project tools)
  const builtinTool = findToolByName(toolName);
  const includeCustom = shouldDiscoverProjectTools(flags, runtime) || getBoolean(flags, "include-custom");
  let projectTool: ToolMetadata | undefined;
  if (!builtinTool && includeCustom) {
    // For runtime tools in Play Mode, use runtime-tool.list which is exempt
    // from the Play Mode busy guard. Try runtime-tool.list first, then fall
    // back to project-tool.list for editor-domain tools.
    const runtimeDiscovery = await discoverRuntimeTools(flags, runtime, requestId);
    if (runtimeDiscovery.ok === false) return runtimeDiscovery;
    projectTool = runtimeDiscovery.tools.find((t: ToolMetadata) => t.name === toolName);

    if (!projectTool) {
      // Tool not found in runtime tools; try editor-domain project-tool.list
      const customTools = await discoverProjectTools(flags, runtime, requestId, { requireBridge: getBoolean(flags, "include-custom") });
      if (customTools.ok === false) return customTools;
      projectTool = customTools.tools.find((t: ToolMetadata) => t.name === toolName);
    }
  }

  const tool = builtinTool ?? projectTool;
  if (!tool) {
    return failure(requestId, "E_TOOL_NOT_FOUND_FOR_TARGET", `Tool not found: ${toolName}.`, [
      "Run unity_cli tools group to see tool groups.",
      "Run unity_cli tools search <keyword> to find related tools."
    ]);
  }

  // ── Extension availability guard ──
  // If the tool requires optional Unity packages that are not installed,
  // block execution and return E_EXTENSION_UNAVAILABLE with nextActions.
  const extensionCheck = await checkToolExtensionAvailability(tool, flags);
  if (!extensionCheck.available) {
    return failure(
      requestId,
      extensionCheck.errorCode,
      extensionCheck.message,
      extensionCheck.nextActions,
      {
        extensionId: extensionCheck.extensionId,
        missingPackageIds: extensionCheck.availabilityResult?.missingPackageIds ?? tool.requiresPackages ?? []
      }
    );
  }

  // Determine the execution target and route accordingly
  const target = tool.target ?? "editor";
  const isRuntime = target === "runtime" || (target === "both" && getBoolean(flags, "runtime"));

  if (isRuntime) {
    // runtime-tool.run 是内部包装命令；确认策略必须按实际 runtime tool descriptor 判定。
    if (tool.destructiveHint) {
      const confirmation = await confirmDestructiveExecution(flags, {
        command: toolName,
        summary: tool.summary,
        destructiveHint: true,
        supportsDryRun: tool.supportsDryRun,
        requestId
      });
      if (confirmation) return confirmation;
    }
    return executeBridgeCommand(requestId, "runtime-tool.run", flags, toolName, runtime);
  }

  // code.exec 是 hybrid，但需要先经过原生 handler 完成本地校验、dry-run、artifact 与 batchmode job 逻辑。
  if (builtinTool && toolName === "code.exec") {
    // code.exec 既支持 tools 通用 --stdin，也保留历史的“代码源来自 stdin”语义；
    // 当没有 --input/--input-file 时，--stdin 交给 handleCode 作为代码源处理。
    const usesCodeSourceStdin = getBoolean(flags, "stdin") && getString(flags, "input") === undefined && getString(flags, "input-file") === undefined;
    const parsedInput = usesCodeSourceStdin ? { ok: true, input: {} } as ParsedToolInput : await parseToolInput(flags, requestId);
    if (!parsedInput.ok) return parsedInput.error!;
    return handleCode("exec", mergeInputToFlags(parsedInput.input, flags), runtime as Runtime, requestId);
  }

  // ── Built-in bridge/hybrid tools WITH bridge spec: route through Unity Bridge ──
  // 仅当存在 bridge spec 时走 bridge 路由(scene/camera/go/.../code.exec 等)。
  // 无 bridge spec 的 hybrid/offline 工具(package.*/docs.*/reflect.*/...)
  // 走下面的原生 handler dispatch。
  const handler = toolHandler(tool);

  if (builtinTool && (handler === "bridge" || handler === "hybrid") && findBridgeSpecForTool(toolName)) {
    const parsedInput = await parseToolInput(flags, requestId);
    if (!parsedInput.ok) return parsedInput.error!;
    const mergedFlags = mergeInputToFlags(parsedInput.input, flags);
    const spec = findBridgeSpecForTool(toolName)!;
    let args = await spec.buildArgs({ command: toolName, flags: mergedFlags, positionalTarget: positional[1] });
    const validationError = validateRequiredInputFields(tool, args as Record<string, unknown>, mergedFlags, positional[1], requestId);
    if (validationError) return validationError;
    if (getBoolean(mergedFlags, "job") && spec.supportsJob !== true) {
      return unsupportedBridgeJob(requestId, toolName);
    }
    // 破坏性工具确认(对齐旧 cli.ts bridge 路径)。
    if (spec.destructiveHint || tool.destructiveHint) {
      const confirmation = await confirmDestructiveExecution(mergedFlags, {
        command: toolName,
        summary: tool.summary,
        destructiveHint: true,
        supportsDryRun: spec.supportsDryRun ?? tool.supportsDryRun,
        requestId
      });
      if (confirmation) return confirmation;
    }
    const bridgeCommand = spec.bridgeCommand;
    if (spec.toolName) {
      args = { name: spec.toolName, input: args };
    }
    if (getBoolean(mergedFlags, "job")) {
      const jobRuntime = bridgeJobRuntime(runtime);
      if (!jobRuntime) {
        return failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `Tool "${toolName}" requires a job runner runtime for --job.`, [
          "Run from the unity_cli entrypoint, or provide runtime.dataDir, runtime.store and runtime.runner in tests."
        ]);
      }
      return startBridgeCommandJob(requestId, toolName, bridgeCommand, args as Record<string, unknown>, mergedFlags, jobRuntime, spec);
    }
    return executeBridgePayload(requestId, bridgeCommand, args, mergedFlags, runtime);
  }

  // ── Built-in job-handler tools: route to native job CLI handlers ──
  if (builtinTool && handler === "job") {
    return dispatchJobHandler(tool, flags, runtime, requestId);
  }

  // ── Built-in offline/hybrid(no bridge spec) tools: route to native CLI handlers ──
  // 迁移自旧顶层命令的 docs/reflect/package/diagnose/tools 发现。
  if (builtinTool && (handler === "offline" || handler === "hybrid")) {
    return dispatchOfflineHandler(tool, flags, runtime, requestId, positional);
  }

  // ── Built-in tools with other unsupported handlers ──
  if (builtinTool) {
    return failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `Tool "${toolName}" uses handler "${handler}" which is not supported by tools run.`, [
      "Run unity_cli tools describe <tool> for details."
    ]);
  }

  // ── Custom project tools (editor domain): route through project-tool.run ──
  return executeBridgeCommand(requestId, "project-tool.run", flags, toolName, runtime);
}

/** 返回 bridge/hybrid 工具未声明 job 支持时的结构化错误。 */
function unsupportedBridgeJob(requestId: string, toolName: string): CliResult {
  return failure(requestId, "E_TOOL_JOB_UNSUPPORTED", `Tool does not support --job: ${toolName}.`, [
    `Run unity_cli tools describe ${toolName} and check supportStatus/cancellation.`,
    "Remove --job to run through the live Bridge, or choose a tool whose descriptor documents bridge-job support."
  ], {
    tool: toolName,
    flag: "--job"
  });
}

/** 提取 batchmode job 需要的运行时依赖，测试 runtime 缺项时显式失败。 */
function bridgeJobRuntime(runtime: ToolsRuntime): (Runtime & { dataDir: string }) | undefined {
  const candidate = runtime as Partial<Runtime>;
  return candidate.dataDir && candidate.store && candidate.runner ? candidate as Runtime & { dataDir: string } : undefined;
}

export function findBridgeSpecForTool(toolName: string) {
  // tools run 只能按完整 dotted tool name 精确解析 bridge spec。
  // 不能回退到 group/command lookup,否则 package.install 会误匹配 package.embed 这类同组 bridge command。
  return bridgeCommandRegistry.specs().find((spec) => spec.toolName === toolName || spec.bridgeCommand === toolName);
}

interface ParsedToolInput {
  ok: boolean;
  input: Record<string, unknown>;
  error?: CliResult;
}

async function parseToolInput(flags: Record<string, string | boolean>, requestId: string): Promise<ParsedToolInput> {
  const inputFilePath = getString(flags, "input-file");
  if (inputFilePath) {
    const content = await readFile(resolve(inputFilePath), "utf8");
    return parseToolInputString(content, requestId, "--input-file");
  }
  // S 节：通用 --stdin 从标准输入读 JSON，对齐 IvanMurzak run-tool --stdin。
  if (getBoolean(flags, "stdin")) {
    const stdinContent = await readStdin();
    if (stdinContent.length > 0) {
      return parseToolInputString(stdinContent, requestId, "--stdin");
    }
  }
  const inputString = getString(flags, "input");
  if (inputString !== undefined) {
    return parseToolInputString(inputString, requestId, "--input");
  }
  return { ok: true, input: {} };
}

/** 从 process.stdin 读取全部内容（管道/重定向场景）。 */
async function readStdin(): Promise<string> {
  return new Promise((resolve) => {
    let settled = false;
    let data = "";
    const done = (value: string) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(value);
    };
    const timer = setTimeout(() => done(data), 50);
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (chunk) => { data += chunk; });
    process.stdin.on("end", () => done(data));
    process.stdin.on("error", () => done(""));
    // 如果 stdin 不是管道（TTY），立即返回空
    if (process.stdin.isTTY) done("");
  });
}

/** S 节：按 metadata inputSchema.required 做轻量必填字段校验，在执行前拦截参数缺失。 */
function validateRequiredInputFields(
  tool: ToolMetadata,
  input: Record<string, unknown>,
  flags: Record<string, string | boolean>,
  positionalTarget: string | undefined,
  requestId: string
): CliResult | undefined {
  const required = tool.inputSchema?.required;
  if (!Array.isArray(required) || required.length === 0) return undefined;
  // 同时检查 --input JSON 与显式 CLI flags；显式 flags 仍是合法调用方式。
  const missing: string[] = [];
  for (const field of required) {
    const kebab = camelToKebab(field);
    const snake = field.replace(/-/g, "_");
    const positionalSatisfiesField = positionalTarget !== undefined && (required.length === 1 || ["target", "name", "keyword", "group", "query", "searchTerm", "path", "assetPath", "scenePath", "packageId", "id", "action"].includes(field));
    const fieldPresent = input[field] !== undefined || input[kebab] !== undefined || input[snake] !== undefined || flags[field] !== undefined || flags[kebab] !== undefined || flags[snake] !== undefined;
    const assetSourceAliasPresent = (tool.name === "asset.duplicate" || tool.name === "asset.move") && field === "source" && (input.path !== undefined || flags.path !== undefined || positionalTarget !== undefined);
    const assetPathAliasPresent = (tool.name === "asset.get-data" || tool.name === "asset.get-components" || tool.name === "asset.preview") && field === "assetPath" && (input.path !== undefined || flags.path !== undefined || positionalTarget !== undefined);
    const cameraOutputAliasPresent = tool.name === "camera.screenshot" && field === "path" && (input.output !== undefined || flags.output !== undefined || positionalTarget !== undefined);
    const physicsMaterialPathAliasPresent = tool.name === "physics.assign-material" && field === "materialPath" && (input.path !== undefined || flags.path !== undefined); // 允许 PhysicMaterial 路径沿用 path 参数。
    const materialColorAliasPresent = tool.name === "material.set-renderer-color" && field === "color" && (input.value !== undefined || flags.value !== undefined);
    const textureColorAliasPresent = tool.name === "texture.modify" && field === "color" && (input.fillColor !== undefined || flags["fill-color"] !== undefined || flags.fillColor !== undefined);
    const uiUxmlAliasPresent = tool.name.startsWith("ui.document.") && field === "uxml" && (input.path !== undefined || flags.path !== undefined || positionalTarget !== undefined);
    const uiPathAliasPresent = tool.name === "ui.document.delete" && field === "path" && (input.uxml !== undefined || flags.uxml !== undefined || positionalTarget !== undefined);
    const uiContentAliasPresent = tool.name === "ui.document.update" && field === "content" && (flags["content-file"] !== undefined || flags.contentFile !== undefined);
    const uiStylesheetAliasPresent = tool.name === "ui.link-stylesheet" && field === "uss" && (input.styleSheet !== undefined || flags["style-sheet"] !== undefined || flags.styleSheet !== undefined);
    const uiRenderTargetAliasPresent = tool.name === "ui.render-preview" && field === "target" && (input.uxml !== undefined || input.path !== undefined || flags.uxml !== undefined || flags.path !== undefined || positionalTarget !== undefined);
    if (!positionalSatisfiesField && !fieldPresent && !assetSourceAliasPresent && !assetPathAliasPresent && !cameraOutputAliasPresent && !physicsMaterialPathAliasPresent && !materialColorAliasPresent && !textureColorAliasPresent && !uiUxmlAliasPresent && !uiPathAliasPresent && !uiContentAliasPresent && !uiStylesheetAliasPresent && !uiRenderTargetAliasPresent) {
      missing.push(field);
    }
  }
  if (missing.length === 0) return undefined;
  return failure(requestId, "E_SCHEMA_INVALID", `Missing required field(s): ${missing.join(", ")}`, [
    `Run unity_cli tools describe ${tool.name} to see the required input fields.`,
    `Required: ${required.join(", ")}.`
  ]);
}

/** 解析 tools run 输入；兼容 PowerShell 将 escaped JSON 拆坏后的常见形态。 */
function parseToolInputString(inputString: string, requestId: string, source: "--input" | "--input-file" | "--stdin"): ParsedToolInput {
  const parsed = parseJsonWithPowerShellFallback(inputString);
  if (parsed.ok) {
    const value = parsed.value;
    return { ok: true, input: typeof value === "object" && value !== null && !Array.isArray(value) ? value as Record<string, unknown> : { input: value } };
  }
  return {
    ok: false,
    input: {},
    error: failure(requestId, "E_TOOL_INPUT_JSON_INVALID", `${source} must be valid JSON. ${parsed.message}`, [
      "In PowerShell, use single quotes with unescaped JSON quotes, or prefer --input-file <path> for complex JSON.",
      "Do not type backslashes before JSON quotes inside PowerShell single quotes.",
      "Run unity_cli tools describe <tool> and copy the inputSchema field names exactly."
    ])
  };
}

function parseJsonWithPowerShellFallback(inputString: string): { ok: true; value: unknown } | { ok: false; message: string } {
  try {
    return { ok: true, value: JSON.parse(inputString) };
  } catch (error) {
    const backslashQuoted = recoverBackslashQuotedJson(inputString);
    if (backslashQuoted !== undefined) {
      return { ok: true, value: backslashQuoted };
    }
    const recovered = recoverPowerShellEscapedJsonObject(inputString);
    if (recovered) {
      return { ok: true, value: recovered };
    }
    return { ok: false, message: error instanceof Error ? error.message : String(error) };
  }
}

function recoverBackslashQuotedJson(inputString: string): unknown | undefined {
  const trimmed = inputString.trim();
  if (!/^[{\[]\\"/.test(trimmed)) {
    return undefined;
  }
  try {
    return JSON.parse(trimmed.replace(/\\"/g, '"'));
  } catch {
    return undefined;
  }
}

function recoverPowerShellEscapedJsonObject(inputString: string): Record<string, unknown> | undefined {
  const trimmed = inputString.trim();
  if (!trimmed.startsWith("{\\") || (!trimmed.endsWith("}") && !trimmed.endsWith("\\}"))) {
    return undefined;
  }
  const body = trimmed
    .replace(/^\{\\\s*/, "")
    .replace(/\\?\}\s*$/, "");
  return parseRecoveredObjectBody(body);
}

/** 解析 PowerShell 常见的 `{"key":"value"}` 被拆坏后的对象体，保留数组/对象层级。 */
function parseRecoveredObjectBody(body: string): Record<string, unknown> | undefined {
  const result: Record<string, unknown> = {};
  for (const pair of splitRecoveredTopLevel(body)) {
    const separator = findRecoveredKeySeparator(pair);
    if (separator < 0) {
      return undefined;
    }
    const key = cleanRecoveredToken(pair.slice(0, separator));
    if (!key) {
      return undefined;
    }
    result[key] = parseRecoveredValue(pair.slice(separator + 1).replace(/\\$/g, "").trim());
  }
  return result;
}

function parseRecoveredValue(value: string): unknown {
  const trimmed = value.trim();
  if (trimmed === "true") return true;
  if (trimmed === "false") return false;
  if (trimmed === "null") return null;
  if (/^-?\d+(?:\.\d+)?$/.test(trimmed)) return Number(trimmed);
  if (trimmed.startsWith("[") && trimmed.endsWith("]")) {
    const inner = trimmed.slice(1, -1).trim();
    if (!inner) return [];
    return splitRecoveredTopLevel(inner).map(parseRecoveredValue);
  }
  if (trimmed.startsWith("{") && trimmed.endsWith("}")) {
    return parseRecoveredObjectBody(trimmed.slice(1, -1));
  }
  return cleanRecoveredToken(trimmed);
}

function splitRecoveredTopLevel(value: string): string[] {
  const parts: string[] = [];
  let start = 0;
  let depth = 0;
  for (let index = 0; index < value.length; index += 1) {
    const char = value[index];
    if (char === "{" || char === "[") depth += 1;
    else if (char === "}" || char === "]") depth = Math.max(0, depth - 1);
    else if (char === "," && depth === 0) {
      parts.push(value.slice(start, value[index - 1] === "\\" ? index - 1 : index).trim());
      start = index + 1;
    }
  }
  parts.push(value.slice(start).trim());
  return parts.filter(Boolean);
}

function findRecoveredKeySeparator(pair: string): number {
  return pair.indexOf(":");
}

function cleanRecoveredToken(value: string): string {
  return value.trim().replace(/^\\/, "").replace(/\\$/, "").replace(/\\:/g, ":").replace(/\\,/g, ",").replace(/\\\\/g, "\\");
}
function camelToKebab(key: string): string {
  return key.replace(/[A-Z]/g, (m) => `-${m.toLowerCase()}`);
}

/**
 * Merge --input JSON fields into CLI flags (kebab-case).
 * Boolean values are preserved as explicit true/false flags; string/number values are passed as-is.
 * Existing flags are not overwritten.
 */
function mergeInputToFlags(
  input: Record<string, unknown>,
  flags: Record<string, string | boolean>
): Record<string, string | boolean> {
  const merged = { ...flags };
  const structuredInput: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(input)) {
    const flagKey = camelToKebab(key);
    // 保留原始 input 字段，避免 tools run --target 实例选择与工具 target 字段冲突。
    structuredInput[key] = value;
    if (flagKey in merged) continue; // explicit CLI flags take precedence
    if (typeof value === "boolean") {
      merged[flagKey] = value;
    } else if (typeof value === "number") {
      merged[flagKey] = String(value);
    } else if (typeof value === "string") {
      merged[flagKey] = value;
    }
  }
  if (Object.keys(structuredInput).length > 0) {
    merged.__structuredInput = JSON.stringify(structuredInput);
  }
  return merged;
}

/**
 * Dispatch a built-in job-handler tool to its native CLI command handler.
 *
 * Routes based on `tool.cliCommand`:
 *   "test run"    → handleTest("run", ...)
 *   "build run"   → handleBuild("run", ...)
 *   "editor refresh" → handleEditor("refresh", ...)
 *
 * Merges --input JSON into CLI flags so that `--input '{"unity":"...","project":"..."}'`
 * is equivalent to passing `--unity ... --project ...` on the native command.
 */
async function dispatchJobHandler(
  tool: ToolMetadata,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  // 按 tool.name 路由(README Project Baseline: cliCommand 现为 "tools run <name>")。
  const dotIndex = tool.name.indexOf(".");
  const group = dotIndex > 0 ? tool.name.substring(0, dotIndex) : tool.name;
  const sub = dotIndex > 0 ? tool.name.substring(dotIndex + 1) : "";

  const fullRuntime = runtime as Runtime;
  if (!fullRuntime.store || !fullRuntime.runner || !fullRuntime.dataDir) {
    return failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `Tool "${tool.name}" requires a job runner runtime.`, [
      "Run unity_cli tools describe <tool> for details."
    ]);
  }

  const parsedInput = await parseToolInput(flags, requestId);
  if (!parsedInput.ok) return parsedInput.error!;
  const validationError = validateRequiredInputFields(tool, parsedInput.input, flags, undefined, requestId);
  if (validationError) return validationError;
  const mergedFlags = mergeInputToFlags(parsedInput.input, flags);

  let result: CliResult | undefined;

  switch (group) {
    case "test":
      result = await handleTest(sub, mergedFlags, fullRuntime, requestId);
      break;
    case "build":
      result = await handleBuild(sub, mergedFlags, fullRuntime, requestId);
      break;
    case "editor":
      result = await handleEditor(sub, mergedFlags, fullRuntime, requestId);
      break;
    default:
      return failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `Tool "${tool.name}" has unmapped job handler.`, [
        "Run unity_cli tools describe <tool> for details."
      ]);
  }

  return result ?? failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `Job tool "${tool.name}" is not supported by tools run.`, [
    "Run unity_cli tools describe <tool> for details."
  ]);
}

/**
 * 分发 offline/hybrid(no bridge spec) 工具到原生 CLI handler。
 * 仅处理仍属于工具目录的离线/混合能力;target/instances/jobs/batch/doctor 属于顶层控制面。
 */
async function dispatchOfflineHandler(
  tool: ToolMetadata,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string,
  positional: string[] = []
): Promise<CliResult> {
  const name = tool.name;
  const dotIndex = name.indexOf(".");
  const group = dotIndex > 0 ? name.substring(0, dotIndex) : name;
  const sub = dotIndex > 0 ? name.substring(dotIndex + 1) : "";
  const fullRuntime = runtime as Runtime;

  const parsedInput = await parseToolInput(flags, requestId);
  if (!parsedInput.ok) return parsedInput.error!;
  const validationError = validateRequiredInputFields(tool, parsedInput.input, flags, positional[1], requestId);
  if (validationError) return validationError;
  const mergedFlags = mergeInputToFlags(parsedInput.input, flags);

  // 破坏性 offline 工具确认(package.remove 等)。
  if (tool.destructiveHint) {
    const confirmation = await confirmDestructiveExecution(mergedFlags, {
      command: name,
      summary: tool.summary,
      destructiveHint: true,
      supportsDryRun: tool.supportsDryRun,
      requestId
    });
    if (confirmation) return confirmation;
  }

  // tools 发现工具(tools.group/group-list/search/describe/run)
  if (group === "tools") {
    return handleToolsCommand(sub === "group-list" ? "group" : sub, [], mergedFlags, runtime, requestId);
  }

  switch (group) {
    case "docs":
      return handleDocsCommand(sub === "list" ? undefined : sub, positional.slice(1), mergedFlags, fullRuntime.dataDir, requestId, fullRuntime);
    case "reflect":
      return handleReflectCommand(sub, positional.slice(1), mergedFlags, fullRuntime.dataDir, requestId);
    case "package":
      return (await handlePackage(sub, positional.slice(1), mergedFlags, fullRuntime, requestId)) ?? failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `package.${sub} not supported.`, ["Run unity_cli tools describe <tool>."]);
    case "build":
      return (await handleBuild(sub, mergedFlags, fullRuntime, requestId)) ?? failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `build.${sub} not supported.`, ["Run unity_cli tools describe <tool>."]);
    case "test":
      return (await handleTest(sub, mergedFlags, fullRuntime, requestId)) ?? failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `test.${sub} not supported.`, ["Run unity_cli tools describe <tool>."]);
    case "editor":
      return (await handleEditor(sub, mergedFlags, fullRuntime, requestId)) ?? failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `editor.${sub} not supported.`, ["Run unity_cli tools describe <tool>."]);
    case "diagnose":
      if (sub === "capabilities") return handleCapabilitiesCommand(mergedFlags, fullRuntime, requestId);
      return failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `diagnose.${sub} not supported.`, ["Run unity_cli tools describe <tool>."]);
    default:
      return failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `Tool "${name}" has unmapped offline handler.`, [
        "Run unity_cli tools describe <tool> for details."
      ]);
  }
}


async function findProjectToolByName(
  name: string,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<ToolMetadata | undefined> {
  const customTools = await discoverProjectTools(flags, runtime, requestId);
  if (customTools.ok === false) {
    return undefined;
  }
  return customTools.tools.find((tool: ToolMetadata) => tool.name === name);
}

/**
 * Resolve which extension tools are available for the target project.
 *
 * Extension tools are shown only when their required Unity packages are installed
 * in the target project. With no explicit --project, a selected live instance can
 * provide the project path for discovery commands that use --instance/--target.
 */

