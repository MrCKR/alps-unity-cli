import { handleDocsCommand } from "../../docs-reflect.js";
import type { BuiltinDocsRuntime } from "../../builtin-docs.js";
import type { Runtime } from "../../cli-commands/types.js";
import type { ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface DocsApiCommonRequest {
  docsDir?: string;
  unityVersion?: string;
  format?: string;
  offlineOnly?: boolean;
  onlineFallback?: boolean;
}

/** 将 docs defineTool 请求转成旧 handler flags，并保留 cache、online fallback、分页和错误语义。 */
export async function runDocsHandler<T>(
  command: "get" | "search",
  target: string | undefined,
  request: DocsApiCommonRequest & Record<string, unknown>,
  context: ToolContext
): Promise<T> {
  const flags: Record<string, string | boolean> = {};
  setStringFlag(flags, "docs-dir", request.docsDir);
  setStringFlag(flags, "unity-version", request.unityVersion);
  setStringFlag(flags, "format", request.format);
  setNumberFlag(flags, "page-size", request.pageSize);
  setStringFlag(flags, "cursor", request.cursor);
  setBooleanFlag(flags, "offline-only", request.offlineOnly);
  setBooleanFlag(flags, "online-fallback", request.onlineFallback);

  const runtime = {
    dataDir: context.dataDir ?? context.artifactDir,
    fetch: context.fetch
  } as Runtime & BuiltinDocsRuntime;
  const result = await handleDocsCommand(command, target ? [target] : [], flags, runtime.dataDir, context.requestId, runtime);
  if (!result.ok) {
    const details = result.error?.details && typeof result.error.details === "object" && !Array.isArray(result.error.details)
      ? result.error.details as Record<string, unknown>
      : undefined;
    throw new StructuredToolError(result.error?.code ?? "COMMAND_FAILED", result.error?.message ?? `docs.${command} failed.`, result.error?.nextActions ?? [], details);
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

function setBooleanFlag(flags: Record<string, string | boolean>, name: string, value: unknown): void {
  if (typeof value === "boolean") flags[name] = value;
}
