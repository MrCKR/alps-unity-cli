import { createHash } from "node:crypto";
import {
  MAX_CATALOG_PAGE_FETCH,
  compareText,
  readEditorCatalogGroups,
  readEditorCatalogSummary,
  readRuntimeCatalogGroups,
  readRuntimeCatalogSummary,
  type CatalogGroupsResult,
  type CatalogRoutingSummary,
  type CatalogSummaryResult,
} from "../catalog-protocol.js";
import {
  extensionAvailabilityForPage,
  extensionIdForToolName,
  freezeExtensionAvailability,
} from "../extension-availability.js";
import { failure, success } from "../output.js";
import { ExtensionInstallerError } from "../extensions/installer.js";
import type { CliResult } from "../types.js";
import { publicToolDescriptor, PUBLIC_DESCRIPTOR_FIELDS, isPublicDescriptorField, resolveCatalogTool, type PublicDescriptorField, type ToolCatalogRuntime } from "./catalog.js";
import { builtinToolMetadata, type ResolvedToolMetadata } from "./metadata/index.js";
import { groupSummary } from "./tools/group-summaries.js";

export const DEFAULT_CATALOG_PAGE_SIZE = 20;
export const MAX_CATALOG_PAGE_SIZE = 100;
const MAX_CATALOG_CURSOR_LENGTH = 24 * 1024;

export interface CatalogPageRequest {
  pageSize?: number;
  cursor?: string;
}

interface CatalogCursor {
  version: 1;
  operation: string;
  generations: { local: string; editor?: string; runtime?: string };
  tokens: { editor?: string; runtime?: string };
}

const OFFLINE_CODES = new Set(["E_INSTANCE_NOT_RUNNING", "E_RUNTIME_NOT_RUNNING"]);

/** Group root 对每个 live target 只读取一次 group/count snapshot。 */
export async function projectCatalogGroups(
  flags: Record<string, string | boolean>,
  runtime: ToolCatalogRuntime,
  requestId: string,
  absoluteDeadlineMs?: number,
): Promise<CliResult> {
  const localTools = builtinToolMetadata();
  const localNames = localTools.map((tool) => tool.name).sort(compareText);
  const targets = await targetGroups(flags, runtime, requestId, undefined, localNames, absoluteDeadlineMs);
  if ("failure" in targets) return targets.failure;
  const primary = targets.editor?.ok ? targets.editor : targets.runtime?.ok ? targets.runtime : undefined;
  if (!primary) return targets.offline!;

  const counts = new Map<string, number>();
  for (const tool of localTools) counts.set(tool.group, (counts.get(tool.group) ?? 0) + 1);
  for (const group of primary.groups) counts.set(group.name, (counts.get(group.name) ?? 0) + group.toolCount);
  const groups = [...counts].sort(([left], [right]) => compareText(left, right)).map(([name, toolCount]) => ({
    name,
    summary: groupSummary(name),
    toolCount,
  }));
  return success(requestId, { count: groups.length, groups });
}

/** group/search/command listing 共享由目标generation签发的opaque continuation。 */
export async function projectCatalogPage(
  operation: string,
  request: CatalogPageRequest,
  flags: Record<string, string | boolean>,
  runtime: ToolCatalogRuntime,
  requestId: string,
  filter: {
    group?: string;
    keyword?: string;
    includeExtensionAvailability?: boolean;
    projectItem?: (tool: CatalogRoutingSummary) => Record<string, unknown>;
    collectionName?: "tools" | "commands";
    extraData?: Record<string, unknown>;
  } = {},
  absoluteDeadlineMs?: number,
): Promise<CliResult> {
  const parsed = parsePageRequest(operation, request, requestId);
  if ("failure" in parsed) return parsed.failure;
  const { pageSize, cursor } = parsed;
  const localTools = builtinToolMetadata();
  const localGeneration = localCatalogGeneration(localTools);
  if (cursor && cursor.generations.local !== localGeneration) return catalogChanged(requestId);

  const localMatches = filterLocalTools(localTools, filter).sort((left, right) => compareText(left.name, right.name));
  const localNames = localTools.map((tool) => tool.name).sort(compareText);
  const localMatchNames = localMatches.map((tool) => tool.name);
  const cursorContext = catalogCursorContext(operation, filter, localGeneration, localMatchNames);
  const targets = await targetSummaries(flags, runtime, requestId, {
    group: filter.group,
    keyword: filter.keyword,
    limit: Math.min(MAX_CATALOG_PAGE_FETCH, pageSize),
    expectedEditorGeneration: cursor?.generations.editor,
    expectedRuntimeGeneration: cursor?.generations.runtime,
    editorCursorToken: cursor?.tokens.editor,
    runtimeCursorToken: cursor?.tokens.runtime,
    cursorContext,
    localNames: localMatchNames,
    reservedNames: localNames,
  }, absoluteDeadlineMs);
  if ("failure" in targets) return targets.failure;
  const generationFailure = validateTargetContinuity(cursor, targets, requestId);
  if (generationFailure) return generationFailure;
  const primary = targets.editor?.ok ? targets.editor : targets.runtime?.ok ? targets.runtime : undefined;
  if (!primary) return targets.offline!;

  const pageNames = primary.pageNames;
  const pageNameSet = new Set(pageNames);
  const overlay = targets.editor?.ok && targets.runtime?.ok ? runtimeAvailabilityOverlay(targets.editor, targets.runtime, requestId) : undefined;
  if (overlay && "failure" in overlay) return overlay.failure;
  const primaryTools = primary.tools.map((tool) => overlay instanceof Map && overlay.has(tool.name) ? { ...tool, ...overlay.get(tool.name) } : tool);
  const localPage = localMatches.filter((tool) => pageNameSet.has(tool.name)).map(localRoutingSummary);
  const merged = mergeSummarySources(localPage, primaryTools, requestId);
  if ("failure" in merged) return merged.failure;
  const tools = merged.tools;
  if (tools.length !== pageNames.length || tools.some((tool, index) => tool.name !== pageNames[index])) {
    return catalogConflict(requestId, tools[0]?.name ?? "catalog", "target signed page projection does not match the merged catalog");
  }

  if (filter.includeExtensionAvailability && tools.some((tool) => extensionIdForToolName(tool.name))) {
    try {
      const snapshot = await freezeExtensionAvailability(flags, runtime);
      const checks = await extensionAvailabilityForPage(tools, snapshot, absoluteDeadlineMs);
      for (let index = 0; index < tools.length; index += 1) {
        const check = checks.get(tools[index].name);
        if (check && !check.available) tools[index] = { ...tools[index], available: false, unavailableReason: `${check.errorCode}: ${check.message}` };
      }
    } catch (error) {
      if (error instanceof ExtensionInstallerError) return failure(requestId, error.code, error.message, error.nextActions);
      throw error;
    }
  }

  const total = localMatches.length + primary.total;
  if (filter.group && total === 0) return failure(requestId, "E_TOOL_GROUP_NOT_FOUND", `Tool group not found: ${filter.group}.`, ["Run unitycli tools group to see all tool groups."]);
  const hasMore = primary.hasMore;
  const generations = {
    local: localGeneration,
    ...(targets.editor?.ok ? { editor: targets.editor.generation } : {}),
    ...(targets.runtime?.ok ? { runtime: targets.runtime.generation } : {}),
  };
  const tokens = {
    ...(targets.editor?.ok ? { editor: targets.editor.continuationToken } : {}),
    ...(targets.runtime?.ok ? { runtime: targets.runtime.continuationToken } : {}),
  };
  const cursorText = request.cursor ?? "start";
  const nextCursor = hasMore && tools.length > 0
    ? encodeCursor({ version: 1, operation, generations, tokens })
    : undefined;
  if (hasMore && !nextCursor) return catalogConflict(requestId, tools[tools.length - 1]?.name ?? "catalog", "target omitted its signed continuation token");
  return success(requestId, {
    ...(filter.extraData ?? {}),
    [filter.collectionName ?? "tools"]: filter.projectItem ? tools.map(filter.projectItem) : tools,
    pageInfo: { cursor: cursorText, pageSize, total, ...(nextCursor ? { nextCursor } : {}) },
  });
}

/** Describe root 只获取 exact-name full descriptor，随后至多检查一个 extension。 */
export async function projectCatalogDescribe(
  name: string,
  flags: Record<string, string | boolean>,
  runtime: ToolCatalogRuntime,
  requestId: string,
  absoluteDeadlineMs?: number,
): Promise<CliResult> {
  if (!name) return failure(requestId, "TOOLS_DESCRIBE_NAME_REQUIRED", "Pass a tool name: unitycli tools describe <tool>.", ["Run unitycli tools group to see tool groups."]);
  const selection = describeSelection(flags, requestId);
  if (selection.kind === "error") return selection.result;
  const resolved = await resolveCatalogTool(name, flags, runtime, requestId, absoluteDeadlineMs);
  if (!resolved.ok) return resolved;
  if (!resolved.tool) return failure(requestId, "E_TOOL_TARGET_NOT_FOUND", `Tool not found for target: ${name}.`, [
    "Run unitycli tools group to see tool groups.",
    "Run unitycli tools search <k1|k2|...> to find related tools.",
  ]);
  const data = publicToolDescriptor(resolved.tool, selection.fields);
  if (extensionIdForToolName(resolved.tool.name)) {
    try {
      const snapshot = await freezeExtensionAvailability(flags, runtime);
      const checks = await extensionAvailabilityForPage([resolved.tool], snapshot, absoluteDeadlineMs);
      const availability = checks.get(resolved.tool.name);
      if (availability && !availability.available) {
        data.available = false;
        data.unavailableReason = `${availability.errorCode}: ${availability.message}`;
      }
    } catch (error) {
      if (error instanceof ExtensionInstallerError) return failure(requestId, error.code, error.message, error.nextActions);
      throw error;
    }
  }
  return success(requestId, data);
}

/** describe 字段选择结果；error 携带已构造的稳定 envelope。 */
type DescribeSelection = { kind: "ok"; fields?: readonly PublicDescriptorField[] } | { kind: "error"; result: CliResult };

/** 解析 describe 字段选择：--fields 整体替换默认集合，--detail full 等价于全字段。 */
function describeSelection(flags: Record<string, string | boolean>, requestId: string): DescribeSelection {
  const fieldsFlag = flags.fields;
  const detailFlag = flags.detail;
  if (fieldsFlag !== undefined && detailFlag !== undefined) {
    return { kind: "error", result: failure(requestId, "E_SCHEMA_INVALID", "Pass either --fields or --detail, not both.", [
      "Use --fields <name,...> for an exact field set, or --detail full for every public field.",
    ], { option: "--fields", conflictingOption: "--detail" }) };
  }
  if (detailFlag !== undefined) {
    if (detailFlag !== "full") {
      return { kind: "error", result: failure(requestId, "E_SCHEMA_INVALID", "--detail accepts only 'full'.", ["Pass --detail full for every public field."], { path: "$.detail", expected: "full", actual: String(detailFlag) }) };
    }
    return { kind: "ok", fields: PUBLIC_DESCRIPTOR_FIELDS };
  }
  if (fieldsFlag === undefined) return { kind: "ok" };
  const tokens = String(fieldsFlag).split(",").map((token) => token.trim()).filter(Boolean);
  if (tokens.length === 0) {
    return { kind: "error", result: failure(requestId, "E_SCHEMA_INVALID", "--fields requires at least one field name.", [`Use only: ${PUBLIC_DESCRIPTOR_FIELDS.join(", ")}.`], { path: "$.fields", expected: "non-empty field list" }) };
  }
  const unknown = tokens.filter((token) => !isPublicDescriptorField(token));
  if (unknown.length > 0) {
    return { kind: "error", result: failure(requestId, "E_SCHEMA_INVALID", `Unknown describe field: ${unknown.join(", ")}.`, [`Use only: ${PUBLIC_DESCRIPTOR_FIELDS.join(", ")}.`], { path: "$.fields", unknownFields: unknown }) };
  }
  return { kind: "ok", fields: [...new Set(tokens)] as PublicDescriptorField[] };
}

interface TargetSummaries {
  editor?: CatalogSummaryResult;
  runtime?: CatalogSummaryResult;
  offline?: CliResult;
}

interface TargetGroups {
  editor?: CatalogGroupsResult;
  runtime?: CatalogGroupsResult;
  offline?: CliResult;
}

async function targetSummaries(
  flags: Record<string, string | boolean>,
  runtime: ToolCatalogRuntime,
  requestId: string,
  query: {
    group?: string;
    keyword?: string;
    limit: number;
    expectedEditorGeneration?: string;
    expectedRuntimeGeneration?: string;
    editorCursorToken?: string;
    runtimeCursorToken?: string;
    cursorContext: string;
    localNames: string[];
    reservedNames: string[];
  },
  absoluteDeadlineMs?: number,
): Promise<TargetSummaries | { failure: CliResult }> {
  const editorRuntime = runtimeForTarget(runtime, "editor");
  const editor = await readEditorCatalogSummary(flags, editorRuntime, requestIdForTarget(runtime, requestId, "editor"), {
    group: query.group,
    keyword: query.keyword,
    limit: query.limit,
    expectedGeneration: query.expectedEditorGeneration,
    reservedNames: query.reservedNames,
    localNames: query.localNames,
    cursorContext: query.cursorContext,
    cursorToken: query.editorCursorToken,
  }, absoluteDeadlineMs);
  if (!editor.ok && !OFFLINE_CODES.has(editor.error.code)) return { failure: editor };

  const runtimeTarget = await readRuntimeCatalogSummary(flags, runtimeForTarget(runtime, "runtime"), requestIdForTarget(runtime, requestId, "runtime"), {
    group: query.group,
    keyword: query.keyword,
    limit: query.limit,
    expectedGeneration: query.expectedRuntimeGeneration,
    reservedNames: query.reservedNames,
    localNames: query.localNames,
    cursorContext: query.cursorContext,
    cursorToken: query.runtimeCursorToken,
  }, absoluteDeadlineMs);
  if (!runtimeTarget.ok && !OFFLINE_CODES.has(runtimeTarget.error.code)) return { failure: runtimeTarget };
  return {
    ...(editor.ok ? { editor } : {}),
    ...(runtimeTarget.ok ? { runtime: runtimeTarget } : {}),
    ...(!editor.ok ? { offline: editor } : !runtimeTarget.ok ? { offline: runtimeTarget } : {}),
  };
}

async function targetGroups(
  flags: Record<string, string | boolean>,
  runtime: ToolCatalogRuntime,
  requestId: string,
  expected: { editor?: string; runtime?: string } | undefined,
  reservedNames: string[],
  absoluteDeadlineMs?: number,
): Promise<TargetGroups | { failure: CliResult }> {
  const editor = await readEditorCatalogGroups(flags, runtimeForTarget(runtime, "editor"), requestIdForTarget(runtime, requestId, "editor"), expected?.editor, reservedNames, absoluteDeadlineMs);
  if (!editor.ok && !OFFLINE_CODES.has(editor.error.code)) return { failure: editor };
  const runtimeTarget = await readRuntimeCatalogGroups(flags, runtimeForTarget(runtime, "runtime"), requestIdForTarget(runtime, requestId, "runtime"), expected?.runtime, reservedNames, absoluteDeadlineMs);
  if (!runtimeTarget.ok && !OFFLINE_CODES.has(runtimeTarget.error.code)) return { failure: runtimeTarget };
  return {
    ...(editor.ok ? { editor } : {}),
    ...(runtimeTarget.ok ? { runtime: runtimeTarget } : {}),
    ...(!editor.ok ? { offline: editor } : !runtimeTarget.ok ? { offline: runtimeTarget } : {}),
  };
}

function runtimeForTarget(runtime: ToolCatalogRuntime, target: "editor" | "runtime"): ToolCatalogRuntime {
  const admission = runtime.rootAdmissions?.[target] ?? (runtime.rootAdmission?.target === target ? runtime.rootAdmission : undefined);
  return { ...runtime, rootAdmission: admission };
}

function requestIdForTarget(runtime: ToolCatalogRuntime, requestId: string, target: "editor" | "runtime"): string {
  return runtime.rootAdmissions?.[target]?.requestId
    ?? (runtime.rootAdmission?.target === target ? runtime.rootAdmission.requestId : undefined)
    ?? `${requestId}_${target}_catalog`;
}

function validateTargetContinuity(cursor: CatalogCursor | undefined, targets: TargetSummaries, requestId: string): CliResult | undefined {
  if (!cursor) return undefined;
  if (Boolean(cursor.generations.editor) !== Boolean(targets.editor?.ok)
      || Boolean(cursor.generations.runtime) !== Boolean(targets.runtime?.ok)) return catalogChanged(requestId);
  if (Boolean(cursor.tokens.editor) !== Boolean(targets.editor?.ok)
      || Boolean(cursor.tokens.runtime) !== Boolean(targets.runtime?.ok)) return catalogChanged(requestId);
  return undefined;
}

function runtimeAvailabilityOverlay(
  editor: CatalogSummaryResult & { ok: true },
  runtime: CatalogSummaryResult & { ok: true },
  requestId: string,
): Map<string, Pick<CatalogRoutingSummary, "available" | "unavailableReason">> | { failure: CliResult } {
  const editorByName = new Map(editor.tools.map((tool) => [tool.name, tool]));
  const overlay = new Map<string, Pick<CatalogRoutingSummary, "available" | "unavailableReason">>();
  for (const runtimeTool of runtime.tools) {
    const editorTool = editorByName.get(runtimeTool.name);
    if (!editorTool) continue;
    if (editorTool.group !== runtimeTool.group || editorTool.target !== runtimeTool.target
        || editorTool.contractProfile !== runtimeTool.contractProfile) {
      return { failure: catalogConflict(requestId, runtimeTool.name, "runtime registration does not match the instance Tool registry") };
    }
    if (editorTool.target === "runtime") {
      overlay.set(runtimeTool.name, runtimeTool.available === false
        ? { available: false, unavailableReason: runtimeTool.unavailableReason }
        : {});
    }
  }
  return overlay;
}

function mergeSummarySources(
  local: CatalogRoutingSummary[],
  live: CatalogRoutingSummary[],
  requestId: string,
): { tools: CatalogRoutingSummary[] } | { failure: CliResult } {
  const merged = new Map<string, CatalogRoutingSummary>();
  for (const tool of local) merged.set(tool.name, tool);
  for (const tool of live) {
    if (merged.has(tool.name)) return { failure: catalogConflict(requestId, tool.name, "package-local and live registrations use the same name") };
    merged.set(tool.name, tool);
  }
  return { tools: [...merged.values()].sort((left, right) => compareText(left.name, right.name)) };
}

function parsePageRequest(
  operation: string,
  request: CatalogPageRequest,
  requestId: string,
): { pageSize: number; cursor?: CatalogCursor } | { failure: CliResult } {
  const pageSize = request.pageSize ?? DEFAULT_CATALOG_PAGE_SIZE;
  if (!Number.isSafeInteger(pageSize) || pageSize < 1 || pageSize > MAX_CATALOG_PAGE_SIZE) {
    return { failure: failure(requestId, "E_SCHEMA_INVALID", `${operation} pageSize must be a [1,${MAX_CATALOG_PAGE_SIZE}] integer.`, [
      "Pass a valid pageSize and use pageInfo.nextCursor unchanged to continue."
    ], { path: "$.pageSize", expected: `[1,${MAX_CATALOG_PAGE_SIZE}] integer`, actual: request.pageSize ?? null }) };
  }
  if (request.cursor === undefined) return { pageSize };
  const cursor = decodeCursor(request.cursor);
  if (!cursor || cursor.operation !== operation) {
    return { failure: failure(requestId, "E_DISCOVERY_CURSOR_INVALID", "The discovery cursor is malformed or belongs to another query.", [
      "Restart this exact discovery query from its first page."
    ], { path: "$.cursor", expected: "opaque cursor returned by this query", actual: request.cursor }) };
  }
  return { pageSize, cursor };
}

function encodeCursor(cursor: CatalogCursor): string {
  return `v1.${Buffer.from(JSON.stringify(cursor), "utf8").toString("base64url")}`;
}

function decodeCursor(value: string): CatalogCursor | undefined {
  if (value.length > MAX_CATALOG_CURSOR_LENGTH || !value.startsWith("v1.")) return undefined;
  try {
    const parsed = JSON.parse(Buffer.from(value.slice(3), "base64url").toString("utf8")) as Partial<CatalogCursor>;
    const generations = parsed.generations;
    const tokens = parsed.tokens;
    if (!parsed || Object.keys(parsed).some((key) => !["version", "operation", "generations", "tokens"].includes(key))
        || parsed.version !== 1 || typeof parsed.operation !== "string" || !parsed.operation || parsed.operation.length > 2300
        || !generations || Object.keys(generations).some((key) => !["local", "editor", "runtime"].includes(key))
        || typeof generations.local !== "string" || !generations.local || generations.local.length > 128
        || (generations.editor !== undefined && (typeof generations.editor !== "string" || !generations.editor || generations.editor.length > 128))
        || (generations.runtime !== undefined && (typeof generations.runtime !== "string" || !generations.runtime || generations.runtime.length > 128))
        || !tokens || Object.keys(tokens).some((key) => !["editor", "runtime"].includes(key))
        || (tokens.editor !== undefined && (typeof tokens.editor !== "string" || !tokens.editor || tokens.editor.length > MAX_CATALOG_CURSOR_LENGTH))
        || (tokens.runtime !== undefined && (typeof tokens.runtime !== "string" || !tokens.runtime || tokens.runtime.length > MAX_CATALOG_CURSOR_LENGTH))) return undefined;
    return parsed as CatalogCursor;
  } catch {
    return undefined;
  }
}

/** scope同时绑定operation、query与本地catalog generation，不能跨命令复用。 */
function catalogCursorContext(
  operation: string,
  filter: { group?: string; keyword?: string },
  localGeneration: string,
  localNames: string[],
): string {
  return createHash("sha256").update(JSON.stringify({
    operation,
    group: filter.group ?? "",
    keyword: filter.keyword ?? "",
    localGeneration,
    localNames,
  }), "utf8").digest("hex");
}

function filterLocalTools(
  tools: ResolvedToolMetadata[],
  filter: { group?: string; keyword?: string },
): ResolvedToolMetadata[] {
  const tokens = (filter.keyword ?? "").split("|").map((token) => token.trim().toLowerCase()).filter(Boolean);
  return tools.filter((tool) => (!filter.group || tool.group === filter.group)
    && (tokens.length === 0 || tokens.some((token) => [tool.name, tool.summary, tool.description, tool.group]
      .some((value) => value.toLowerCase().includes(token)))));
}

function localRoutingSummary(tool: ResolvedToolMetadata): CatalogRoutingSummary {
  return {
    name: tool.name,
    summary: tool.summary,
    group: tool.group,
    target: tool.target === "runtime" || tool.target === "both" ? tool.target : "editor",
    contractProfile: tool.contractProfile ?? "native",
    commandName: tool.name,
    ...((tool.available ?? true) === false ? { available: false, unavailableReason: tool.unavailableReason } : {}),
  };
}

function localCatalogGeneration(tools: ResolvedToolMetadata[]): string {
  return createHash("sha256").update(JSON.stringify(tools.map((tool) => [
    tool.name, tool.summary, tool.description, tool.group, tool.target, tool.contractProfile, tool.available ?? true, tool.unavailableReason ?? "",
  ]).sort(([left], [right]) => compareText(String(left), String(right)))), "utf8").digest("hex");
}

function catalogChanged(requestId: string): CliResult {
  return failure(requestId, "E_TOOL_CATALOG_CHANGED", "The Tool catalog generation changed before this continuation page was read.", [
    "Restart this discovery query from its first page against the current Unity generation."
  ]);
}

function catalogConflict(requestId: string, name: string, reason: string): CliResult {
  return failure(requestId, "E_TOOL_SCHEMA_CONFLICT", `Tool catalog conflict for ${name}: ${reason}.`, [
    "Fix the duplicate Tool registration in the selected Unity project, then retry discovery."
  ], { toolName: name, reason });
}
