import type { DocsGetRequest, DocsGetResult } from "../tools/docs/get.js";
import type { DocsSearchRequest, DocsSearchResult } from "../tools/docs/search.js";
import { StructuredToolError } from "../tools/errors.js";
import {
  CacheError,
  cacheError,
  cacheSource,
  docsCacheDir,
  docText,
  entryMatchesName,
  filterVersion,
  hydrateContent,
  loadDocsCache,
  paginate,
  publicDoc,
  scoreEntries,
  unityVersion
} from "./docs-reflect-cache.js";

export interface DocsServiceRuntime {
  dataDir: string;
  fetch?: DocsFetcher;
}

type DocsFetcher = (url: string) => Promise<{ ok: boolean; status: number; text: () => Promise<string> }>;
type Fetcher = DocsFetcher;

/** 读取单个 Unity API 文档；输入为 typed request，输出为业务结果，副作用仅为 cache/网络读取。 */
export async function getDocs(request: DocsGetRequest, runtime: DocsServiceRuntime): Promise<DocsGetResult> {
  let cacheFailure: StructuredToolError | undefined;
  try {
    const root = docsCacheDir(request.docsDir, runtime.dataDir);
    const cache = filterVersion(await loadDocsCache(root), request.unityVersion, "DOCS_VERSION_NOT_FOUND");
    const matches = cache.entries.filter((entry) => entryMatchesName(entry, request.api));
    if (matches.length === 0) {
      cacheFailure = new StructuredToolError("DOCS_API_NOT_FOUND", `Unity API documentation was not found in the offline cache: ${request.api}.`, [
        "Run tools run docs.search --input '{\"query\":\"<query>\"}' to inspect cached APIs.",
        "Pass --docs-dir <path> or set UNITY_CLI_DOCS_DIR to a populated cache."
      ]);
    } else if (matches.length > 1) {
      throw new StructuredToolError("DOCS_API_AMBIGUOUS", `Multiple cached Unity APIs matched: ${request.api}.`, [
        `Use one of: ${matches.slice(0, 5).map((entry) => entry.name).join(", ")}.`
      ]);
    } else {
      const doc = await hydrateContent(matches[0], cache.root);
      const format = outputFormat(request.format);
      return {
        mode: "offline",
        requiresUnity: false,
        source: cacheSource(cache) as DocsGetResult["source"],
        unityVersion: unityVersion(cache) as DocsGetResult["unityVersion"],
        api: request.api,
        format,
        doc: publicDoc(doc, format === "json") as DocsGetResult["doc"],
        ...(format === "text" ? { text: docText(doc) } : {})
      };
    }
  } catch (error) {
    cacheFailure = cacheError(error);
    if (cacheFailure.code === "DOCS_API_AMBIGUOUS") throw cacheFailure;
  }

  if (shouldUseOnlineFallback(request, cacheFailure)) {
    return getDocsOnline(request, runtime, cacheFailure);
  }
  throw cacheFailure;
}

/** 搜索 Unity API 文档；优先 cache 并保留 cursor 分页，必要时只读访问在线文档。 */
export async function searchDocs(request: DocsSearchRequest, runtime: DocsServiceRuntime): Promise<DocsSearchResult> {
  let cacheFailure: StructuredToolError | undefined;
  try {
    const root = docsCacheDir(request.docsDir, runtime.dataDir);
    const cache = filterVersion(await loadDocsCache(root), request.unityVersion, "DOCS_VERSION_NOT_FOUND");
    const items = scoreEntries(cache.entries, request.query).filter((entry) => entry.score > 0).map(({ entry }) => publicDoc(entry, false));
    if (items.length === 0 && !request.offlineOnly) {
      const notFound = new StructuredToolError("DOCS_QUERY_NOT_FOUND", `No offline docs matched: ${request.query}.`, ["Try a broader query or provide a populated docs cache."]);
      return searchDocsOnline(request, runtime, notFound);
    }
    const page = paginate(items, request);
    const format = outputFormat(request.format);
    return {
      mode: "offline",
      requiresUnity: false,
      source: cacheSource(cache) as DocsSearchResult["source"],
      unityVersion: unityVersion(cache) as DocsSearchResult["unityVersion"],
      query: request.query,
      format,
      items: page.items as DocsSearchResult["items"],
      pageInfo: page.pageInfo,
      ...(format === "text" ? { text: page.items.map((entry) => `${entry.name} - ${entry.title ?? entry.summary ?? ""}`.trim()).join("\n") } : {})
    };
  } catch (error) {
    cacheFailure = cacheError(error);
  }

  if (shouldUseOnlineFallback(request, cacheFailure)) {
    return searchDocsOnline(request, runtime, cacheFailure);
  }
  throw cacheFailure;
}

/** 根据显式离线配置和 cache 来源决定是否允许网络 fallback，不产生副作用。 */
function shouldUseOnlineFallback(request: { docsDir?: string; offlineOnly?: boolean; onlineFallback?: boolean }, error: StructuredToolError): boolean {
  if (request.offlineOnly) return false;
  const explicitCache = request.docsDir !== undefined || process.env.UNITY_CLI_DOCS_DIR !== undefined;
  const missingExplicitCache = explicitCache && error.code === "DOCS_CACHE_MISSING";
  return !missingExplicitCache || request.onlineFallback === true;
}

/** 在线读取单个 API 文档；网络不可用时抛出保留离线错误详情的结构化错误。 */
async function getDocsOnline(request: DocsGetRequest, runtime: DocsServiceRuntime, offlineError: StructuredToolError): Promise<DocsGetResult> {
  const fetcher = docsFetcher(runtime);
  if (!fetcher) throw offlineError;
  const candidates = docsUrlCandidates(request.api);
  for (const url of candidates) {
    const response = await safeFetch(fetcher, url);
    if (!response?.ok) continue;
    const html = await response.text();
    const title = extractTitle(html) ?? request.api;
    const content = htmlToText(html);
    const format = outputFormat(request.format);
    const doc = { name: request.api, title, summary: firstSentence(content), content, url, kind: "online" };
    return {
      mode: "online",
      requiresUnity: false,
      source: { kind: "online", url, offlineFallbackFrom: offlineError.code },
      unityVersion: unityVersionHint(request.unityVersion),
      api: request.api,
      format,
      doc: (format === "json" ? doc : { name: doc.name, title: doc.title, summary: doc.summary, url: doc.url, kind: doc.kind }),
      diagnostics: [{ level: "warning", code: "DOCS_ONLINE_FALLBACK", message: `Offline docs failed with ${offlineError.code}; fetched docs.unity3d.com instead.` }],
      ...(format === "text" ? { text: content } : {})
    };
  }
  throw new StructuredToolError("DOCS_ONLINE_UNAVAILABLE", `Unity online documentation was not reachable for: ${request.api}.`, [
    "Retry later, pass --offline-only to skip online lookup, or provide --docs-dir with a populated cache."
  ], { offlineError: offlineError.code, attemptedUrls: candidates });
}

/** 在线搜索 API 文档并分页；网络不可用时返回稳定错误 code 和尝试 URL。 */
async function searchDocsOnline(request: DocsSearchRequest, runtime: DocsServiceRuntime, offlineError: StructuredToolError): Promise<DocsSearchResult> {
  const fetcher = docsFetcher(runtime);
  if (!fetcher) throw offlineError;
  const url = `https://docs.unity3d.com/ScriptReference/30_search.html?q=${encodeURIComponent(request.query)}`;
  const response = await safeFetch(fetcher, url);
  if (!response?.ok) {
    throw new StructuredToolError("DOCS_ONLINE_UNAVAILABLE", `Unity online documentation search was not reachable for: ${request.query}.`, [
      "Retry later, pass --offline-only to skip online lookup, or provide --docs-dir with a populated cache."
    ], { offlineError: offlineError.code, attemptedUrl: url });
  }
  const page = paginate(extractSearchItems(await response.text(), request.query), request);
  const format = outputFormat(request.format);
  return {
    mode: "online",
    requiresUnity: false,
    source: { kind: "online", url, offlineFallbackFrom: offlineError.code },
    unityVersion: unityVersionHint(request.unityVersion),
    query: request.query,
    format,
    diagnostics: [{ level: "warning", code: "DOCS_ONLINE_FALLBACK", message: `Offline docs failed with ${offlineError.code}; searched docs.unity3d.com instead.` }],
    items: page.items,
    pageInfo: page.pageInfo,
    ...(format === "text" ? { text: page.items.map((entry) => `${entry.name} - ${entry.summary ?? ""}`.trim()).join("\n") } : {})
  };
}

/** 取得宿主 fetch 或全局 fetch，并统一为可注入接口。 */
function docsFetcher(runtime: DocsServiceRuntime): Fetcher | undefined {
  if (runtime.fetch) return runtime.fetch;
  if (typeof fetch !== "function") return undefined;
  return async (url: string) => {
    const response = await fetch(url);
    return { ok: response.ok, status: response.status, text: () => response.text() };
  };
}

/** 执行一次网络读取；网络异常被视为候选不可用。 */
async function safeFetch(fetcher: Fetcher, url: string): Promise<Awaited<ReturnType<Fetcher>> | undefined> {
  try {
    return await fetcher(url);
  } catch {
    return undefined;
  }
}

/** 为 API 名生成去重后的 Unity ScriptReference URL 候选。 */
function docsUrlCandidates(api: string): string[] {
  const normalized = api.trim();
  const shortName = normalized.replace(/^UnityEngine\./, "").replace(/^UnityEditor\./, "");
  return Array.from(new Set([
    `https://docs.unity3d.com/ScriptReference/${normalized}.html`,
    `https://docs.unity3d.com/ScriptReference/${shortName}.html`,
    `https://docs.unity3d.com/ScriptReference/${shortName.replace(/\./g, "-")}.html`
  ]));
}

/** 从 HTML title 中提取文档标题。 */
function extractTitle(html: string): string | undefined {
  return html.match(/<title[^>]*>([\s\S]*?)<\/title>/i)?.[1]?.replace(/\s*-\s*Unity.*$/i, "").trim();
}

/** 删除脚本、样式和标签并解码常用实体，生成纯文本。 */
function htmlToText(html: string): string {
  return html.replace(/<script[\s\S]*?<\/script>/gi, "").replace(/<style[\s\S]*?<\/style>/gi, "").replace(/<[^>]+>/g, " ").replace(/&nbsp;/g, " ").replace(/&amp;/g, "&").replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/\s+/g, " ").trim();
}

/** 提取最多 240 字符的首句作为摘要。 */
function firstSentence(text: string): string {
  return text.split(/(?<=[.!?])\s+/)[0]?.slice(0, 240) ?? "";
}

/** 从在线搜索 HTML 提取链接；无结果时保留可访问的搜索入口。 */
function extractSearchItems(html: string, query: string): Array<{ name: string; title: string; summary: string; url: string; kind: string }> {
  const items = Array.from(html.matchAll(/<a[^>]+href=["']([^"']+)["'][^>]*>([\s\S]*?)<\/a>/gi)).map((match) => {
    const href = match[1];
    const title = htmlToText(match[2]);
    if (!href || !title) return undefined;
    const url = href.startsWith("http") ? href : `https://docs.unity3d.com/ScriptReference/${href.replace(/^\.\//, "")}`;
    return { name: title, title, summary: url, url, kind: "online" };
  }).filter((item): item is { name: string; title: string; summary: string; url: string; kind: string } => item !== undefined);
  const url = `https://docs.unity3d.com/ScriptReference/30_search.html?q=${encodeURIComponent(query)}`;
  return items.length > 0 ? items : [{ name: query, title: query, summary: `Online Unity documentation search for ${query}.`, url, kind: "online" }];
}

/** 规范化用户请求的输出格式。 */
function outputFormat(format: string | undefined): "json" | "text" {
  return format === "text" ? "text" : "json";
}

/** 生成在线结果使用的 Unity 版本提示。 */
function unityVersionHint(requested: string | undefined): { requested?: string; resolved?: string; source: string } {
  return { requested, resolved: requested, source: "online" };
}
