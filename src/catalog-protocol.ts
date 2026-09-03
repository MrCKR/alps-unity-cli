import { createHash, createHmac, randomBytes, timingSafeEqual } from "node:crypto";
import { executeBridgePayload } from "./bridge/index.js";
import { failure } from "./output.js";
import { projectDescriptorToToolMetadata, type ProjectToolRuntime } from "./project-tool-discovery.js";
import type { CliResult } from "./types.js";
import { normalizeToolMetadata, type ResolvedToolMetadata, type ToolMetadata } from "./tools/metadata/index.js";

export const MAX_CATALOG_PAGE_FETCH = 101;
const MAX_CATALOG_SUMMARY_BYTES = 256 * 1024;
const MAX_CATALOG_GROUPS_BYTES = 2 * 1024 * 1024;
const MAX_CATALOG_DESCRIPTOR_BYTES = 3 * 1024 * 1024;
const fixtureCursorSecret = randomBytes(32);

export interface CatalogSummaryQuery {
  group?: string;
  keyword?: string;
  limit: number;
  expectedGeneration?: string;
  reservedNames: string[];
  localNames: string[];
  cursorContext: string;
  cursorToken?: string;
}

export interface CatalogRoutingSummary {
  name: string;
  summary: string;
  group: string;
  target: "editor" | "runtime" | "both";
  contractProfile: "native" | "official-attribute";
  commandName?: string;
  available?: false;
  unavailableReason?: string;
}

export interface CatalogGroupSummary {
  name: string;
  toolCount: number;
}

export type CatalogSummaryResult = {
  ok: true;
  generation: string;
  total: number;
  tools: CatalogRoutingSummary[];
  hasMore: boolean;
  pageNames: string[];
  continuationToken: string;
} | (CliResult & { ok: false });

export type CatalogGroupsResult = {
  ok: true;
  generation: string;
  groups: CatalogGroupSummary[];
} | (CliResult & { ok: false });

export type CatalogDescribeResult = {
  ok: true;
  generation: string;
  tool?: ResolvedToolMetadata;
} | (CliResult & { ok: false });

/** Editor catalog summary 在 Bridge 主线程完成筛选和上限裁剪。 */
export function readEditorCatalogSummary(
  flags: Record<string, string | boolean>,
  runtime: ProjectToolRuntime,
  requestId: string,
  query: CatalogSummaryQuery,
  absoluteDeadlineMs?: number,
): Promise<CatalogSummaryResult> {
  return readCatalogSummary("editor", flags, runtime, requestId, query, absoluteDeadlineMs);
}

/** Runtime catalog summary 只补充当前 Runtime target 的可用性。 */
export function readRuntimeCatalogSummary(
  flags: Record<string, string | boolean>,
  runtime: ProjectToolRuntime,
  requestId: string,
  query: CatalogSummaryQuery,
  absoluteDeadlineMs?: number,
): Promise<CatalogSummaryResult> {
  return readCatalogSummary("runtime", flags, runtime, requestId, query, absoluteDeadlineMs);
}

/** Group 聚合只传输 group/count，不物化 descriptor 图。 */
export function readEditorCatalogGroups(
  flags: Record<string, string | boolean>,
  runtime: ProjectToolRuntime,
  requestId: string,
  expectedGeneration: string | undefined,
  reservedNames: string[],
  absoluteDeadlineMs?: number,
): Promise<CatalogGroupsResult> {
  return readCatalogGroups("editor", flags, runtime, requestId, expectedGeneration, reservedNames, absoluteDeadlineMs);
}

export function readRuntimeCatalogGroups(
  flags: Record<string, string | boolean>,
  runtime: ProjectToolRuntime,
  requestId: string,
  expectedGeneration: string | undefined,
  reservedNames: string[],
  absoluteDeadlineMs?: number,
): Promise<CatalogGroupsResult> {
  return readCatalogGroups("runtime", flags, runtime, requestId, expectedGeneration, reservedNames, absoluteDeadlineMs);
}

/** exact descriptor retrieval 是 schema/执行解析的唯一 live 入口。 */
export function readEditorCatalogDescriptor(
  flags: Record<string, string | boolean>,
  runtime: ProjectToolRuntime,
  requestId: string,
  name: string,
  absoluteDeadlineMs?: number,
  fallbackName?: string,
): Promise<CatalogDescribeResult> {
  return readCatalogDescriptor("editor", flags, runtime, requestId, name, absoluteDeadlineMs, fallbackName);
}

export function readRuntimeCatalogDescriptor(
  flags: Record<string, string | boolean>,
  runtime: ProjectToolRuntime,
  requestId: string,
  name: string,
  absoluteDeadlineMs?: number,
): Promise<CatalogDescribeResult> {
  return readCatalogDescriptor("runtime", flags, runtime, requestId, name, absoluteDeadlineMs);
}

async function readCatalogSummary(
  target: "editor" | "runtime",
  flags: Record<string, string | boolean>,
  runtime: ProjectToolRuntime,
  requestId: string,
  query: CatalogSummaryQuery,
  absoluteDeadlineMs?: number,
): Promise<CatalogSummaryResult> {
  const queryFailure = validateSummaryQuery(requestId, query);
  if (queryFailure) return queryFailure;
  const fixture = target === "runtime" ? runtime.runtimeTools ?? runtime.projectTools : runtime.projectTools;
  if (fixture) return summarizeFixture(target, fixture, requestId, query);

  const command = `${target === "editor" ? "project" : "runtime"}-tool.summary`;
  const result = await executeBridgePayload(requestId, command, {
    ...(query.group ? { group: query.group } : {}),
    ...(query.keyword ? { keyword: query.keyword } : {}),
    limit: query.limit,
    ...(query.expectedGeneration ? { expectedCatalogGeneration: query.expectedGeneration } : {}),
    reservedNames: query.reservedNames,
    ...(query.localNames ? { localNames: query.localNames } : {}),
    ...(query.cursorContext ? { cursorContext: query.cursorContext } : {}),
    ...(query.cursorToken ? { cursorToken: query.cursorToken } : {}),
  }, flags, runtime, { mode: "read", mutation: false, absoluteDeadlineMs });
  if (!result.ok) return result as CliResult & { ok: false };
  return parseSummaryResponse(result.data, requestId, command, query);
}

async function readCatalogGroups(
  target: "editor" | "runtime",
  flags: Record<string, string | boolean>,
  runtime: ProjectToolRuntime,
  requestId: string,
  expectedGeneration: string | undefined,
  reservedNames: string[],
  absoluteDeadlineMs?: number,
): Promise<CatalogGroupsResult> {
  const boundsFailure = validateGenerationAndReservedNames(requestId, expectedGeneration, reservedNames);
  if (boundsFailure) return boundsFailure;
  const fixture = target === "runtime" ? runtime.runtimeTools ?? runtime.projectTools : runtime.projectTools;
  if (fixture) return groupsFixture(target, fixture, requestId, expectedGeneration, reservedNames);

  const command = `${target === "editor" ? "project" : "runtime"}-tool.groups`;
  const result = await executeBridgePayload(requestId, command, {
    ...(expectedGeneration ? { expectedCatalogGeneration: expectedGeneration } : {}),
    reservedNames,
  }, flags, runtime, { mode: "read", mutation: false, absoluteDeadlineMs });
  if (!result.ok) return result as CliResult & { ok: false };
  return parseGroupsResponse(result.data, requestId, command);
}

async function readCatalogDescriptor(
  target: "editor" | "runtime",
  flags: Record<string, string | boolean>,
  runtime: ProjectToolRuntime,
  requestId: string,
  name: string,
  absoluteDeadlineMs?: number,
  fallbackName?: string,
): Promise<CatalogDescribeResult> {
  if (!name || utf8ByteLength(name) > 256) return schemaFailure(requestId, "Catalog describe name is required and must not exceed 256 UTF-8 bytes.");
  if (fallbackName !== undefined && (!fallbackName || utf8ByteLength(fallbackName) > 256 || fallbackName === name)) {
    return schemaFailure(requestId, "Catalog describe fallbackName must be distinct and must not exceed 256 characters.");
  }
  const fixture = target === "runtime" ? runtime.runtimeTools ?? runtime.projectTools : runtime.projectTools;
  if (fixture) {
    const tools = fixtureTools(target, fixture);
    const integrity = fixtureCatalogIntegrity(requestId, tools);
    if (integrity) return integrity;
    return {
      ok: true,
      generation: fixtureGeneration(target, tools),
      tool: tools.find((tool) => tool.name === name) ?? (fallbackName ? tools.find((tool) => tool.name === fallbackName) : undefined),
    };
  }

  const command = `${target === "editor" ? "project" : "runtime"}-tool.describe`;
  const input = target === "editor" && fallbackName ? { name, fallbackName } : { name };
  const result = await executeBridgePayload(requestId, command, input, flags, runtime, { mode: "read", mutation: false, absoluteDeadlineMs });
  if (!result.ok) return result as CliResult & { ok: false };
  return parseDescriptorResponse(result.data, requestId, command, name, target, target === "editor" ? fallbackName : undefined);
}

/** 测试注入仍执行真实筛选、generation 与 ownership 规则。 */
function summarizeFixture(
  target: "editor" | "runtime",
  fixture: ToolMetadata[],
  requestId: string,
  query: CatalogSummaryQuery,
): CatalogSummaryResult {
  const tools = fixtureTools(target, fixture);
  const integrity = fixtureCatalogIntegrity(requestId, tools);
  if (integrity) return integrity;
  const generation = fixtureGeneration(target, tools);
  const generationFailure = requireGeneration(requestId, query.expectedGeneration, generation);
  if (generationFailure) return generationFailure;
  const ownershipFailure = reservedNameConflict(requestId, tools, query.reservedNames);
  if (ownershipFailure) return ownershipFailure;
  const tokens = searchTokens(query.keyword);
  const matches = tools.filter((tool) =>
    (!query.group || tool.group === query.group)
    && (tokens.length === 0 || tokens.some((token) => [tool.name, tool.summary, tool.description, tool.group]
      .some((value) => value.toLowerCase().includes(token))))
  ).sort(compareToolName);
  const nativeNames = new Set([...tools.filter((item) => item.contractProfile !== "official-attribute").map((item) => item.name), ...query.reservedNames]);
  const scope = fixtureCursorScope(target, generation, query);
  const cursor = query.cursorToken ? verifyFixtureCursor(query.cursorToken, scope) : { offset: 0, boundary: "" };
  if (!cursor) return invalidCursor(requestId);
  const localNames = query.localNames ?? [];
  const combinedNames = [...new Set([...localNames, ...matches.map((tool) => tool.name)])].sort(compareText);
  if (cursor.offset > combinedNames.length || cursor.offset > 0 && combinedNames[cursor.offset - 1] !== cursor.boundary) return invalidCursor(requestId);
  const end = Math.min(cursor.offset + query.limit, combinedNames.length);
  const pageNames = new Set(combinedNames.slice(cursor.offset, end));
  const hasMore = end < combinedNames.length;
  return {
    ok: true,
    generation,
    total: matches.length,
    tools: matches.filter((tool) => pageNames.has(tool.name)).map((tool) => routingSummary(tool, nativeNames)),
    hasMore,
    pageNames: combinedNames.slice(cursor.offset, end),
    continuationToken: signFixtureCursor(scope, end, combinedNames[end - 1] ?? cursor.boundary),
  };
}

function groupsFixture(
  target: "editor" | "runtime",
  fixture: ToolMetadata[],
  requestId: string,
  expectedGeneration: string | undefined,
  reservedNames: string[],
): CatalogGroupsResult {
  const tools = fixtureTools(target, fixture);
  const integrity = fixtureCatalogIntegrity(requestId, tools);
  if (integrity) return integrity;
  const generation = fixtureGeneration(target, tools);
  const generationFailure = requireGeneration(requestId, expectedGeneration, generation);
  if (generationFailure) return generationFailure;
  const ownershipFailure = reservedNameConflict(requestId, tools, reservedNames);
  if (ownershipFailure) return ownershipFailure;
  const groups = new Map<string, number>();
  for (const tool of tools) groups.set(tool.group, (groups.get(tool.group) ?? 0) + 1);
  return {
    ok: true,
    generation,
    groups: [...groups].sort(([left], [right]) => compareText(left, right)).map(([name, toolCount]) => ({ name, toolCount })),
  };
}

function fixtureTools(target: "editor" | "runtime", fixture: ToolMetadata[]): ResolvedToolMetadata[] {
  return fixture.map((tool) => normalizeToolMetadata(tool, tool.source ?? "project"))
    .filter((tool) => target === "editor" || tool.target === "runtime" || tool.target === "both");
}

function fixtureCatalogIntegrity(requestId: string, tools: ResolvedToolMetadata[]): (CliResult & { ok: false }) | undefined {
  const names = new Set<string>();
  for (const tool of tools) {
    if (utf8ByteLength(tool.name) > 256 || utf8ByteLength(tool.summary) > 512 || utf8ByteLength(tool.group) > 256) return failure(requestId, "E_TOOL_REGISTRY_UNHEALTHY", `Tool ${tool.name} exceeds a catalog routing field bound.`, ["Shorten the Tool name, group, or summary before retrying discovery."]) as CliResult & { ok: false };
    if (names.has(tool.name)) return failure(requestId, "E_TOOL_SCHEMA_CONFLICT", `Tool catalog conflict for ${tool.name}: duplicate live registrations use the same name.`, ["Fix the duplicate Tool registration, then retry discovery."]) as CliResult & { ok: false };
    names.add(tool.name);
    const attribute = tool.contractProfile === "official-attribute";
    const prefix = tool.name.startsWith("UnityPipeline_");
    const group = tool.group === "UnityPipeline";
    if (attribute && (!prefix || !group)) return failure(requestId, "E_TOOL_SCHEMA_CONFLICT", `Tool catalog conflict for ${tool.name}: official-attribute registration must use the UnityPipeline_ prefix and UnityPipeline group.`, ["Fix the Tool registration, then retry discovery."]) as CliResult & { ok: false };
    if (!attribute && (prefix || group)) return failure(requestId, "E_TOOL_SCHEMA_CONFLICT", `Tool catalog conflict for ${tool.name}: native registration cannot use the reserved UnityPipeline_ prefix or UnityPipeline group.`, ["Fix the Tool registration, then retry discovery."]) as CliResult & { ok: false };
  }
  return undefined;
}

function fixtureGeneration(target: "editor" | "runtime", tools: ResolvedToolMetadata[]): string {
  const material = tools.sort(compareToolName).map((tool) => [
    tool.name, tool.summary, tool.description, tool.group, tool.target, tool.contractProfile,
    tool.available ?? true, tool.unavailableReason ?? "",
  ]);
  return createHash("sha256").update(JSON.stringify({ target, tools: material }), "utf8").digest("hex");
}

function routingSummary(tool: ResolvedToolMetadata, nativeNames: ReadonlySet<string>): CatalogRoutingSummary {
  const available = tool.available ?? true;
  const attributeName = tool.contractProfile === "official-attribute" && tool.name.startsWith("UnityPipeline_")
    ? tool.name.slice("UnityPipeline_".length)
    : tool.name;
  return {
    name: tool.name,
    summary: tool.summary,
    group: tool.group,
    target: tool.target === "runtime" || tool.target === "both" ? tool.target : "editor",
    contractProfile: tool.contractProfile ?? "native",
    commandName: tool.contractProfile === "official-attribute" && !nativeNames.has(attributeName) ? attributeName : tool.name,
    ...(!available ? { available: false as const, unavailableReason: tool.unavailableReason } : {}),
  };
}

function parseSummaryResponse(data: unknown, requestId: string, command: string, query: CatalogSummaryQuery): CatalogSummaryResult {
  if (jsonByteLength(data) > MAX_CATALOG_SUMMARY_BYTES) return unhealthySummary(requestId, `${command} exceeded the bounded summary transport size.`);
  const record = objectRecord(data);
  const generation = stringField(record, "catalogGeneration");
  const total = numberField(record, "total");
  const hasMore = record?.hasMore;
  const rawTools = record?.tools;
  const continuationToken = record?.continuationToken;
  const rawPageNames = record?.pageNames;
  const allowedKeys = ["catalogGeneration", "total", "hasMore", "tools", "pageNames", "continuationToken"];
  if (!record || !hasOnlyKeys(record, allowedKeys)
      || !generation || utf8ByteLength(generation) > 128 || total === undefined || !Number.isSafeInteger(total) || total < 0
      || typeof hasMore !== "boolean" || !Array.isArray(rawTools) || rawTools.length > query.limit || rawTools.length > total
      || !Array.isArray(rawPageNames) || rawPageNames.length > query.limit
      || rawPageNames.some((name) => typeof name !== "string" || !name || utf8ByteLength(name) > 256)
      || !strictlySortedUnique(rawPageNames as string[])
      || typeof continuationToken !== "string" || !continuationToken || utf8ByteLength(continuationToken) > 8192) {
    return unhealthySummary(requestId, `${command} returned an invalid bounded summary envelope.`);
  }
  const tools: CatalogRoutingSummary[] = [];
  for (let index = 0; index < rawTools.length; index += 1) {
    const parsed = parseRoutingSummary(rawTools[index]);
    if (!parsed) return unhealthySummary(requestId, `${command} tools[${index}] is not a routing summary.`);
    if (query.group && parsed.group !== query.group) return unhealthySummary(requestId, `${command} tools[${index}] escaped the requested group.`);
    tools.push(parsed);
  }
  if (!strictlySortedUnique(tools.map((tool) => tool.name))) {
    return unhealthySummary(requestId, `${command} summary names must be unique and ordinally sorted.`);
  }
  return {
    ok: true,
    generation,
    total,
    tools,
    hasMore,
    pageNames: rawPageNames as string[],
    continuationToken: continuationToken as string,
  };
}

function parseGroupsResponse(data: unknown, requestId: string, command: string): CatalogGroupsResult {
  if (jsonByteLength(data) > MAX_CATALOG_GROUPS_BYTES) return unhealthySummary(requestId, `${command} exceeded the bounded group transport size.`);
  const record = objectRecord(data);
  const generation = stringField(record, "catalogGeneration");
  const rawGroups = record?.groups;
  if (!record || !hasOnlyKeys(record, ["catalogGeneration", "groups"]) || !generation || utf8ByteLength(generation) > 128
      || !Array.isArray(rawGroups) || rawGroups.length > 4096) {
    return unhealthySummary(requestId, `${command} returned an invalid group envelope.`);
  }
  const groups: CatalogGroupSummary[] = [];
  for (let index = 0; index < rawGroups.length; index += 1) {
    const item = objectRecord(rawGroups[index]);
    const name = stringField(item, "name");
    const toolCount = numberField(item, "toolCount");
    if (!item || !hasOnlyKeys(item, ["name", "toolCount"]) || !name || utf8ByteLength(name) > 256
        || toolCount === undefined || !Number.isSafeInteger(toolCount) || toolCount < 1) {
      return unhealthySummary(requestId, `${command} groups[${index}] is invalid.`);
    }
    groups.push({ name, toolCount });
  }
  if (!strictlySortedUnique(groups.map((group) => group.name))) return unhealthySummary(requestId, `${command} group names must be unique and ordinally sorted.`);
  return { ok: true, generation, groups };
}

function parseDescriptorResponse(
  data: unknown,
  requestId: string,
  command: string,
  expectedName: string,
  target: "editor" | "runtime",
  fallbackName?: string,
): CatalogDescribeResult {
  if (jsonByteLength(data) > MAX_CATALOG_DESCRIPTOR_BYTES) return unhealthySummary(requestId, `${command} exceeded the bounded exact descriptor transport size.`);
  const record = objectRecord(data);
  const generation = stringField(record, "catalogGeneration");
  if (!record || !generation || utf8ByteLength(generation) > 128 || typeof record.found !== "boolean"
      || !hasOnlyKeys(record, record.found ? ["catalogGeneration", "found", "descriptor"] : ["catalogGeneration", "found"])) {
    return unhealthySummary(requestId, `${command} returned an invalid exact descriptor envelope.`);
  }
  if (!record.found) return { ok: true, generation };
  const tool = projectDescriptorToToolMetadata(record.descriptor);
  if (!tool || tool.name !== expectedName && tool.name !== fallbackName
      || target === "runtime" && tool.target !== "runtime" && tool.target !== "both") {
    return unhealthySummary(requestId, `${command} returned a descriptor outside the exact requested identity.`);
  }
  return { ok: true, generation, tool };
}

function parseRoutingSummary(value: unknown): CatalogRoutingSummary | undefined {
  const record = objectRecord(value);
  const name = stringField(record, "name");
  const summary = stringField(record, "summary");
  const group = stringField(record, "group");
  const target = record?.target;
  const contractProfile = record?.contractProfile;
  const commandName = record?.commandName;
  const available = record?.available;
  const unavailableReason = record?.unavailableReason;
  if (!record || !hasOnlyKeys(record, ["name", "summary", "group", "target", "contractProfile", "commandName", "available", "unavailableReason"])
      || !name || utf8ByteLength(name) > 256 || !summary || utf8ByteLength(summary) > 512 || !group || utf8ByteLength(group) > 256
      || !(target === "editor" || target === "runtime" || target === "both")
      || !(contractProfile === "native" || contractProfile === "official-attribute")
      || (commandName !== undefined && (typeof commandName !== "string" || !commandName || utf8ByteLength(commandName) > 256))
      || (available !== undefined && available !== false)
      || (available === false && (typeof unavailableReason !== "string" || !unavailableReason || utf8ByteLength(unavailableReason) > 2048))
      || (available !== false && unavailableReason !== undefined)) return undefined;
  const usesAttributePrefix = name.startsWith("UnityPipeline_");
  const usesAttributeGroup = group === "UnityPipeline";
  const attributeName = usesAttributePrefix ? name.slice("UnityPipeline_".length) : "";
  if (contractProfile === "native" && (usesAttributePrefix || usesAttributeGroup || commandName !== undefined && commandName !== name)) return undefined;
  if (contractProfile === "official-attribute" && (!usesAttributePrefix || !usesAttributeGroup || !attributeName
      || commandName !== undefined && commandName !== name && commandName !== attributeName)) return undefined;
  return {
    name,
    summary,
    group,
    target,
    contractProfile,
    ...(typeof commandName === "string" ? { commandName } : {}),
    ...(available === false ? { available: false, unavailableReason: unavailableReason as string } : {}),
  };
}

/** 注入fixture使用同形scope，确保测试不会绕过cursor integrity。 */
function fixtureCursorScope(target: "editor" | "runtime", generation: string, query: CatalogSummaryQuery): string {
  return createHash("sha256").update(JSON.stringify({
    target,
    generation,
    cursorContext: query.cursorContext,
    group: query.group ?? "",
    keyword: query.keyword ?? "",
    localNames: query.localNames ?? [],
    reservedNames: query.reservedNames,
  }), "utf8").digest("hex");
}

/** fixture secret仅活在当前测试进程，不进入产品持久状态。 */
function signFixtureCursor(scope: string, offset: number, boundary: string): string {
  const payload = Buffer.from(JSON.stringify({ scope, offset, boundary }), "utf8").toString("base64url");
  const signature = createHmac("sha256", fixtureCursorSecret).update(payload, "utf8").digest("base64url");
  return `${payload}.${signature}`;
}

/** 验证fixture token签名并恢复其不可编辑边界。 */
function verifyFixtureCursor(token: string, scope: string): { offset: number; boundary: string } | undefined {
  try {
    const [payload, signature, extra] = token.split(".");
    if (!payload || !signature || extra !== undefined) return undefined;
    const actual = Buffer.from(signature, "base64url");
    const expected = createHmac("sha256", fixtureCursorSecret).update(payload, "utf8").digest();
    if (actual.length !== expected.length || !timingSafeEqual(actual, expected)) return undefined;
    const parsed = JSON.parse(Buffer.from(payload, "base64url").toString("utf8")) as Record<string, unknown>;
    if (Object.keys(parsed).some((key) => !["scope", "offset", "boundary"].includes(key))
        || parsed.scope !== scope || !Number.isSafeInteger(parsed.offset) || Number(parsed.offset) < 0
        || typeof parsed.boundary !== "string" || utf8ByteLength(parsed.boundary) > 256
        || Number(parsed.offset) === 0 !== (parsed.boundary === "")) return undefined;
    return { offset: Number(parsed.offset), boundary: parsed.boundary };
  } catch {
    return undefined;
  }
}

/** 统一fixture与live target的cursor拒绝语义。 */
function invalidCursor(requestId: string): CliResult & { ok: false } {
  return failure(requestId, "E_DISCOVERY_CURSOR_INVALID", "The target rejected an altered or cross-query discovery continuation.", [
    "Restart this exact discovery query from its first page."
  ]) as CliResult & { ok: false };
}

function requireGeneration(requestId: string, expected: string | undefined, actual: string): (CliResult & { ok: false }) | undefined {
  if (!expected || expected === actual) return undefined;
  return failure(requestId, "E_TOOL_CATALOG_CHANGED", "The Tool catalog generation changed before this continuation page was read.", [
    "Restart this discovery query from its first page against the current Unity generation."
  ]) as CliResult & { ok: false };
}

function reservedNameConflict(requestId: string, tools: ResolvedToolMetadata[], reservedNames: string[]): (CliResult & { ok: false }) | undefined {
  const reserved = new Set(reservedNames);
  const conflict = tools.find((tool) => reserved.has(tool.name));
  if (!conflict) return undefined;
  return failure(requestId, "E_TOOL_SCHEMA_CONFLICT", `Tool catalog conflict for ${conflict.name}: package-local and live registrations use the same name.`, [
    "Fix the duplicate Tool registration in the selected Unity project, then retry discovery."
  ], { toolName: conflict.name, reason: "package-local and live registrations use the same name" }) as CliResult & { ok: false };
}

function unhealthySummary(requestId: string, message: string): CliResult & { ok: false } {
  return failure(requestId, "E_TOOL_REGISTRY_UNHEALTHY", message, [
    "Fix the Tool descriptor registry, then let Unity compile and reload the domain before retrying discovery."
  ]) as CliResult & { ok: false };
}

function validateSummaryQuery(requestId: string, query: CatalogSummaryQuery): (CliResult & { ok: false }) | undefined {
  if (!Number.isSafeInteger(query.limit) || query.limit < 1 || query.limit > MAX_CATALOG_PAGE_FETCH) {
    return schemaFailure(requestId, "Catalog summary limit must be an integer in [1,101].");
  }
  if (utf8ByteLength(query.group ?? "") > 256 || utf8ByteLength(query.keyword ?? "") > 2048) {
    return schemaFailure(requestId, "Catalog summary filters exceed their bounded string limits.");
  }
  const tokens = searchTokens(query.keyword);
  if (tokens.length > 16 || tokens.some((token) => utf8ByteLength(token) > 128)) {
    return schemaFailure(requestId, "Catalog search accepts at most 16 keywords of at most 128 UTF-8 bytes each.");
  }
  if (query.localNames.length > 4096 || query.localNames.some((name) => !name || utf8ByteLength(name) > 256)
      || !strictlySortedUnique(query.localNames)) return schemaFailure(requestId, "Catalog local names must be a bounded ordinally sorted set.");
  if (!/^[a-f0-9]{64}$/.test(query.cursorContext)) return schemaFailure(requestId, "Catalog cursor context must be a SHA-256 value.");
  if (query.cursorToken !== undefined && (!query.cursorToken || utf8ByteLength(query.cursorToken) > 8192)) {
    return schemaFailure(requestId, "Catalog continuation token is invalid.");
  }
  return validateGenerationAndReservedNames(requestId, query.expectedGeneration, query.reservedNames);
}

function validateGenerationAndReservedNames(
  requestId: string,
  generation: string | undefined,
  reservedNames: string[],
): (CliResult & { ok: false }) | undefined {
  if (utf8ByteLength(generation ?? "") > 128) return schemaFailure(requestId, "Catalog generation exceeds its bounded string limit.");
  if (reservedNames.length > 4096 || reservedNames.some((name) => !name || utf8ByteLength(name) > 256)) {
    return schemaFailure(requestId, "Catalog reserved names exceed their bounded item or string limits.");
  }
  return undefined;
}

function schemaFailure(requestId: string, message: string): CliResult & { ok: false } {
  return failure(requestId, "E_SCHEMA_INVALID", message, ["Use the bounded private catalog protocol fields."]) as CliResult & { ok: false };
}

function searchTokens(keyword: string | undefined): string[] {
  return (keyword ?? "").split("|").map((token) => token.trim().toLowerCase()).filter(Boolean);
}

function jsonByteLength(value: unknown): number {
  try { return Buffer.byteLength(JSON.stringify(value), "utf8"); }
  catch { return Number.POSITIVE_INFINITY; }
}

/** catalog public string limits全部按wire UTF-8字节计算。 */
function utf8ByteLength(value: string): number {
  return Buffer.byteLength(value, "utf8");
}

function objectRecord(value: unknown): Record<string, unknown> | undefined {
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

function hasOnlyKeys(record: Record<string, unknown>, allowed: string[]): boolean {
  const keys = new Set(allowed);
  return Object.keys(record).every((key) => keys.has(key));
}

function stringField(record: Record<string, unknown> | undefined, key: string): string | undefined {
  const value = record?.[key];
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

function numberField(record: Record<string, unknown> | undefined, key: string): number | undefined {
  const value = record?.[key];
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function compareToolName(left: { name: string }, right: { name: string }): number {
  return compareText(left.name, right.name);
}

export function compareText(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function strictlySortedUnique(values: string[]): boolean {
  return values.every((value, index) => index === 0 || compareText(values[index - 1], value) < 0);
}
