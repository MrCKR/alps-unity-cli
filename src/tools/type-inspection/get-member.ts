import { defineTool } from "../define.js";
import { getReflectMember } from "../../services/reflect-service.js";
import type { AnyJson } from "../../schema/any-json.js";

export interface ReflectMemberRequest {
  member: string;
  reflectDir?: string;
  unityVersion?: string;
  format?: string;
}

export interface ReflectMemberResult {
  mode: string;
  requiresUnity: boolean;
  source: Record<string, AnyJson>;
  unityVersion: Record<string, AnyJson>;
  assemblies: Record<string, AnyJson>[];
  diagnostics: Record<string, AnyJson>[];
  target: string;
  format: string;
  type: Record<string, AnyJson>;
  member: Record<string, AnyJson>;
  text?: string;
}

export default defineTool<ReflectMemberRequest, ReflectMemberResult>({
  name: "type-inspection.get-member",
  summary: "Look up a single member's full signature on a C# type in the offline reflection cache.",
  description: "Look up a single member's full signature on a C# type in the offline reflection cache. Pass the member as Type.Member (last dot splits type and member). Use this for detailed parameter/return info; use type-inspection.get-type for the whole member list.",
  group: "type-inspection",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  target: "editor",
  examples: [
    { description: "Read one member's full signature", argv: ["tools", "run", "type-inspection.get-member", "--input", "{\"member\":\"UnityEngine.Camera.Render\",\"reflectDir\":\".cache/unity-reflect\",\"format\":\"text\"}"] },
    { description: "Read one member as JSON", argv: ["tools", "run", "type-inspection.get-member", "--input", "{\"member\":\"UnityEngine.Camera.Render\",\"reflectDir\":\".cache/unity-reflect\",\"format\":\"json\"}"] }
  ],
  /** 读取离线反射 cache 中的单个成员签名；service 只执行文件读取和筛选。 */
  execute(request, context) {
    return getReflectMember(request, { dataDir: context.dataDir ?? context.artifactDir });
  }
});
