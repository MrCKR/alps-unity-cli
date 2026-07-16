import { defineTool } from "../define.js";
import { getDocs } from "../../services/docs-service.js";
import type { AnyJson } from "../../schema/any-json.js";

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
  source: Record<string, AnyJson>;
  unityVersion: Record<string, AnyJson>;
  api: string;
  format: string;
  doc: Record<string, AnyJson>;
  diagnostics?: Record<string, AnyJson>[];
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
    { description: "Read cached Camera docs", argv: ["tools", "run", "docs.get", "--input", "{\"api\":\"UnityEngine.Camera\",\"docsDir\":\".cache/unity-docs\",\"format\":\"text\"}"] },
    { description: "Read cached docs without network fallback", argv: ["tools", "run", "docs.get", "--input", "{\"api\":\"UnityEngine.Camera\",\"docsDir\":\".cache/unity-docs\",\"format\":\"json\",\"offlineOnly\":true}"] }
  ],
  /** 读取 Unity API 文档；service 负责离线 cache、错误转换和只读 online fallback。 */
  execute(request, context) {
    return getDocs(request, { dataDir: context.dataDir ?? context.artifactDir, fetch: context.fetch });
  }
});
