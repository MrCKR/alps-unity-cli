import { defineTool } from "../define.js";
import { runReflectHandler } from "./shared.js";

export interface ReflectTypeRequest {
  name: string;
  reflectDir?: string;
  unityVersion?: string;
  format?: string;
}

export interface ReflectTypeResult {
  command: string;
  mode: string;
  requiresUnity: boolean;
  source: Record<string, unknown>;
  unityVersion: Record<string, unknown>;
  assemblies: Record<string, unknown>[];
  diagnostics: Record<string, unknown>[];
  name: string;
  format: string;
  type: Record<string, unknown>;
  text?: string;
}

export default defineTool<ReflectTypeRequest, ReflectTypeResult>({
  name: "reflect.type",
  summary: "Look up a C# type and its member names in the offline Unity reflection cache.",
  description: "Look up a C# type and its member names in the offline Unity reflection cache. Use this to verify a type exists and read its public member names before writing C# or calling reflection.method.*. Use reflect.search first when the exact type name is unknown.",
  group: "reflect",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  target: "editor",
  examples: [
    { description: "Read a type's member names", argv: ["tools", "run", "reflect.type", "--input", "{\"name\":\"UnityEngine.Camera\",\"reflectDir\":\".cache/unity-reflect\",\"format\":\"text\"}"] }
  ],
  /** 读取离线反射 cache 中的 type 详情；不连接 Unity Editor。 */
  execute(request, context) {
    return runReflectHandler<ReflectTypeResult>("type", request.name, request as ReflectTypeRequest & Record<string, unknown>, context);
  }
});
