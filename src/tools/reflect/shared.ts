import { handleReflectCommand } from "../../docs-reflect.js";
import type { Runtime } from "../../cli-commands/types.js";
import type { ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface ReflectCommonRequest {
  reflectDir?: string;
  unityVersion?: string;
  format?: string;
}

/** 将 reflect defineTool 请求转成旧 handler flags，并保留原有 cache/分页/错误语义。 */
export async function runReflectHandler<T>(
  command: "type" | "member" | "search",
  target: string | undefined,
  request: ReflectCommonRequest & Record<string, unknown>,
  context: ToolContext
): Promise<T> {
  const flags: Record<string, string | boolean> = {};
  setStringFlag(flags, "reflect-dir", request.reflectDir);
  setStringFlag(flags, "unity-version", request.unityVersion);
  setStringFlag(flags, "format", request.format);
  setNumberFlag(flags, "page-size", request.pageSize);
  setStringFlag(flags, "cursor", request.cursor);

  const runtime = { dataDir: context.dataDir ?? context.artifactDir } as Runtime;
  const result = await handleReflectCommand(command, target ? [target] : [], flags, runtime.dataDir, context.requestId);
  if (!result.ok) {
    const details = result.error?.details && typeof result.error.details === "object" && !Array.isArray(result.error.details)
      ? result.error.details as Record<string, unknown>
      : undefined;
    throw new StructuredToolError(result.error?.code ?? "COMMAND_FAILED", result.error?.message ?? `reflect.${command} failed.`, result.error?.nextActions ?? [], details);
  }
  return result.data as T;
}

function setStringFlag(flags: Record<string, string | boolean>, name: string, value: unknown): void {
  if (typeof value === "string" && value.length > 0) flags[name] = value;
}

function setNumberFlag(flags: Record<string, string | boolean>, name: string, value: unknown): void {
  if (typeof value === "number" && Number.isFinite(value)) flags[name] = String(value);
  if (typeof value === "string" && value.length > 0) flags[name] = value;
}
