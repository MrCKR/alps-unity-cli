import { defineTool } from "../define.js";
import { runReflectHandler } from "./shared.js";

export interface ReflectSearchRequest {
  query: string;
  reflectDir?: string;
  pageSize?: number;
  cursor?: string;
  format?: string;
}

export interface ReflectSearchPageInfo {
  pageSize: number;
  cursor: string;
  nextCursor?: string;
  total: number;
}

export interface ReflectSearchResult {
  command: string;
  mode: string;
  requiresUnity: boolean;
  source: Record<string, unknown>;
  unityVersion: Record<string, unknown>;
  assemblies: Record<string, unknown>[];
  diagnostics: Record<string, unknown>[];
  query: string;
  format: string;
  items: Record<string, unknown>[];
  pageInfo: ReflectSearchPageInfo;
  text?: string;
}

export default defineTool<ReflectSearchRequest, ReflectSearchResult>({
  name: "reflect.search",
  summary: "Search cached C# types and members by query, returning ranked, paged results.",
  description: "Search cached C# types and members by query, returning ranked, paged results. Use this to discover exact type/member names before reflect.type or reflect.member. Results include both type and member matches scored by the query.",
  group: "reflect",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  target: "editor",
  examples: [
    { description: "Search for Render-related members", argv: ["tools", "run", "reflect.search", "--input", "{\"query\":\"Render\",\"reflectDir\":\".cache/unity-reflect\",\"pageSize\":10}"] }
  ],
  /** 搜索离线反射 cache 并保留旧分页语义；不连接 Unity Editor。 */
  execute(request, context) {
    return runReflectHandler<ReflectSearchResult>("search", request.query, request as ReflectSearchRequest & Record<string, unknown>, context);
  }
});
