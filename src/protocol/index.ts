/**
 * 传输层协议模块入口。
 *
 * 这些类型是 CLI ↔ Bridge 传输层的唯一权威源，通过 scripts/gen-protocol.mjs
 * 单向生成 C# DTO；Tool 和 Context 业务 DTO 由各自能力定义持有。
 */
export type {
  BridgeExecution,
  BridgeRequest,
  BridgeSecurity,
  Envelope,
  ErrorEnvelope,
  SuccessEnvelope,
  ToolError
} from "./envelope.js";
