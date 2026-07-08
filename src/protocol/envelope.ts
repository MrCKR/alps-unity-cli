/**
 * 传输层协议类型(CLI ↔ Bridge 全链路唯一权威源头)。
 *
 * 设计依据:.plan/协议框架.md
 * - JSON 线材统一 camelCase(TS 源头即 camelCase,C# 生成侧由生成器映射为 PascalCase 属性)
 * - null 策略 = proto3 语义:无值不发(序列化时省略 null/默认值字段)
 * - 这些类型通过 scripts/gen-protocol.mjs 单向生成 C# DTO(unitypkg/Editor/Protocol/*.cs),
 *   生成产物提交 git,禁止手改。
 *
 * 本文件只定义传输层(envelope/error/request),不含各工具的业务 DTO——
 * 工具协议定义的源头跟着执行逻辑走(C# 工具在 C#,TS 工具在 TS),见 .plan/工具统一形态.md。
 */

// ─── 统一信封 ────────────────────────────────────────────────────────────

/** CLI ↔ Bridge 统一响应信封。所有 Bridge 响应都包在此结构内。 */
export interface Envelope<TData = unknown> {
  /** 是否成功。true 时 data 有值;false 时 error 有值。 */
  ok: boolean;
  /** 请求追踪 ID,成功/失败均可能携带。无值时不发(proto3)。 */
  requestId?: string;
  /** 失败时的结构化错误。成功时不发。 */
  error?: ToolError;
  /** 成功时的业务数据。失败时不发。 */
  data?: TData;
}

// ─── 错误 ────────────────────────────────────────────────────────────────

/**
 * 结构化错误。details 也是 DTO(不再手拼 JSON 字符串)。
 * 报错路径:handler 抛携带结构化信息的异常 → 框架 catch → 转 ToolError → 序列化进 envelope.error。
 */
export interface ToolError<TDetails = unknown> {
  /** 稳定错误码,机器可读,如 E_TARGET_NOT_FOUND。 */
  code: string;
  /** 人类可读错误说明。 */
  message: string;
  /** 结构化错误详情(DTO,不再手拼 JSON)。无值时不发。 */
  details?: TDetails;
  /** 可执行的下一步建议(机器可读恢复动作说明)。空数组或不发。 */
  nextActions?: string[];
  /** 是否为瞬时错误(可重试)。无值时视为 false。 */
  transient?: boolean;
  /** 瞬时错误建议重试间隔(毫秒)。无值时不发。 */
  retryAfterMs?: number;
}

// ─── 请求 ────────────────────────────────────────────────────────────────

/**
 * CLI → Bridge 的请求信封。command 字符串是天然 kind/discriminator,仅用于路由;
 * 字段取用走反序列化好的 DTO(废除 BridgeRequest 的正则刮 JSON)。
 */
export interface BridgeRequest<TInput = unknown> {
  /** 命令名,路由用(如 "scene.hierarchy")。 */
  command: string;
  /** 请求追踪 ID。无值时不发。 */
  requestId?: string;
  /** 命令输入,反序列化为具体命令的 Request DTO。无值时视为空对象。 */
  input?: TInput;
}
