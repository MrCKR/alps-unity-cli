/**
 * CLI ↔ Bridge 全链路唯一传输层协议源。
 *
 * JSON wire 使用 camelCase，null 字段不发送；scripts/gen-protocol.mjs 从这里单向生成
 * unitypkg/Editor/Protocol 下的 C# DTO。业务 DTO 归属各自的 Tool 或 Context。
 */

// ─── 统一信封 ────────────────────────────────────────────────────────────

/** Bridge 成功响应。 */
export interface SuccessEnvelope<TData = unknown> {
  ok: true;
  requestId: string;
  data: TData;
  warnings?: string[];
  logsRef?: string;
  jobId?: string;
  durationMs?: number;
}

/** Bridge 失败响应。nextActions 只属于 error。 */
export interface ErrorEnvelope<TDetails = unknown> {
  ok: false;
  requestId: string;
  error: ToolError<TDetails>;
  durationMs?: number;
}

/** CLI ↔ Bridge 唯一响应信封。 */
export type Envelope<TData = unknown, TDetails = unknown> = SuccessEnvelope<TData> | ErrorEnvelope<TDetails>;

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
  /** 可执行的下一步建议。错误响应始终携带该数组。 */
  nextActions: string[];
  /** 是否为瞬时错误(可重试)。非瞬时错误为 false。 */
  transient: boolean;
  /** 建议重试间隔(毫秒)。非瞬时错误为 0。 */
  retryAfterMs: number;
}

// ─── 请求 ────────────────────────────────────────────────────────────────

/**
 * CLI → Bridge 的请求信封。command 字符串是天然 kind/discriminator,仅用于路由;
 * 字段取用走反序列化好的 DTO(废除 BridgeRequest 的正则刮 JSON)。
 */
export interface BridgeExecution {
  dryRun?: boolean;
  timeoutMs?: number;
}

export interface BridgeSecurity {
  confirmed: boolean;
  toolName: string;
  descriptorFingerprint: string;
  inputSha256: string;
}

export interface BridgeRequest<TInput = unknown> {
  /** 命令名，仅用于路由。 */
  command: string;
  /** 请求追踪 ID。 */
  requestId: string;
  /** 命令输入，缺省时按空对象处理。 */
  input?: TInput;
  /** 与业务输入分离的执行控制。 */
  execution?: BridgeExecution;
  /** 绑定工具 descriptor 与输入内容的确认凭证。 */
  security?: BridgeSecurity;
}
