import { createHash } from "node:crypto";
import { constants } from "node:fs";
import { access, readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { stdin } from "node:process";
import { executeBridgePayload, type BridgeRuntime } from "./bridge/index.js";
import type { CompletionProbe } from "./tools/completion-probe.js";
import { StructuredToolError } from "./tools/errors.js";

export interface RuntimeCodeRuntime extends BridgeRuntime {
  dataDir: string;
}

export interface RuntimeCodeRequest {
  file?: string;
  stdin?: boolean;
  code?: string;
  refs?: string[];
  define?: string[];
  args?: Record<string, unknown>;
  timeoutMs?: number;
  instance?: string;
  dryRun?: boolean;
  completionProbe?: CompletionProbe;
}

interface CodeSource {
  kind: "file" | "stdin" | "inline";
  path?: string;
  text: string;
  sha256: string;
  sizeBytes: number;
}

const liveDefaultTimeoutMs = 60_000;

/** 只通过已打开 Editor 执行 Runtime Code；提供 completionProbe 时返回业务完成证明。 */
export async function executeRuntimeCode(request: RuntimeCodeRequest, runtime: RuntimeCodeRuntime, requestId: string): Promise<Record<string, unknown>> {
  const source = await readCodeSource(request);
  const timeoutMs = normalizeTimeout(request.timeoutMs);
  const parsedArgs = request.args ?? {};
  const refs = await validateExtraReferences(request.refs);
  const payload = {
    sourceKind: source.kind,
    sourcePath: source.path,
    sourceText: source.text,
    sourceSha256: source.sha256,
    sourceSizeBytes: source.sizeBytes,
    argsJson: JSON.stringify(parsedArgs),
    defines: request.define?.join(",") ?? "",
    refs,
    ...(request.completionProbe ? { completionProbe: request.completionProbe } : {}),
  };

  if (request.dryRun) {
    return {
      dryRun: true,
      preview: {
        sourceKind: source.kind,
        ...(source.path ? { sourcePath: source.path } : {}),
        sourceSha256: source.sha256,
        sourceSizeBytes: source.sizeBytes,
        timeoutMs,
        args: parsedArgs,
      },
      plannedEffects: [{ type: "code.exec", sideEffects: "user-code-not-executed" }],
    };
  }

  const result = await executeBridgePayload(requestId, "code.exec", payload, {
    instance: request.instance ?? "",
    "timeout-ms": String(timeoutMs),
    __destructiveConfirmed: true,
  }, runtime, undefined, { mode: "execute", mutation: true, defaultTimeoutMs: timeoutMs });
  if (!result.ok) throwCliFailure(result);
  return (result.data ?? {}) as Record<string, unknown>;
}

/** 读取且哈希唯一代码源，所有文件错误均发生在 Unity effect 前。 */
async function readCodeSource(request: RuntimeCodeRequest): Promise<CodeSource> {
  const selected = [request.file !== undefined, request.code !== undefined, request.stdin === true].filter(Boolean).length;
  if (selected !== 1) throw new StructuredToolError("E_CODE_SOURCE_INVALID", "Pass exactly one code source: file, stdin, or inline code.");
  try {
    if (request.file) {
      const path = resolve(request.file);
      return codeSource("file", await readFile(path, "utf8"), path);
    }
    if (request.code !== undefined) return codeSource("inline", request.code);
    return codeSource("stdin", await readAllStdin());
  } catch (error) {
    if (error instanceof StructuredToolError) throw error;
    throw new StructuredToolError("E_CODE_SOURCE_INVALID", error instanceof Error ? error.message : String(error), ["Check that the source path exists and is UTF-8 readable."]);
  }
}

function codeSource(kind: CodeSource["kind"], text: string, path?: string): CodeSource {
  if (text.trim().length === 0) throw new StructuredToolError("E_CODE_SOURCE_INVALID", "Code source is empty.");
  return { kind, path, text, sha256: createHash("sha256").update(text, "utf8").digest("hex"), sizeBytes: Buffer.byteLength(text, "utf8") };
}

async function readAllStdin(): Promise<string> {
  let text = "";
  stdin.setEncoding("utf8");
  for await (const chunk of stdin) text += chunk;
  return text;
}

function normalizeTimeout(value: number | undefined): number {
  if (!value || value <= 0) return liveDefaultTimeoutMs;
  return Math.min(Math.trunc(value), 600_000);
}

/** 引用必须在 effect 前全部存在且可读。 */
async function validateExtraReferences(refs: string[] | undefined): Promise<string> {
  if (!refs) return "";
  const normalized = refs.map((part) => part.trim()).filter(Boolean);
  for (const reference of normalized) {
    try { await access(reference, constants.R_OK); }
    catch (error) {
      throw new StructuredToolError("E_REFERENCE_RESOLUTION_FAILED", `Runtime Code reference is not readable: ${reference}.`, [error instanceof Error ? error.message : String(error)]);
    }
  }
  return normalized.join(",");
}

function throwCliFailure(result: { error?: { code?: string; message?: string; nextActions?: string[]; details?: unknown; transient?: boolean; retryAfterMs?: number } }): never {
  const error = result.error ?? {};
  throw StructuredToolError.from({
    code: error.code ?? "CODE_EXEC_FAILED",
    message: error.message ?? "Runtime Code execution failed.",
    nextActions: error.nextActions,
    details: error.details,
    transient: error.transient,
    retryAfterMs: error.retryAfterMs,
  });
}
