import { defineTool } from "../define.js";
import { getReflectType } from "../../services/reflect-service.js";
import type { AnyJson } from "../../schema/any-json.js";

export interface ReflectTypeRequest {
  name: string;
  reflectDir?: string;
  unityVersion?: string;
  format?: string;
}

export interface ReflectTypeResult {
  mode: string;
  requiresUnity: boolean;
  source: Record<string, AnyJson>;
  unityVersion: Record<string, AnyJson>;
  assemblies: Record<string, AnyJson>[];
  diagnostics: Record<string, AnyJson>[];
  name: string;
  format: string;
  type: Record<string, AnyJson>;
  text?: string;
}

export default defineTool<ReflectTypeRequest, ReflectTypeResult>({
  name: "type-inspection.get-type",
  summary: "Look up a C# type and its member names in the offline Unity reflection cache.",
  description: "Look up a C# type and its member names in the offline Unity reflection cache. Use this to verify a type exists and read its public member names before writing C# or calling reflection.method.*. Use type-inspection.search first when the exact type name is unknown.",
  group: "type-inspection",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  target: "editor",
  examples: [
    { description: "Read a type's member names", argv: ["tools", "run", "type-inspection.get-type", "--input", "{\"name\":\"UnityEngine.Camera\",\"reflectDir\":\".cache/unity-reflect\",\"format\":\"text\"}"] },
    { description: "Read a type as JSON", argv: ["tools", "run", "type-inspection.get-type", "--input", "{\"name\":\"UnityEngine.Camera\",\"reflectDir\":\".cache/unity-reflect\",\"format\":\"json\"}"] }
  ],
  /** 读取离线反射 cache 中的 type 详情；service 只执行文件读取和筛选。 */
  execute(request, context) {
    return getReflectType(request, { dataDir: context.dataDir ?? context.artifactDir });
  }
});
