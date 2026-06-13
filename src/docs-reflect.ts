import { access, readFile, readdir, stat } from "node:fs/promises";
import { basename, extname, join, relative, resolve, sep } from "node:path";
import { getNumber, getString } from "./args.js";
import { failure, success } from "./output.js";
import type { CliResult } from "./types.js";

interface CacheEntry {
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
  raw?: Record<string, unknown>;
}

interface CacheIndex {
  root: string;
  indexPath?: string;
  unityVersion?: string;
  entries: CacheEntry[];
  assemblies?: AssemblyEntry[];
  diagnostics: Diagnostic[];
}

interface AssemblyEntry {
  name: string;
  path?: string;
  status?: string;
  error?: string;
}

interface Diagnostic {
  level: "info" | "warning" | "error";
  code: string;
  message: string;
  assembly?: string;
  path?: string;
}

interface Page<T> {
  items: T[];
  pageInfo: {
    pageSize: number;
    cursor: string;
    nextCursor?: string;
    total: number;
  };
}

class CacheError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly nextActions: string[] = []
  ) {
    super(message);
  }
}

const docsIndexCandidates = ["docs-index.json", "unity-docs.json", "docs.json", "index.json"];
const reflectIndexCandidates = ["reflect-index.json", "unity-reflect.json", "reflect.json", "index.json"];

export async function handleDocsCommand(
  command: string | undefined,
  args: string[],
  flags: Record<string, string | boolean>,
  dataDir: string,
  requestId: string
): Promise<CliResult> {
  const cacheDir = docsCacheDir(flags, dataDir);
  try {
    const cache = await loadDocsCache(cacheDir);
    const versioned = filterVersion(cache, getString(flags, "unity-version") ?? getString(flags, "version"), "DOCS_VERSION_NOT_FOUND");
    if (command === "get") {
      return await docsGet(args[0] ?? getString(flags, "api"), flags, requestId, versioned);
    }
    if (command === "search") {
      return docsSearch(args[0] ?? getString(flags, "query"), flags, requestId, versioned);
    }
    return failure(requestId, "UNKNOWN_DOCS_COMMAND", "Expected docs get <api> or docs search <query>.");
  } catch (error) {
    return cacheFailure(requestId, error);
  }
}

export async function handleReflectCommand(
  command: string | undefined,
  args: string[],
  flags: Record<string, string | boolean>,
  dataDir: string,
  requestId: string
): Promise<CliResult> {
  const cacheDir = reflectCacheDir(flags, dataDir);
  try {
    const cache = await loadReflectCache(cacheDir);
    const versioned = filterVersion(cache, getString(flags, "unity-version") ?? getString(flags, "version"), "REFLECT_VERSION_NOT_FOUND");
    if (versioned.entries.length === 0 && assemblyDiagnostics(versioned).some((diagnostic) => diagnostic.level === "error")) {
      return failure(requestId, "REFLECT_ASSEMBLY_LOAD_FAILED", "Reflection cache contains only failed assembly loads.", [
        "Regenerate the reflection cache from Unity batchmode or a live bridge context.",
        "Check assembly paths and Unity compilation errors."
      ]);
    }
    if (command === "type") {
      return reflectType(args[0] ?? getString(flags, "name"), flags, requestId, versioned);
    }
    if (command === "member") {
      return reflectMember(args[0] ?? getString(flags, "member"), flags, requestId, versioned);
    }
    if (command === "search") {
      return reflectSearch(args[0] ?? getString(flags, "query"), flags, requestId, versioned);
    }
    return failure(requestId, "UNKNOWN_REFLECT_COMMAND", "Expected reflect type <name>, reflect member <type.member> or reflect search <query>.");
  } catch (error) {
    return cacheFailure(requestId, error);
  }
}

async function docsGet(api: string | undefined, flags: Record<string, string | boolean>, requestId: string, cache: CacheIndex): Promise<CliResult> {
  if (!api) {
    return failure(requestId, "DOCS_API_REQUIRED", "Pass an API name: unity_cli docs get <api>.");
  }
  const matches = cache.entries.filter((entry) => entryMatchesName(entry, api));
  if (matches.length === 0) {
    return failure(requestId, "DOCS_API_NOT_FOUND", `Unity API documentation was not found in the offline cache: ${api}.`, [
      "Run unity_cli docs search <query> to inspect cached APIs.",
      "Pass --docs-dir <path> or set ALPS_UNITY_DOCS_DIR to a populated cache."
    ]);
  }
  if (matches.length > 1) {
    return failure(requestId, "DOCS_API_AMBIGUOUS", `Multiple cached Unity APIs matched: ${api}.`, [
      `Use one of: ${matches.slice(0, 5).map((entry) => entry.name).join(", ")}.`
    ]);
  }

  const doc = await hydrateContent(matches[0], cache.root);
  const format = outputFormat(flags);
  const data = {
    command: "docs.get",
    mode: "offline",
    requiresUnity: false,
    source: cacheSource(cache),
    unityVersion: unityVersion(cache),
    api,
    format,
    doc: publicDoc(doc, format === "json"),
    ...(format === "text" ? { text: docText(doc) } : {})
  };
  return success(requestId, data);
}

function docsSearch(query: string | undefined, flags: Record<string, string | boolean>, requestId: string, cache: CacheIndex): CliResult {
  if (!query) {
    return failure(requestId, "DOCS_QUERY_REQUIRED", "Pass a search query: unity_cli docs search <query>.");
  }
  const scored = scoreEntries(cache.entries, query)
    .filter((entry) => entry.score > 0)
    .map(({ entry }) => publicDoc(entry, false));
  const page = paginate(scored, flags);
  const format = outputFormat(flags);
  return success(requestId, {
    command: "docs.search",
    mode: "offline",
    requiresUnity: false,
    source: cacheSource(cache),
    unityVersion: unityVersion(cache),
    query,
    format,
    ...page,
    ...(format === "text" ? { text: page.items.map((entry) => `${entry.name} - ${entry.title ?? entry.summary ?? ""}`.trim()).join("\n") } : {})
  });
}

function reflectType(name: string | undefined, flags: Record<string, string | boolean>, requestId: string, cache: CacheIndex): CliResult {
  if (!name) {
    return failure(requestId, "REFLECT_TYPE_REQUIRED", "Pass a type name: unity_cli reflect type <name>.");
  }
  const matches = typeMatches(cache.entries, name);
  if (matches.length === 0) {
    return failure(requestId, "REFLECT_TYPE_NOT_FOUND", `Type was not found in the offline reflection cache: ${name}.`, [
      "Run unity_cli reflect search <query> to inspect cached types and members.",
      "Regenerate the reflection cache from Unity if the type comes from project assemblies."
    ]);
  }
  if (matches.length > 1) {
    return failure(requestId, "REFLECT_TYPE_AMBIGUOUS", `Multiple types matched: ${name}.`, [
      `Use a fully-qualified type name: ${matches.slice(0, 5).map((entry) => entry.fullName ?? entry.name).join(", ")}.`
    ]);
  }
  const type = publicType(matches[0], true);
  const format = outputFormat(flags);
  return success(requestId, {
    command: "reflect.type",
    mode: "offline",
    requiresUnity: false,
    source: cacheSource(cache),
    unityVersion: unityVersion(cache),
    assemblies: publicAssemblies(cache.assemblies),
    diagnostics: assemblyDiagnostics(cache),
    name,
    format,
    type,
    ...(format === "text" ? { text: typeText(type) } : {})
  });
}

function reflectMember(target: string | undefined, flags: Record<string, string | boolean>, requestId: string, cache: CacheIndex): CliResult {
  if (!target || !target.includes(".")) {
    return failure(requestId, "REFLECT_MEMBER_REQUIRED", "Pass a member target: unity_cli reflect member <type.member>.");
  }
  const lastDot = target.lastIndexOf(".");
  const typeName = target.slice(0, lastDot);
  const memberName = target.slice(lastDot + 1);
  const types = typeMatches(cache.entries, typeName);
  if (types.length === 0) {
    return failure(requestId, "REFLECT_TYPE_NOT_FOUND", `Type was not found in the offline reflection cache: ${typeName}.`, ["Run unity_cli reflect search <query> to inspect cached types and members."]);
  }

  const memberMatches = types.flatMap((type) => (type.members ?? [])
    .filter((member) => normalize(member.name) === normalize(memberName))
    .map((member) => ({ type, member })));
  if (memberMatches.length === 0) {
    return failure(requestId, "REFLECT_MEMBER_NOT_FOUND", `Member was not found in the offline reflection cache: ${target}.`, ["Run unity_cli reflect type <type> to inspect cached members."]);
  }
  if (memberMatches.length > 1) {
    return failure(requestId, "REFLECT_MEMBER_AMBIGUOUS", `Multiple members matched: ${target}.`, [
      "Use a fully-qualified type name or inspect overload signatures with reflect type."
    ]);
  }

  const match = memberMatches[0];
  const type = publicType(match.type, false);
  const member = publicMember(match.member, match.type);
  const format = outputFormat(flags);
  return success(requestId, {
    command: "reflect.member",
    mode: "offline",
    requiresUnity: false,
    source: cacheSource(cache),
    unityVersion: unityVersion(cache),
    assemblies: publicAssemblies(cache.assemblies),
    diagnostics: assemblyDiagnostics(cache),
    target,
    format,
    type,
    member,
    ...(format === "text" ? { text: memberText(type, member) } : {})
  });
}

function reflectSearch(query: string | undefined, flags: Record<string, string | boolean>, requestId: string, cache: CacheIndex): CliResult {
  if (!query) {
    return failure(requestId, "REFLECT_QUERY_REQUIRED", "Pass a search query: unity_cli reflect search <query>.");
  }
  const typeResults = scoreEntries(cache.entries, query)
    .filter((entry) => entry.score > 0)
    .map(({ entry, score }) => ({ score, item: { ...publicType(entry, false), kind: "type" } }));
  const memberResults = cache.entries.flatMap((type) => (type.members ?? [])
    .map((member) => ({ type, member, score: scoreEntry(member, query) + scoreText(type.fullName ?? type.name, query) }))
    .filter((entry) => entry.score > 0)
    .map(({ type, member, score }) => ({ score, item: { kind: "member", type: publicType(type, false), member: publicMember(member, type) } })));
  const results = [...typeResults, ...memberResults]
    .sort((left, right) => right.score - left.score)
    .map((result) => result.item);
  const page = paginate(results, flags);
  const format = outputFormat(flags);
  return success(requestId, {
    command: "reflect.search",
    mode: "offline",
    requiresUnity: false,
    source: cacheSource(cache),
    unityVersion: unityVersion(cache),
    assemblies: publicAssemblies(cache.assemblies),
    diagnostics: assemblyDiagnostics(cache),
    query,
    format,
    ...page,
    ...(format === "text" ? { text: page.items.map(reflectSearchLine).join("\n") } : {})
  });
}

function docsCacheDir(flags: Record<string, string | boolean>, dataDir: string): string {
  return resolve(getString(flags, "docs-dir") ?? getString(flags, "cache") ?? process.env.ALPS_UNITY_DOCS_DIR ?? join(dataDir, "docs"));
}

function reflectCacheDir(flags: Record<string, string | boolean>, dataDir: string): string {
  return resolve(getString(flags, "reflect-dir") ?? getString(flags, "cache") ?? process.env.ALPS_UNITY_REFLECT_DIR ?? join(dataDir, "reflect"));
}

async function loadDocsCache(root: string): Promise<CacheIndex> {
  await assertDirectory(root, "DOCS_CACHE_MISSING", "Unity documentation cache was not found.", [
    "Pass --docs-dir <path> or set ALPS_UNITY_DOCS_DIR.",
    "Populate the cache with index.json/docs.json or Markdown files before running offline docs queries."
  ]);
  const json = await readFirstJson(root, docsIndexCandidates);
  if (json) {
    const parsed = normalizeIndex(json.data, root, "docs");
    return { ...parsed, root, indexPath: json.path };
  }
  const markdownEntries = await readMarkdownDocs(root);
  if (markdownEntries.length === 0) {
    throw new CacheError("DOCS_CACHE_MISSING", "Unity documentation cache has no readable index or Markdown files.", [
      "Add docs-index.json, unity-docs.json, docs.json, index.json, or *.md files.",
      "Pass --docs-dir <path> pointing at a populated cache."
    ]);
  }
  return { root, entries: markdownEntries, diagnostics: [] };
}

async function loadReflectCache(root: string): Promise<CacheIndex> {
  await assertDirectory(root, "REFLECT_CACHE_MISSING", "Unity reflection cache was not found.", [
    "Pass --reflect-dir <path> or set ALPS_UNITY_REFLECT_DIR.",
    "Generate a reflection index from Unity batchmode or a live bridge context."
  ]);
  const json = await readFirstJson(root, reflectIndexCandidates);
  if (!json) {
    throw new CacheError("REFLECT_CACHE_MISSING", "Unity reflection cache has no readable index file.", [
      "Add reflect-index.json, unity-reflect.json, reflect.json, or index.json.",
      "Regenerate the reflection cache from Unity batchmode or a live bridge context."
    ]);
  }
  const parsed = normalizeIndex(json.data, root, "reflect");
  return { ...parsed, root, indexPath: json.path };
}

async function assertDirectory(path: string, code: string, message: string, nextActions: string[]): Promise<void> {
  try {
    const stats = await stat(path);
    if (!stats.isDirectory()) {
      throw new CacheError(code, message, nextActions);
    }
  } catch (error) {
    if (error instanceof CacheError) {
      throw error;
    }
    throw new CacheError(code, message, nextActions);
  }
}

async function readFirstJson(root: string, names: string[]): Promise<{ path: string; data: unknown } | undefined> {
  for (const name of names) {
    const path = join(root, name);
    try {
      await access(path);
      return { path, data: JSON.parse(await readFile(path, "utf8")) as unknown };
    } catch {
    }
  }
  return undefined;
}

function normalizeIndex(data: unknown, root: string, kind: "docs" | "reflect"): CacheIndex {
  const object = asObject(data);
  const entries = rawEntries(data, kind).map((entry) => normalizeEntry(entry));
  const assemblies = (rawArray(object.assemblies) ?? []).map((assembly) => normalizeAssembly(assembly));
  const diagnostics = (rawArray(object.diagnostics) ?? []).map(normalizeDiagnostic).filter((diagnostic): diagnostic is Diagnostic => diagnostic !== undefined);
  for (const assembly of assemblies) {
    if (assembly.status === "failed" || assembly.status === "missing" || assembly.error) {
      diagnostics.push({ level: "error", code: "REFLECT_ASSEMBLY_LOAD_FAILED", message: assembly.error ?? `Assembly could not be loaded: ${assembly.name}.`, assembly: assembly.name, path: assembly.path });
    }
  }
  return {
    root,
    unityVersion: stringField(object, "unityVersion") ?? stringField(object, "version"),
    entries,
    assemblies,
    diagnostics
  };
}

function rawEntries(data: unknown, kind: "docs" | "reflect"): unknown[] {
  if (Array.isArray(data)) {
    return data;
  }
  const object = asObject(data);
  const direct = rawArray(object.entries) ?? rawArray(object.items) ?? rawArray(kind === "docs" ? object.docs : object.types) ?? rawArray(kind === "docs" ? object.apis : undefined);
  if (direct) {
    return direct;
  }
  const map = kind === "docs" ? asObject(object.docs ?? object.apis) : asObject(object.types);
  if (Object.keys(map).length > 0) {
    return Object.entries(map).map(([name, value]) => ({ name, ...(asObject(value)) }));
  }
  return [];
}

function normalizeEntry(value: unknown): CacheEntry {
  const object = asObject(value);
  const fullName = stringField(object, "fullName") ?? stringField(object, "qualifiedName");
  const name = stringField(object, "name") ?? stringField(object, "api") ?? fullName ?? stringField(object, "title") ?? "unknown";
  return {
    name,
    title: stringField(object, "title"),
    summary: stringField(object, "summary") ?? stringField(object, "description"),
    content: stringField(object, "content") ?? stringField(object, "markdown") ?? stringField(object, "text"),
    url: stringField(object, "url") ?? stringField(object, "href"),
    kind: stringField(object, "kind") ?? stringField(object, "type"),
    namespace: stringField(object, "namespace"),
    version: stringField(object, "unityVersion") ?? stringField(object, "version"),
    path: stringField(object, "path") ?? stringField(object, "file") ?? stringField(object, "contentPath"),
    assembly: stringField(object, "assembly") ?? stringField(object, "assemblyName"),
    fullName,
    signature: stringField(object, "signature"),
    returnType: stringField(object, "returnType"),
    parameters: object.parameters,
    members: (rawArray(object.members) ?? []).map((member) => normalizeEntry(member)),
    raw: object
  };
}

function normalizeAssembly(value: unknown): AssemblyEntry {
  const object = asObject(value);
  return {
    name: stringField(object, "name") ?? stringField(object, "assembly") ?? basename(stringField(object, "path") ?? "unknown"),
    path: stringField(object, "path"),
    status: stringField(object, "status"),
    error: stringField(object, "error") ?? stringField(object, "message")
  };
}

function normalizeDiagnostic(value: unknown): Diagnostic | undefined {
  const object = asObject(value);
  const code = stringField(object, "code");
  const message = stringField(object, "message");
  if (!code || !message) {
    return undefined;
  }
  const level = stringField(object, "level");
  return {
    level: level === "info" || level === "warning" || level === "error" ? level : "warning",
    code,
    message,
    assembly: stringField(object, "assembly"),
    path: stringField(object, "path")
  };
}

async function readMarkdownDocs(root: string): Promise<CacheEntry[]> {
  const files = await walk(root);
  const docs: CacheEntry[] = [];
  for (const file of files.filter((path) => [".md", ".markdown", ".txt"].includes(extname(path).toLowerCase()))) {
    const content = await readFile(file, "utf8");
    const title = content.split(/\r?\n/).find((line) => line.startsWith("# "))?.replace(/^#\s+/, "").trim();
    const name = basename(file, extname(file));
    docs.push({ name, title, summary: firstParagraph(content), content, path: relativeCachePath(root, file) });
  }
  return docs;
}

async function walk(root: string): Promise<string[]> {
  const entries = await readdir(root, { withFileTypes: true });
  const paths = await Promise.all(entries.map(async (entry) => {
    const path = join(root, entry.name);
    if (entry.isDirectory()) {
      return walk(path);
    }
    return [path];
  }));
  return paths.flat();
}

function filterVersion(cache: CacheIndex, requested: string | undefined, code: string): CacheIndex {
  if (!requested) {
    return cache;
  }
  const withVersions = cache.entries.filter((entry) => entry.version ?? cache.unityVersion);
  const matches = cache.entries.filter((entry) => versionMatches(entry.version ?? cache.unityVersion, requested));
  if (withVersions.length > 0 && matches.length === 0) {
    const available = [...new Set(withVersions.map((entry) => entry.version ?? cache.unityVersion).filter(Boolean))];
    throw new CacheError(code, `Unity version was not found in cache: ${requested}.`, [`Available versions: ${available.join(", ") || "unknown"}.`]);
  }
  return { ...cache, entries: matches.length > 0 ? matches : cache.entries, unityVersion: requested };
}

function versionMatches(version: string | undefined, requested: string): boolean {
  return !!version && (version === requested || version.startsWith(`${requested}.`) || requested.startsWith(`${version}.`));
}

async function hydrateContent(entry: CacheEntry, root: string): Promise<CacheEntry> {
  if (entry.content || !entry.path) {
    return entry;
  }
  try {
    return { ...entry, content: await readFile(resolve(root, entry.path), "utf8") };
  } catch {
    return entry;
  }
}

function paginate<T>(items: T[], flags: Record<string, string | boolean>): Page<T> {
  const pageSize = Math.max(1, Math.min(getNumber(flags, "page-size") ?? getNumber(flags, "limit") ?? 20, 500));
  const cursor = Math.max(0, Number.parseInt(getString(flags, "cursor") ?? "0", 10) || 0);
  const pageItems = items.slice(cursor, cursor + pageSize);
  const next = cursor + pageItems.length;
  return {
    items: pageItems,
    pageInfo: {
      pageSize,
      cursor: String(cursor),
      ...(next < items.length ? { nextCursor: String(next) } : {}),
      total: items.length
    }
  };
}

function publicDoc(entry: CacheEntry, includeContent: boolean): Record<string, unknown> {
  return compact({
    name: entry.name,
    title: entry.title,
    summary: entry.summary,
    kind: entry.kind,
    namespace: entry.namespace,
    version: entry.version,
    url: entry.url,
    path: entry.path,
    content: includeContent ? entry.content : undefined
  });
}

function publicType(entry: CacheEntry, includeMembers: boolean): Record<string, unknown> {
  return compact({
    name: entry.name,
    fullName: entry.fullName ?? entry.name,
    namespace: entry.namespace,
    kind: entry.kind,
    assembly: entry.assembly,
    signature: entry.signature,
    summary: entry.summary,
    members: includeMembers ? (entry.members ?? []).map((member) => publicMember(member, entry)) : undefined
  });
}

function publicMember(member: CacheEntry, type: CacheEntry): Record<string, unknown> {
  return compact({
    name: member.name,
    kind: member.kind,
    signature: member.signature,
    returnType: member.returnType,
    parameters: member.parameters,
    summary: member.summary,
    assembly: member.assembly ?? type.assembly,
    declaringType: type.fullName ?? type.name
  });
}

function publicAssemblies(assemblies: AssemblyEntry[] | undefined): AssemblyEntry[] {
  return assemblies ?? [];
}

function assemblyDiagnostics(cache: CacheIndex): Diagnostic[] {
  return cache.diagnostics.filter((diagnostic) => diagnostic.code.includes("ASSEMBLY"));
}

function typeMatches(entries: CacheEntry[], query: string): CacheEntry[] {
  const normalized = normalize(query);
  return entries.filter((entry) => [entry.fullName, entry.name, entry.title].some((value) => normalize(value) === normalized));
}

function entryMatchesName(entry: CacheEntry, query: string): boolean {
  const normalized = normalize(query);
  return [entry.name, entry.fullName, entry.title].some((value) => normalize(value) === normalized);
}

function scoreEntries(entries: CacheEntry[], query: string): Array<{ entry: CacheEntry; score: number }> {
  return entries
    .map((entry) => ({ entry, score: scoreEntry(entry, query) }))
    .sort((left, right) => right.score - left.score || left.entry.name.localeCompare(right.entry.name));
}

function scoreEntry(entry: CacheEntry, query: string): number {
  return scoreText(entry.name, query) * 4 + scoreText(entry.fullName, query) * 4 + scoreText(entry.title, query) * 3 + scoreText(entry.summary, query) * 2 + scoreText(entry.signature, query) + scoreText(entry.content, query);
}

function scoreText(value: string | undefined, query: string): number {
  if (!value) {
    return 0;
  }
  const text = normalize(value);
  const needle = normalize(query);
  if (text === needle) {
    return 4;
  }
  if (text.startsWith(needle)) {
    return 3;
  }
  return text.includes(needle) ? 1 : 0;
}

function outputFormat(flags: Record<string, string | boolean>): "json" | "text" {
  return getString(flags, "format") === "text" ? "text" : "json";
}

function cacheSource(cache: CacheIndex): Record<string, unknown> {
  return compact({ cacheDir: cache.root, indexPath: cache.indexPath });
}

function unityVersion(cache: CacheIndex): Record<string, unknown> {
  return compact({ resolved: cache.unityVersion });
}

function docText(doc: CacheEntry): string {
  return [doc.title ?? doc.name, doc.summary, doc.content, doc.url].filter(Boolean).join("\n\n");
}

function typeText(type: Record<string, unknown>): string {
  const members = Array.isArray(type.members) ? type.members as Array<Record<string, unknown>> : [];
  return [
    `${type.fullName ?? type.name}${type.assembly ? ` (${type.assembly})` : ""}`,
    type.signature,
    type.summary,
    ...members.map((member) => `- ${member.signature ?? member.name}`)
  ].filter(Boolean).join("\n");
}

function memberText(type: Record<string, unknown>, member: Record<string, unknown>): string {
  return [`${type.fullName ?? type.name}.${member.name}`, member.signature, member.summary, member.assembly ? `Assembly: ${member.assembly}` : undefined].filter(Boolean).join("\n");
}

function reflectSearchLine(item: unknown): string {
  const object = asObject(item);
  if (object.kind === "member") {
    const member = asObject(object.member);
    return `member ${member.declaringType}.${member.name} - ${member.signature ?? ""}`.trim();
  }
  return `type ${object.fullName ?? object.name} - ${object.assembly ?? ""}`.trim();
}

function firstParagraph(content: string): string | undefined {
  return content.split(/\r?\n\s*\r?\n/).map((part) => part.replace(/^#.*$/m, "").trim()).find(Boolean);
}

function relativeCachePath(root: string, file: string): string {
  return relative(root, file).split(sep).join("/");
}

function cacheFailure(requestId: string, error: unknown): CliResult {
  if (error instanceof CacheError) {
    return failure(requestId, error.code, error.message, error.nextActions);
  }
  return failure(requestId, "CACHE_READ_FAILED", error instanceof Error ? error.message : String(error));
}

function normalize(value: string | undefined): string {
  return (value ?? "").trim().toLowerCase();
}

function stringField(object: Record<string, unknown>, key: string): string | undefined {
  const value = object[key];
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

function rawArray(value: unknown): unknown[] | undefined {
  return Array.isArray(value) ? value : undefined;
}

function asObject(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : {};
}

function compact<T extends Record<string, unknown>>(value: T): Record<string, unknown> {
  return Object.fromEntries(Object.entries(value).filter(([, entry]) => entry !== undefined));
}
