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
    readonly details?: Record<string, unknown>
  ) {
    super(message);
  }
}
