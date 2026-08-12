import { sanitizeEditorBlockers, type EditorBlocker } from "../editor-blocker-observer.js";

/**
 * TS defineTool 统一结构化错误。
 *
 * 由本地 TS 工具抛出,`tools run` 捕获后转换为标准 failure envelope。
 * 适用于 docs/reflect/package 等不属于 Bridge 的离线能力,避免退化成 COMMAND_FAILED。
 */
export class StructuredToolError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly nextActions: string[] = [],
    readonly details?: Record<string, unknown>,
    readonly transient?: boolean,
    readonly retryAfterMs?: number
  ) {
    super(message);
  }

  /** 跨内部抽象边界转发完整错误语义，不丢失 wire retry 元数据。 */
  static from(error: {
    code: string;
    message: string;
    nextActions?: string[];
    details?: unknown;
    transient?: boolean;
    retryAfterMs?: number;
  }): StructuredToolError {
    return new StructuredToolError(
      error.code,
      error.message,
      error.nextActions ?? [],
      error.details as Record<string, unknown> | undefined,
      error.transient,
      error.retryAfterMs
    );
  }
}

/** 仅转发已公开的 blocker DTO，禁止旧字段或内部观察数据穿透。 */
export function blockersFromDetails(details: Record<string, unknown> | undefined): EditorBlocker[] | undefined {
  if (!details) return undefined;
  const blockers = sanitizeEditorBlockers(details.blockers);
  return blockers.length > 0 ? blockers : undefined;
}

/** effect 已开始后的 timeout 保留原错误码，并提供当前 modal 的明确恢复操作。 */
export function blockerTimeoutMessage(operation: string, blockers: EditorBlocker[] | undefined): string {
  return blockers?.[0] ? `${operation} did not return a terminal result before the request deadline; modal dialog '${blockers[0].title}' was observed.` : `${operation} did not return a terminal result before the request deadline.`;
}

export function blockerNextActions(): string[] {
  return [
    "Run unity_cli tools run editor.dialogs to inspect the current modal dialog.",
    "Run unity_cli tools run editor.dialog.click with a current dialogId and exact button label.",
  ];
}
