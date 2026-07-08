import { defineTool } from "../define.js";
import { runReflectHandler } from "./shared.js";

export interface ReflectMemberRequest {
  member: string;
  reflectDir?: string;
  format?: string;
}

export interface ReflectMemberResult {
  command: string;
  mode: string;
  requiresUnity: boolean;
  source: Record<string, unknown>;
  unityVersion: Record<string, unknown>;
  assemblies: Record<string, unknown>[];
  diagnostics: Record<string, unknown>[];
  target: string;
  format: string;
  type: Record<string, unknown>;
  member: Record<string, unknown>;
  text?: string;
}

export default defineTool<ReflectMemberRequest, ReflectMemberResult>({
  name: "reflect.member",
  summary: "Look up a single member's full signature on a C# type in the offline reflection cache.",
  description: "Look up a single member's full signature on a C# type in the offline reflection cache. Pass the member as Type.Member (last dot splits type and member). Use this for detailed parameter/return info; use reflect.type for the whole member list.",
  group: "reflect",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  target: "editor",
  examples: [
    { description: "Read one member's full signature", argv: ["tools", "run", "reflect.member", "--input", "{\"member\":\"UnityEngine.Camera.Render\",\"reflectDir\":\".cache/unity-reflect\",\"format\":\"text\"}"] }
  ],
  /** 读取离线反射 cache 中的单个成员签名；不连接 Unity Editor。 */
  execute(request, context) {
    return runReflectHandler<ReflectMemberResult>("member", request.member, request as ReflectMemberRequest & Record<string, unknown>, context);
  }
});
