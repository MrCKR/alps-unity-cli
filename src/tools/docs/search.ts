import { defineTool } from "../define.js";
import { searchDocs } from "../../services/docs-service.js";
import type { AnyJson } from "../../schema/any-json.js";

export interface DocsSearchRequest {
  query: string;
  docsDir?: string;
  unityVersion?: string;
  pageSize?: number;
  cursor?: string;
  format?: string;
  offlineOnly?: boolean;
  onlineFallback?: boolean;
}

export interface DocsSearchPageInfo {
  pageSize: number;
  cursor: string;
  nextCursor?: string;
  total: number;
}

export interface DocsSearchResult {
  command: string;
  mode: string;
  requiresUnity: boolean;
  source: Record<string, AnyJson>;
  unityVersion: Record<string, AnyJson>;
  query: string;
  format: string;
  items: Record<string, AnyJson>[];
  pageInfo: DocsSearchPageInfo;
  diagnostics?: Record<string, AnyJson>[];
  text?: string;
}

export default defineTool<DocsSearchRequest, DocsSearchResult>({
  name: "docs.search",
  summary: "Search Unity API documentation offline first, with docs.unity3d.com fallback when cache lookup misses.",
  description: "Search Unity API documentation offline first, with cursor pagination and docs.unity3d.com fallback when cache lookup misses. Pass offlineOnly=true to keep the command strictly offline.",
  group: "docs",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  target: "editor",
  examples: [
    { description: "Search cached Unity docs", argv: ["tools", "run", "docs.search", "--input", "{\"query\":\"Camera\",\"docsDir\":\".cache/unity-docs\",\"pageSize\":5}"] },
    { description: "Search cached docs offline", argv: ["tools", "run", "docs.search", "--input", "{\"query\":\"Render\",\"docsDir\":\".cache/unity-docs\",\"pageSize\":5,\"offlineOnly\":true}"] }
  ],
  /** 搜索 Unity API 文档；service 负责 cache 排序分页和只读 online fallback。 */
  execute(request, context) {
    return searchDocs(request, { dataDir: context.dataDir ?? context.artifactDir, fetch: context.fetch });
  }
});
