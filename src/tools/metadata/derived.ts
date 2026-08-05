import { GENERATED_TOOL_COMPLETION_CONTRACTS } from "../.generated-completion-registry.js";
import type { ToolMetadata, ToolRiskLevel, ToolSource } from "./types.js";

const TOOL_MUTATION_CONTRACTS = new Map(GENERATED_TOOL_COMPLETION_CONTRACTS.map((contract) => [contract.toolName, contract.mutation]));

const TOOL_PUBLIC_ROUTE_OVERRIDES = new Map<string, string>([
  ["tools.group", "tools group"],
  ["tools.group-list", "tools group list"],
  ["tools.search", "tools search"],
  ["tools.describe", "tools describe"]
]);

export function toolPublicRoute(tool: { name: string }): string {
  return TOOL_PUBLIC_ROUTE_OVERRIDES.get(tool.name) ?? `tools run ${tool.name}`;
}

const DOCS_TOPIC_OVERRIDES = new Map<string, string>([
  ["type.schema", "reflection"]
]);

const LONG_RUNNING_TOOL_NAMES = new Set<string>([
  "asset.refresh",
  "build.run",
  "cinemachine.create",
  "code.exec",
  "editor.compile",
  "editor.refresh",
  "graphics.bake-start",
  "package.resolve",
  "probuilder.create-poly-shape",
  "probuilder.create-shape",
  "profiler.capture",
  "profiler.export",
  "profiler.memory-snapshot.take",
  "test.run"
]);

const CRITICAL_RISK_EFFECTS = new Set([
  "arbitrary-method-invocation",
  "user-code-execution"
]);

const HIGH_RISK_EFFECTS = new Set([
  "asset-delete",
  "asset-delete-nested",
  "lightmapping-data-clear",
  "manifest-change",
  "object-modify",
  "project-file-delete",
  "project-file-write"
]);

const MEDIUM_RISK_EFFECTS = new Set([
  "console-clear",
  "editor-menu",
  "lightmapping-bake",
  "prefab-unlink",
  "reflection-probe-bake",
  "session-affecting",
  "shader-recompile",
  "unknown-editor-side-effect"
]);

const HIGH_RISK_TOOL_PREFIXES = ["object.", "scriptable-object."];
const HIGH_RISK_TOOL_NAMES = new Set(["component.modify"]);
const MEDIUM_RISK_TOOL_NAMES = new Set([
  "animation.clip.create",
  "animation.clip.modify",
  "console.clear",
  "graphics.bake-reflection-probe",
  "graphics.bake-start",
  "material.assign",
  "package.resolve",
  "prefab.stage-open",
  "scene.load",
  "scene.save",
  "scene.unload",
  "screenshot.surround",
  "test.run",
  "texture.modify",
  "ui.attach-document",
  "ui.detach-document",
  "ui.document.update",
  "ui.link-stylesheet",
  "ui.update-panel-settings"
]);

/** 生成文档归类；少数跨 group 文档归属由集中 override 维护。 */
export function toolDocsTopic(tool: { name: string; group: string; source?: ToolSource; target?: string }): string {
  if (tool.source === "project") return tool.target === "runtime" ? "runtime-tool" : "project-tool";
  if (tool.name.startsWith("animator.") || tool.name.startsWith("controller.")) return "animation";
  return DOCS_TOPIC_OVERRIDES.get(tool.name) ?? tool.group;
}

/** 生成可用范围；除 runtime 工具外默认是 Editor session。 */
export function toolAvailabilityScope(tool: { availabilityScope?: string; target?: string }): string {
  if (tool.availabilityScope && tool.availabilityScope !== "editorSession") return tool.availabilityScope;
  if (tool.target === "runtime") return "playModeSession";
  return "editorSession";
}

/** 生成工具来源，注册扩展和项目工具时由调用方提供 fallback。 */
export function toolSource(tool: ToolMetadata | { source?: ToolSource }, fallback: ToolSource = "built-in"): ToolSource {
  return (tool as { source?: ToolSource }).source ?? fallback;
}

/** 判断工具是否可能长时间运行，用于 capability summary。 */
export function isLongRunningTool(tool: { name: string; longRunning?: boolean; sideEffects?: string[] }): boolean {
  if (tool.longRunning) return true;
  if (LONG_RUNNING_TOOL_NAMES.has(tool.name)) return true;
  return tool.sideEffects?.some((effect) => effect === "lightmapping-bake" || effect === "editor-long-running") ?? false;
}

/** 风险级别仅作内部派生，公开 descriptor 不投影。 */
export function inferToolRiskLevel(tool: Pick<ToolMetadata, "name" | "destructiveHint" | "permissions" | "sideEffects">): ToolRiskLevel {
  const effects = tool.sideEffects ?? [];
  if (tool.permissions?.includes("execute-user-code") || tool.permissions?.includes("arbitrary-method-invocation") || effects.some((effect) => CRITICAL_RISK_EFFECTS.has(effect))) return "critical";
  if (HIGH_RISK_TOOL_NAMES.has(tool.name) || HIGH_RISK_TOOL_PREFIXES.some((prefix) => tool.name.startsWith(prefix)) || effects.some((effect) => HIGH_RISK_EFFECTS.has(effect))) return "high";
  if (MEDIUM_RISK_TOOL_NAMES.has(tool.name) || effects.some((effect) => MEDIUM_RISK_EFFECTS.has(effect))) return "medium";
  return tool.destructiveHint || effects.length > 0 ? "low" : "safe";
}

/** known Tool 使用 completion contract；custom Tool 无契约时按 descriptor 风险信号保守归类。 */
export function toolIsMutation(tool: Pick<ToolMetadata, "name" | "destructiveHint" | "supportsDryRun" | "sideEffects" | "playModeSupport">): boolean {
  const contracted = TOOL_MUTATION_CONTRACTS.get(tool.name);
  if (contracted !== undefined) return contracted;
  return tool.destructiveHint
    || tool.supportsDryRun === true
    || (tool.sideEffects?.length ?? 0) > 0
    || tool.playModeSupport === "session-affecting";
}
