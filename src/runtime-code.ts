import { createHash } from "node:crypto";
import { constants } from "node:fs";
import { access, open } from "node:fs/promises";
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
  absoluteDeadlineMs?: number;
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
const maxCodeSourceBytes = 768 * 1024;
const maxBridgePayloadBytes = 900 * 1024;

/** 只通过已打开 Editor 执行 Runtime Code；提供 completionProbe 时返回业务完成证明。 */
export async function executeRuntimeCode(request: RuntimeCodeRequest, runtime: RuntimeCodeRuntime, requestId: string): Promise<Record<string, unknown>> {
  const timeoutMs = normalizeTimeout(request.timeoutMs);
  const absoluteDeadlineMs = request.absoluteDeadlineMs ?? Date.now() + timeoutMs;
  requireRuntimeCodeDeadline(absoluteDeadlineMs);
  const source = await readCodeSource(request, absoluteDeadlineMs);
  requireRuntimeCodeDeadline(absoluteDeadlineMs);
  const parsedArgs = request.args ?? {};
  const refs = await validateExtraReferences(request.refs, absoluteDeadlineMs);
  requireRuntimeCodeDeadline(absoluteDeadlineMs);
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
  const payloadBytes = Buffer.byteLength(JSON.stringify(payload), "utf8");
  if (payloadBytes > maxBridgePayloadBytes)
    throw new StructuredToolError("E_CODE_INPUT_TOO_LARGE", `Runtime Code Bridge payload exceeds the ${maxBridgePayloadBytes}-byte local limit.`);
  requireRuntimeCodeDeadline(absoluteDeadlineMs);

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
      plannedEffects: [{ type: "code.execute", sideEffects: "user-code-not-executed" }],
    };
  }

  requireRuntimeCodeDeadline(absoluteDeadlineMs);
  const result = await executeBridgePayload(requestId, "code.execute", payload, {
    instance: request.instance ?? "",
    "timeout-ms": String(timeoutMs),
    __destructiveConfirmed: true,
  }, runtime, { mode: "execute", mutation: true, defaultTimeoutMs: timeoutMs, absoluteDeadlineMs });
  if (!result.ok) throwCliFailure(result);
  return (result.data ?? {}) as Record<string, unknown>;
}

/** 读取且哈希唯一代码源，所有文件错误均发生在 Unity effect 前。 */
async function readCodeSource(request: RuntimeCodeRequest, absoluteDeadlineMs: number): Promise<CodeSource> {
  const selected = [request.file !== undefined, request.code !== undefined, request.stdin === true].filter(Boolean).length;
  if (selected !== 1) throw new StructuredToolError("E_CODE_SOURCE_INVALID", "Pass exactly one code source: file, stdin, or inline code.");
  try {
    if (request.file) {
      const path = resolve(request.file);
      return codeSource("file", await readBoundedCodeFile(path, absoluteDeadlineMs), path);
    }
    if (request.code !== undefined) return codeSource("inline", request.code);
    return codeSource("stdin", await readAllStdin(absoluteDeadlineMs));
  } catch (error) {
    if (error instanceof StructuredToolError) throw error;
    throw new StructuredToolError("E_CODE_SOURCE_INVALID", error instanceof Error ? error.message : String(error), ["Check that the source path exists and is UTF-8 readable."]);
  }
}

/** 从同一文件句柄按字节和deadline有界读取代码，避免路径替换、增长或阻塞绕过入口预算。 */
async function readBoundedCodeFile(path: string, absoluteDeadlineMs: number): Promise<string> {
  const openPromise = open(path, "r");
  const handle = await beforeRuntimeCodeDeadline(openPromise, absoluteDeadlineMs, () => {
    void openPromise.then((lateHandle) => lateHandle.close()).catch(() => undefined);
  });
  const chunks: Buffer[] = [];
  let total = 0;
  let closeDeferred = false;
  try {
    while (true) {
      requireRuntimeCodeDeadline(absoluteDeadlineMs);
      const buffer = Buffer.allocUnsafe(Math.min(64 * 1024, maxCodeSourceBytes + 1 - total));
      const readPromise = handle.read(buffer, 0, buffer.length, null);
      const { bytesRead } = await beforeRuntimeCodeDeadline(readPromise, absoluteDeadlineMs, () => {
        closeDeferred = true;
        void readPromise.then(() => handle.close(), () => handle.close()).catch(() => undefined);
      });
      if (bytesRead === 0) break;
      total += bytesRead;
      if (total > maxCodeSourceBytes)
        throw new StructuredToolError("E_CODE_INPUT_TOO_LARGE", `Runtime Code source file exceeds the ${maxCodeSourceBytes}-byte local limit.`);
      chunks.push(buffer.subarray(0, bytesRead));
    }
    requireRuntimeCodeDeadline(absoluteDeadlineMs);
    return Buffer.concat(chunks, total).toString("utf8");
  } finally { if (!closeDeferred) await handle.close(); }
}

function codeSource(kind: CodeSource["kind"], text: string, path?: string): CodeSource {
  if (text.trim().length === 0) throw new StructuredToolError("E_CODE_SOURCE_INVALID", "Code source is empty.");
  const sizeBytes = Buffer.byteLength(text, "utf8");
  if (sizeBytes > maxCodeSourceBytes)
    throw new StructuredToolError("E_CODE_INPUT_TOO_LARGE", `Runtime Code source exceeds the ${maxCodeSourceBytes}-byte local limit.`);
  return { kind, path, text, sha256: createHash("sha256").update(text, "utf8").digest("hex"), sizeBytes };
}

/** 事件式读取Runtime Code源码stdin，达到字节或deadline上限时立即解除监听。 */
async function readAllStdin(absoluteDeadlineMs: number): Promise<string> {
  requireRuntimeCodeDeadline(absoluteDeadlineMs);
  return new Promise<string>((resolvePromise, rejectPromise) => {
    let text = "";
    let sizeBytes = 0;
    let timer: NodeJS.Timeout | undefined;
    const cleanup = (): void => {
      if (timer) clearTimeout(timer);
      stdin.off("data", onData);
      stdin.off("end", onEnd);
      stdin.off("error", onError);
      stdin.pause();
    };
    const onData = (chunk: string | Buffer): void => {
      const value = typeof chunk === "string" ? chunk : chunk.toString("utf8");
      sizeBytes += Buffer.byteLength(value, "utf8");
      if (sizeBytes > maxCodeSourceBytes) {
        cleanup();
        rejectPromise(new StructuredToolError("E_CODE_INPUT_TOO_LARGE", `Runtime Code stdin exceeds the ${maxCodeSourceBytes}-byte local limit.`));
        return;
      }
      text += value;
    };
    const onEnd = (): void => { cleanup(); resolvePromise(text); };
    const onError = (error: Error): void => { cleanup(); rejectPromise(error); };
    stdin.setEncoding("utf8");
    stdin.on("data", onData);
    stdin.once("end", onEnd);
    stdin.once("error", onError);
    timer = setTimeout(() => {
      cleanup();
      rejectPromise(new StructuredToolError("E_EXECUTION_TIMEOUT", "Runtime Code stdin did not finish reading before the immutable deadline."));
    }, Math.max(1, absoluteDeadlineMs - Date.now()));
    stdin.resume();
  });
}

function requireRuntimeCodeDeadline(absoluteDeadlineMs: number): void {
  if (!Number.isFinite(absoluteDeadlineMs) || Date.now() >= absoluteDeadlineMs)
    throw new StructuredToolError("E_EXECUTION_TIMEOUT", "Runtime Code immutable deadline elapsed before Bridge dispatch.", ["Reduce local input size or submit a new request with a sufficient timeout."]);
}

function normalizeTimeout(value: number | undefined): number {
  if (!value || value <= 0) return liveDefaultTimeoutMs;
  return Math.trunc(value);
}

/** 引用必须在 effect 前全部存在且可读。 */
async function validateExtraReferences(refs: string[] | undefined, absoluteDeadlineMs: number): Promise<string> {
  if (!refs) return "";
  if (refs.length > 64) throw new StructuredToolError("E_REFERENCE_RESOLUTION_FAILED", "Runtime Code accepts at most 64 extra references.");
  const normalized = refs.map((part) => part.trim()).filter(Boolean);
  for (const reference of normalized) {
    if (Buffer.byteLength(reference, "utf8") > 2048) throw new StructuredToolError("E_REFERENCE_RESOLUTION_FAILED", "Runtime Code reference path exceeds 2048 UTF-8 bytes.");
    requireRuntimeCodeDeadline(absoluteDeadlineMs);
    try {
      await accessReferenceBeforeDeadline(reference, absoluteDeadlineMs);
      requireRuntimeCodeDeadline(absoluteDeadlineMs);
    }
    catch (error) {
      if (error instanceof StructuredToolError) throw error;
      throw new StructuredToolError("E_REFERENCE_RESOLUTION_FAILED", `Runtime Code reference is not readable: ${reference}.`, [error instanceof Error ? error.message : String(error)]);
    }
  }
  return normalized.join(",");
}

/** 将不可取消的代码源I/O等待限制在入口deadline内，并由调用方安排迟到资源回收。 */
async function beforeRuntimeCodeDeadline<T>(operation: Promise<T>, absoluteDeadlineMs: number, onLate?: () => void): Promise<T> {
  requireRuntimeCodeDeadline(absoluteDeadlineMs);
  let timer: NodeJS.Timeout | undefined;
  try {
    return await Promise.race([
      operation,
      new Promise<never>((_, rejectPromise) => { timer = setTimeout(() => { onLate?.(); rejectPromise(new StructuredToolError("E_EXECUTION_TIMEOUT", "Runtime Code local I/O exceeded the immutable deadline.")); }, Math.max(1, absoluteDeadlineMs - Date.now())); }),
    ]);
  } finally { if (timer) clearTimeout(timer); }
}

/** 引用可读性检查与入口deadline竞速，避免本地I/O无限延长请求。 */
async function accessReferenceBeforeDeadline(reference: string, absoluteDeadlineMs: number): Promise<void> {
  const remainingMs = Math.max(1, absoluteDeadlineMs - Date.now());
  let timer: NodeJS.Timeout | undefined;
  try {
    await Promise.race([
      access(reference, constants.R_OK),
      new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new StructuredToolError("E_EXECUTION_TIMEOUT", "Runtime Code reference validation exceeded the immutable deadline.")), remainingMs); }),
    ]);
  } finally { if (timer) clearTimeout(timer); }
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
