import { defineTool } from "../define.js";
import { runDocsHandler } from "./shared.js";

export interface DocsGetRequest {
  api: string;
  docsDir?: string;
  unityVersion?: string;
  format?: string;
  offlineOnly?: boolean;
  onlineFallback?: boolean;
}

export interface DocsGetResult {
  command: string;
  mode: string;
  requiresUnity: boolean;
  source: Record<string, unknown>;
  unityVersion: Record<string, unknown>;
  api: string;
  format: string;
  doc: Record<string, unknown>;
  diagnostics?: Record<string, unknown>[];
  text?: string;
}

export default defineTool<DocsGetRequest, DocsGetResult>({
  name: "docs.get",
  summary: "Read one Unity API document offline first, with docs.unity3d.com fallback when cache lookup misses.",
  description: "Read one Unity API document from the offline docs cache first, with docs.unity3d.com fallback when cache lookup misses. A populated docs cache via --docs-dir or UNITY_CLI_DOCS_DIR is preferred; pass offlineOnly=true to forbid network fallback. If --docs-dir points to a missing cache, the command preserves DOCS_CACHE_MISSING unless onlineFallback=true is set explicitly.",
  group: "docs",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  target: "editor",
  examples: [
    { description: "Read cached Camera docs", argv: ["tools", "run", "docs.get", "--input", "{\"api\":\"UnityEngine.Camera\",\"docsDir\":\".cache/unity-docs\",\"format\":\"text\"}"] }
  ],
  /** 读取离线 Unity API 文档；旧 handler 负责 cache 解析和 online fallback。 */
  execute(request, context) {
    return runDocsHandler<DocsGetResult>("get", request.api, request as DocsGetRequest & Record<string, unknown>, context);
  }
});
