import type { ReflectMemberRequest, ReflectMemberResult } from "../tools/reflect/member.js";
import type { ReflectSearchRequest, ReflectSearchResult } from "../tools/reflect/search.js";
import type { ReflectTypeRequest, ReflectTypeResult } from "../tools/reflect/type.js";
import { StructuredToolError } from "../tools/errors.js";
import {
  assemblyDiagnostics,
  cacheError,
  cacheSource,
  filterVersion,
  loadReflectCache,
  memberText,
  paginate,
  publicMember,
  publicType,
  reflectCacheDir,
  reflectSearchLine,
  scoreEntries,
  scoreEntry,
  scoreText,
  typeMatches,
  typeText,
  unityVersion,
  type CacheIndex
} from "./docs-reflect-cache.js";

export interface ReflectServiceRuntime {
  dataDir: string;
}

/** 读取反射 cache 中的类型及成员；输入为 typed request，副作用仅为 cache 文件读取。 */
export async function getReflectType(request: ReflectTypeRequest, runtime: ReflectServiceRuntime): Promise<ReflectTypeResult> {
  const cache = await readCache(request.reflectDir, request.unityVersion, runtime);
  const matches = typeMatches(cache.entries, request.name);
  if (matches.length === 0) {
    throw new StructuredToolError("REFLECT_TYPE_NOT_FOUND", `Type was not found in the offline reflection cache: ${request.name}.`, [
      "Run tools run reflect.search --input '{\"query\":\"<query>\"}' to inspect cached types and members.",
      "Regenerate the reflection cache from Unity if the type comes from project assemblies."
    ]);
  }
  if (matches.length > 1) {
    throw new StructuredToolError("REFLECT_TYPE_AMBIGUOUS", `Multiple types matched: ${request.name}.`, [
      `Use a fully-qualified type name: ${matches.slice(0, 5).map((entry) => entry.fullName ?? entry.name).join(", ")}.`
    ]);
  }
  const type = publicType(matches[0], true);
  const format = outputFormat(request.format);
  return {
    command: "reflect.type",
    mode: "offline",
    requiresUnity: false,
    source: cacheSource(cache) as ReflectTypeResult["source"],
    unityVersion: unityVersion(cache) as ReflectTypeResult["unityVersion"],
    assemblies: (cache.assemblies ?? []) as unknown as ReflectTypeResult["assemblies"],
    diagnostics: assemblyDiagnostics(cache) as unknown as ReflectTypeResult["diagnostics"],
    name: request.name,
    format,
    type: type as ReflectTypeResult["type"],
    ...(format === "text" ? { text: typeText(type) } : {})
  };
}

/** 读取反射 cache 中的单个成员签名；最后一个点用于分隔类型和成员。 */
export async function getReflectMember(request: ReflectMemberRequest, runtime: ReflectServiceRuntime): Promise<ReflectMemberResult> {
  const cache = await readCache(request.reflectDir, request.unityVersion, runtime);
  if (!request.member.includes(".")) {
    throw new StructuredToolError("REFLECT_MEMBER_REQUIRED", "Pass a member target like Type.Member: tools run reflect.member --input '{\"member\":\"<type.member>\"}'.");
  }
  const lastDot = request.member.lastIndexOf(".");
  const typeName = request.member.slice(0, lastDot);
  const memberName = request.member.slice(lastDot + 1);
  const types = typeMatches(cache.entries, typeName);
  if (types.length === 0) {
    throw new StructuredToolError("REFLECT_TYPE_NOT_FOUND", `Type was not found in the offline reflection cache: ${typeName}.`, [
      "Run tools run reflect.search --input '{\"query\":\"<query>\"}' to inspect cached types and members."
    ]);
  }
  const matches = types.flatMap((type) => (type.members ?? []).filter((member) => normalize(member.name) === normalize(memberName)).map((member) => ({ type, member })));
  if (matches.length === 0) {
    throw new StructuredToolError("REFLECT_MEMBER_NOT_FOUND", `Member was not found in the offline reflection cache: ${request.member}.`, [
      "Run tools run reflect.type --input '{\"name\":\"<type>\"}' to inspect cached members."
    ]);
  }
  if (matches.length > 1) {
    throw new StructuredToolError("REFLECT_MEMBER_AMBIGUOUS", `Multiple members matched: ${request.member}.`, [
      "Use a fully-qualified type name or inspect overload signatures with reflect type."
    ]);
  }
  const type = publicType(matches[0].type, false);
  const member = publicMember(matches[0].member, matches[0].type);
  const format = outputFormat(request.format);
  return {
    command: "reflect.member",
    mode: "offline",
    requiresUnity: false,
    source: cacheSource(cache) as ReflectMemberResult["source"],
    unityVersion: unityVersion(cache) as ReflectMemberResult["unityVersion"],
    assemblies: (cache.assemblies ?? []) as unknown as ReflectMemberResult["assemblies"],
    diagnostics: assemblyDiagnostics(cache) as unknown as ReflectMemberResult["diagnostics"],
    target: request.member,
    format,
    type: type as ReflectMemberResult["type"],
    member: member as ReflectMemberResult["member"],
    ...(format === "text" ? { text: memberText(type, member) } : {})
  };
}

/** 搜索反射 cache 中的类型和成员，按相关性排序并返回 cursor 分页。 */
export async function searchReflect(request: ReflectSearchRequest, runtime: ReflectServiceRuntime): Promise<ReflectSearchResult> {
  const cache = await readCache(request.reflectDir, request.unityVersion, runtime);
  const typeResults = scoreEntries(cache.entries, request.query).filter((entry) => entry.score > 0).map(({ entry, score }) => ({ score, item: { ...publicType(entry, false), kind: "type" } }));
  const memberResults = cache.entries.flatMap((type) => (type.members ?? []).map((member) => ({ type, member, score: scoreEntry(member, request.query) + scoreText(type.fullName ?? type.name, request.query) })).filter((entry) => entry.score > 0).map(({ type, member, score }) => ({ score, item: { kind: "member", type: publicType(type, false), member: publicMember(member, type) } })));
  const items = [...typeResults, ...memberResults].sort((left, right) => right.score - left.score).map((result) => result.item);
  const page = paginate(items, request);
  const format = outputFormat(request.format);
  return {
    command: "reflect.search",
    mode: "offline",
    requiresUnity: false,
    source: cacheSource(cache) as ReflectSearchResult["source"],
    unityVersion: unityVersion(cache) as ReflectSearchResult["unityVersion"],
    assemblies: (cache.assemblies ?? []) as unknown as ReflectSearchResult["assemblies"],
    diagnostics: assemblyDiagnostics(cache) as unknown as ReflectSearchResult["diagnostics"],
    query: request.query,
    format,
    items: page.items as ReflectSearchResult["items"],
    pageInfo: page.pageInfo,
    ...(format === "text" ? { text: page.items.map(reflectSearchLine).join("\n") } : {})
  };
}

/** 读取、版本筛选并校验反射 cache；所有读取错误在此转换为结构化工具错误。 */
async function readCache(reflectDir: string | undefined, requestedVersion: string | undefined, runtime: ReflectServiceRuntime): Promise<CacheIndex> {
  try {
    const cache = filterVersion(await loadReflectCache(reflectCacheDir(reflectDir, runtime.dataDir)), requestedVersion, "REFLECT_VERSION_NOT_FOUND");
    if (cache.entries.length === 0 && assemblyDiagnostics(cache).some((diagnostic) => diagnostic.level === "error")) {
      throw new StructuredToolError("REFLECT_ASSEMBLY_LOAD_FAILED", "Reflection cache contains only failed assembly loads.", [
        "Regenerate the reflection cache from Unity batchmode or a live bridge context.",
        "Check assembly paths and Unity compilation errors."
      ]);
    }
    return cache;
  } catch (error) {
    throw cacheError(error);
  }
}

/** 规范化用户请求的输出格式。 */
function outputFormat(format: string | undefined): "json" | "text" {
  return format === "text" ? "text" : "json";
}

/** 规范化成员名以供不区分大小写比较。 */
function normalize(value: string): string {
  return value.trim().toLowerCase();
}
