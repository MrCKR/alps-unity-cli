/**
 * 传输层协议模块入口。
 *
 * 这些类型是 CLI ↔ Bridge 传输层的唯一权威源头,通过 scripts/gen-protocol.mjs
 * 单向生成 C# DTO。工具业务 DTO 不在此处定义(各回各家,见 .plan/工具统一形态.md)。
 */
export type { Envelope, ToolError, BridgeRequest } from "./envelope.js";
