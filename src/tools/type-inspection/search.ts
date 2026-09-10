import { defineTool } from "../define.js";
import { searchReflect } from "../../services/reflect-service.js";
import type { AnyJson } from "../../schema/any-json.js";

export interface ReflectSearchRequest {
  query: string;
  reflectDir?: string;
  unityVersion?: string;
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
  mode: string;
  requiresUnity: boolean;
  source: Record<string, AnyJson>;
  unityVersion: Record<string, AnyJson>;
  assemblies: Record<string, AnyJson>[];
  diagnostics: Record<string, AnyJson>[];
  query: string;
  format: string;
  items: Record<string, AnyJson>[];
  pageInfo: ReflectSearchPageInfo;
  text?: string;
}

export default defineTool<ReflectSearchRequest, ReflectSearchResult>({
  name: "type-inspection.search",
  summary: "Search cached C# types and members by query, returning ranked, paged results.",
  description: "Search cached C# types and members by query, returning ranked, paged results. Use this to discover exact type/member names before type-inspection.get-type or reflect.member. Results include both type and member matches scored by the query.",
  group: "type-inspection",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  target: "editor",
  examples: [
    { description: "Search for Render-related members", argv: ["tools", "run", "type-inspection.search", "--input", "{\"query\":\"Render\",\"reflectDir\":\".cache/unity-reflect\",\"pageSize\":10}"] },
    { description: "Search reflection data as text", argv: ["tools", "run", "type-inspection.search", "--input", "{\"query\":\"Camera\",\"reflectDir\":\".cache/unity-reflect\",\"pageSize\":10,\"format\":\"text\"}"] }
  ],
  /** 搜索离线反射 cache 中的类型和成员；service 负责排序与 cursor 分页。 */
  execute(request, context) {
    return searchReflect(request, { dataDir: context.dataDir ?? context.artifactDir });
  }
});
