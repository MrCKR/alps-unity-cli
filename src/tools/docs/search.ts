import { defineTool } from "../define.js";
import { runDocsHandler } from "./shared.js";

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
  source: Record<string, unknown>;
  unityVersion: Record<string, unknown>;
  query: string;
  format: string;
  items: Record<string, unknown>[];
  pageInfo: DocsSearchPageInfo;
  diagnostics?: Record<string, unknown>[];
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
    { description: "Search cached Unity docs", argv: ["tools", "run", "docs.search", "--input", "{\"query\":\"Camera\",\"docsDir\":\".cache/unity-docs\",\"pageSize\":5}"] }
  ],
  /** 搜索离线 Unity API 文档；旧 handler 负责分页、online fallback 和错误语义。 */
  execute(request, context) {
    return runDocsHandler<DocsSearchResult>("search", request.query, request as DocsSearchRequest & Record<string, unknown>, context);
  }
});
