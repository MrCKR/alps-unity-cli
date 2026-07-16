import { access, readFile, readdir, stat } from "node:fs/promises";
import { basename, extname, join, relative, resolve, sep } from "node:path";
import { StructuredToolError } from "../tools/errors.js";

export interface CacheEntry {
  name: string;
  title?: string;
  summary?: string;
  content?: string;
  url?: string;
  kind?: string;
  namespace?: string;
  version?: string;
  path?: string;
  assembly?: string;
  fullName?: string;
  signature?: string;
  returnType?: string;
  parameters?: unknown;
  members?: CacheEntry[];
}

export interface AssemblyEntry {
  name: string;
  path?: string;
  status?: string;
  error?: string;
}

export interface Diagnostic {
  level: "info" | "warning" | "error";
  code: string;
  message: string;
  assembly?: string;
  path?: string;
}

export interface CacheIndex {
  root: string;
  indexPath?: string;
  unityVersion?: string;
  entries: CacheEntry[];
  assemblies?: AssemblyEntry[];
  diagnostics: Diagnostic[];
}

export interface PageOptions {
  pageSize?: number;
  cursor?: string;
}

export interface Page<T> {
  items: T[];
  pageInfo: { pageSize: number; cursor: string; nextCursor?: string; total: number };
}

export class CacheError extends Error {
  constructor(readonly code: string, message: string, readonly nextActions: string[] = []) {
    super(message);
  }
}

const docsIndexCandidates = ["docs-index.json", "unity-docs.json", "docs.json", "index.json"];
const reflectIndexCandidates = ["reflect-index.json", "unity-reflect.json", "reflect.json", "index.json"];

/** 解析文档 cache 目录；只读取显式输入、环境变量和 dataDir。 */
export function docsCacheDir(docsDir: string | undefined, dataDir: string): string {
  return resolve(docsDir ?? process.env.UNITY_CLI_DOCS_DIR ?? join(dataDir, "docs"));
}

/** 解析反射 cache 目录；只读取显式输入、环境变量和 dataDir。 */
export function reflectCacheDir(reflectDir: string | undefined, dataDir: string): string {
  return resolve(reflectDir ?? process.env.UNITY_CLI_REFLECT_DIR ?? join(dataDir, "reflect"));
}

/** 读取文档索引或 Markdown 文件树，返回规范化 cache；副作用仅为文件读取。 */
export async function loadDocsCache(root: string): Promise<CacheIndex> {
  await assertDirectory(root, "DOCS_CACHE_MISSING", "Unity documentation cache was not found.", [
    "Pass --docs-dir <path> or set UNITY_CLI_DOCS_DIR.",
    "Populate the cache with index.json/docs.json or Markdown files before running offline docs queries."
  ]);
  const json = await readFirstJson(root, docsIndexCandidates);
  if (json) return { ...normalizeIndex(json.data, root, "docs"), root, indexPath: json.path };
  const entries = await readMarkdownDocs(root);
  if (entries.length === 0) {
    throw new CacheError("DOCS_CACHE_MISSING", "Unity documentation cache has no readable index or Markdown files.", [
      "Add docs-index.json, unity-docs.json, docs.json, index.json, or *.md files.",
      "Pass --docs-dir <path> pointing at a populated cache."
    ]);
  }
  return { root, entries, diagnostics: [] };
}

/** 读取并规范化反射索引；副作用仅为文件读取。 */
export async function loadReflectCache(root: string): Promise<CacheIndex> {
  await assertDirectory(root, "REFLECT_CACHE_MISSING", "Unity reflection cache was not found.", [
    "Pass --reflect-dir <path> or set UNITY_CLI_REFLECT_DIR.",
    "Generate a reflection index from Unity batchmode or a live bridge context."
  ]);
  const json = await readFirstJson(root, reflectIndexCandidates);
  if (!json) {
    throw new CacheError("REFLECT_CACHE_MISSING", "Unity reflection cache has no readable index file.", [
      "Add reflect-index.json, unity-reflect.json, reflect.json, or index.json.",
      "Regenerate the reflection cache from Unity batchmode or a live bridge context."
    ]);
  }
  return { ...normalizeIndex(json.data, root, "reflect"), root, indexPath: json.path };
}

/** 按请求版本筛选条目；无匹配时抛出带稳定 code 的 cache 错误。 */
export function filterVersion(cache: CacheIndex, requested: string | undefined, code: string): CacheIndex {
  if (!requested) return cache;
  const withVersions = cache.entries.filter((entry) => entry.version ?? cache.unityVersion);
  const matches = cache.entries.filter((entry) => versionMatches(entry.version ?? cache.unityVersion, requested));
  if (withVersions.length > 0 && matches.length === 0) {
    const available = [...new Set(withVersions.map((entry) => entry.version ?? cache.unityVersion).filter(Boolean))];
    throw new CacheError(code, `Unity version was not found in cache: ${requested}.`, [`Available versions: ${available.join(", ") || "unknown"}.`]);
  }
  return { ...cache, entries: matches.length > 0 ? matches : cache.entries, unityVersion: requested };
}

/** 延迟读取文档正文；读取失败时保留索引条目，不产生写入。 */
export async function hydrateContent(entry: CacheEntry, root: string): Promise<CacheEntry> {
  if (entry.content || !entry.path) return entry;
  try {
    return { ...entry, content: await readFile(resolve(root, entry.path), "utf8") };
  } catch {
    return entry;
  }
}

/** 按 cursor 和 pageSize 截取结果并生成 continuation 信息。 */
export function paginate<T>(items: T[], options: PageOptions): Page<T> {
  const pageSize = Math.max(1, Math.min(options.pageSize ?? 20, 500));
  const cursor = Math.max(0, Number.parseInt(options.cursor ?? "0", 10) || 0);
  const pageItems = items.slice(cursor, cursor + pageSize);
  const next = cursor + pageItems.length;
  return { items: pageItems, pageInfo: { pageSize, cursor: String(cursor), ...(next < items.length ? { nextCursor: String(next) } : {}), total: items.length } };
}

/** 将 cache 异常转换为工具层结构化错误，保留 code 和恢复动作。 */
export function cacheError(error: unknown): StructuredToolError {
  if (error instanceof StructuredToolError) return error;
  if (error instanceof CacheError) return new StructuredToolError(error.code, error.message, error.nextActions);
  return new StructuredToolError("CACHE_READ_FAILED", error instanceof Error ? error.message : String(error));
}

/** 返回适合公开输出的文档字段，可按需携带正文。 */
export function publicDoc(entry: CacheEntry, includeContent: boolean): Record<string, unknown> {
  return compact({ name: entry.name, title: entry.title, summary: entry.summary, kind: entry.kind, namespace: entry.namespace, version: entry.version, url: entry.url, path: entry.path, content: includeContent ? entry.content : undefined });
}

/** 返回适合公开输出的类型字段，可按需展开成员。 */
export function publicType(entry: CacheEntry, includeMembers: boolean): Record<string, unknown> {
  return compact({ name: entry.name, fullName: entry.fullName ?? entry.name, namespace: entry.namespace, kind: entry.kind, assembly: entry.assembly, signature: entry.signature, summary: entry.summary, members: includeMembers ? (entry.members ?? []).map((member) => publicMember(member, entry)) : undefined });
}

/** 返回适合公开输出的成员字段，并补充声明类型和程序集。 */
export function publicMember(member: CacheEntry, type: CacheEntry): Record<string, unknown> {
  return compact({ name: member.name, kind: member.kind, signature: member.signature, returnType: member.returnType, parameters: member.parameters, summary: member.summary, assembly: member.assembly ?? type.assembly, declaringType: type.fullName ?? type.name });
}

/** 返回 cache 的公开来源信息。 */
export function cacheSource(cache: CacheIndex): Record<string, unknown> {
  return compact({ cacheDir: cache.root, indexPath: cache.indexPath });
}

/** 返回 cache 解析出的 Unity 版本。 */
export function unityVersion(cache: CacheIndex): Record<string, unknown> {
  return compact({ resolved: cache.unityVersion });
}

/** 只返回程序集相关诊断，避免混入其他 cache 警告。 */
export function assemblyDiagnostics(cache: CacheIndex): Diagnostic[] {
  return cache.diagnostics.filter((diagnostic) => diagnostic.code.includes("ASSEMBLY"));
}

/** 精确匹配类型短名、全名或标题，匹配不区分大小写。 */
export function typeMatches(entries: CacheEntry[], query: string): CacheEntry[] {
  const target = normalize(query);
  return entries.filter((entry) => [entry.fullName, entry.name, entry.title].some((value) => normalize(value) === target));
}

/** 精确匹配文档名、全名或标题，匹配不区分大小写。 */
export function entryMatchesName(entry: CacheEntry, query: string): boolean {
  const target = normalize(query);
  return [entry.name, entry.fullName, entry.title].some((value) => normalize(value) === target);
}

/** 对 cache 条目进行相关性评分并稳定排序。 */
export function scoreEntries(entries: CacheEntry[], query: string): Array<{ entry: CacheEntry; score: number }> {
  return entries.map((entry) => ({ entry, score: scoreEntry(entry, query) })).sort((left, right) => right.score - left.score || left.entry.name.localeCompare(right.entry.name));
}

/** 计算单条 cache 记录的字段加权相关性。 */
export function scoreEntry(entry: CacheEntry, query: string): number {
  return scoreText(entry.name, query) * 4 + scoreText(entry.fullName, query) * 4 + scoreText(entry.title, query) * 3 + scoreText(entry.summary, query) * 2 + scoreText(entry.signature, query) + scoreText(entry.content, query);
}

/** 计算文本与查询的精确、前缀或包含匹配分值。 */
export function scoreText(value: string | undefined, query: string): number {
  if (!value) return 0;
  const text = normalize(value);
  const target = normalize(query);
  if (text === target) return 4;
  if (text.startsWith(target)) return 3;
  return text.includes(target) ? 1 : 0;
}

/** 将文档条目渲染为纯文本输出。 */
export function docText(doc: CacheEntry): string {
  return [doc.title ?? doc.name, doc.summary, doc.content, doc.url].filter(Boolean).join("\n\n");
}

/** 将类型及其成员摘要渲染为纯文本输出。 */
export function typeText(type: Record<string, unknown>): string {
  const members = Array.isArray(type.members) ? type.members as Array<Record<string, unknown>> : [];
  return [`${type.fullName ?? type.name}${type.assembly ? ` (${type.assembly})` : ""}`, type.signature, type.summary, ...members.map((member) => `- ${member.signature ?? member.name}`)].filter(Boolean).join("\n");
}

/** 将单个成员详情渲染为纯文本输出。 */
export function memberText(type: Record<string, unknown>, member: Record<string, unknown>): string {
  return [`${type.fullName ?? type.name}.${member.name}`, member.signature, member.summary, member.assembly ? `Assembly: ${member.assembly}` : undefined].filter(Boolean).join("\n");
}

/** 将反射搜索项渲染为单行文本。 */
export function reflectSearchLine(item: unknown): string {
  const object = asObject(item);
  if (object.kind === "member") {
    const member = asObject(object.member);
    return `member ${member.declaringType}.${member.name} - ${member.signature ?? ""}`.trim();
  }
  return `type ${object.fullName ?? object.name} - ${object.assembly ?? ""}`.trim();
}

/** 校验路径存在且为目录；失败时统一为指定 cache 错误。 */
async function assertDirectory(path: string, code: string, message: string, nextActions: string[]): Promise<void> {
  try {
    if (!(await stat(path)).isDirectory()) throw new CacheError(code, message, nextActions);
  } catch (error) {
    if (error instanceof CacheError) throw error;
    throw new CacheError(code, message, nextActions);
  }
}

/** 按候选顺序读取首个合法 JSON 索引；不存在或解析失败时继续尝试。 */
async function readFirstJson(root: string, names: string[]): Promise<{ path: string; data: unknown } | undefined> {
  for (const name of names) {
    const path = join(root, name);
    try {
      await access(path);
      return { path, data: JSON.parse(await readFile(path, "utf8")) as unknown };
    } catch {
      // 候选文件缺失或损坏时继续检查下一名称。
    }
  }
  return undefined;
}

/** 将外部索引结构规范化为 docs/reflect 共用 cache 模型。 */
function normalizeIndex(data: unknown, root: string, kind: "docs" | "reflect"): CacheIndex {
  const object = asObject(data);
  const entries = rawEntries(data, kind).map(normalizeEntry);
  const assemblies = (rawArray(object.assemblies) ?? []).map(normalizeAssembly);
  const diagnostics = (rawArray(object.diagnostics) ?? []).map(normalizeDiagnostic).filter((item): item is Diagnostic => item !== undefined);
  for (const assembly of assemblies) {
    if (assembly.status === "failed" || assembly.status === "missing" || assembly.error) {
      diagnostics.push({ level: "error", code: "REFLECT_ASSEMBLY_LOAD_FAILED", message: assembly.error ?? `Assembly could not be loaded: ${assembly.name}.`, assembly: assembly.name, path: assembly.path });
    }
  }
  return { root, unityVersion: stringField(object, "unityVersion") ?? stringField(object, "version"), entries, assemblies, diagnostics };
}

/** 从数组或名称映射中提取原始条目列表。 */
function rawEntries(data: unknown, kind: "docs" | "reflect"): unknown[] {
  if (Array.isArray(data)) return data;
  const object = asObject(data);
  const direct = rawArray(object.entries) ?? rawArray(object.items) ?? rawArray(kind === "docs" ? object.docs : object.types) ?? rawArray(kind === "docs" ? object.apis : undefined);
  if (direct) return direct;
  const map = kind === "docs" ? asObject(object.docs ?? object.apis) : asObject(object.types);
  return Object.entries(map).map(([name, value]) => ({ name, ...asObject(value) }));
}

/** 将一个外部条目规范化并递归处理成员。 */
function normalizeEntry(value: unknown): CacheEntry {
  const object = asObject(value);
  const fullName = stringField(object, "fullName") ?? stringField(object, "qualifiedName");
  return {
    name: stringField(object, "name") ?? stringField(object, "api") ?? fullName ?? stringField(object, "title") ?? "unknown",
    title: stringField(object, "title"), summary: stringField(object, "summary") ?? stringField(object, "description"),
    content: stringField(object, "content") ?? stringField(object, "markdown") ?? stringField(object, "text"),
    url: stringField(object, "url") ?? stringField(object, "href"), kind: stringField(object, "kind") ?? stringField(object, "type"),
    namespace: stringField(object, "namespace"), version: stringField(object, "unityVersion") ?? stringField(object, "version"),
    path: stringField(object, "path") ?? stringField(object, "file") ?? stringField(object, "contentPath"),
    assembly: stringField(object, "assembly") ?? stringField(object, "assemblyName"), fullName,
    signature: stringField(object, "signature"), returnType: stringField(object, "returnType"), parameters: object.parameters,
    members: (rawArray(object.members) ?? []).map(normalizeEntry)
  };
}

/** 将程序集状态规范化为公开诊断使用的结构。 */
function normalizeAssembly(value: unknown): AssemblyEntry {
  const object = asObject(value);
  return { name: stringField(object, "name") ?? stringField(object, "assembly") ?? basename(stringField(object, "path") ?? "unknown"), path: stringField(object, "path"), status: stringField(object, "status"), error: stringField(object, "error") ?? stringField(object, "message") };
}

/** 校验并规范化外部诊断；缺少 code/message 的记录被忽略。 */
function normalizeDiagnostic(value: unknown): Diagnostic | undefined {
  const object = asObject(value);
  const code = stringField(object, "code");
  const message = stringField(object, "message");
  if (!code || !message) return undefined;
  const level = stringField(object, "level");
  return { level: level === "info" || level === "warning" || level === "error" ? level : "warning", code, message, assembly: stringField(object, "assembly"), path: stringField(object, "path") };
}

/** 遍历文本文件并生成文档 cache 条目；副作用仅为目录和文件读取。 */
async function readMarkdownDocs(root: string): Promise<CacheEntry[]> {
  const files = await walk(root);
  const docs: CacheEntry[] = [];
  for (const file of files.filter((path) => [".md", ".markdown", ".txt"].includes(extname(path).toLowerCase()))) {
    const content = await readFile(file, "utf8");
    const title = content.split(/\r?\n/).find((line) => line.startsWith("# "))?.replace(/^#\s+/, "").trim();
    docs.push({ name: basename(file, extname(file)), title, summary: firstParagraph(content), content, path: relativeCachePath(root, file) });
  }
  return docs;
}

/** 递归列出目录内文件；副作用仅为目录读取。 */
async function walk(root: string): Promise<string[]> {
  const entries = await readdir(root, { withFileTypes: true });
  return (await Promise.all(entries.map(async (entry) => entry.isDirectory() ? walk(join(root, entry.name)) : [join(root, entry.name)]))).flat();
}

/** 判断 cache 版本与请求版本是否互为兼容前缀。 */
function versionMatches(version: string | undefined, requested: string): boolean {
  return !!version && (version === requested || version.startsWith(`${requested}.`) || requested.startsWith(`${version}.`));
}

/** 提取 Markdown 首个非空正文段落作为摘要。 */
function firstParagraph(content: string): string | undefined {
  return content.split(/\r?\n\s*\r?\n/).map((part) => part.replace(/^#.*$/m, "").trim()).find(Boolean);
}

/** 将文件路径转换为跨平台 cache 相对路径。 */
function relativeCachePath(root: string, file: string): string {
  return relative(root, file).split(sep).join("/");
}

/** 规范化可选字符串以供不区分大小写比较。 */
function normalize(value: string | undefined): string {
  return (value ?? "").trim().toLowerCase();
}

/** 从未知对象中读取非空字符串字段。 */
function stringField(object: Record<string, unknown>, key: string): string | undefined {
  const value = object[key];
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

/** 将未知值收窄为数组。 */
function rawArray(value: unknown): unknown[] | undefined {
  return Array.isArray(value) ? value : undefined;
}

/** 将未知值收窄为普通对象，其他输入返回空对象。 */
function asObject(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : {};
}

/** 删除公开输出中的 undefined 字段。 */
function compact<T extends Record<string, unknown>>(value: T): Record<string, unknown> {
  return Object.fromEntries(Object.entries(value).filter(([, entry]) => entry !== undefined));
}
