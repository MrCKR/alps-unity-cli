import { Buffer } from "node:buffer";

export const MAX_TOOL_NAME_UTF8_BYTES = 128;

const REGISTERED_TOOL_NAME_PATTERN = /^(?:[a-z][a-z0-9-]*|UnityPipeline_[a-z][a-z0-9-]*)(?:\.[a-z0-9][a-z0-9-]*)*$/;

/** 所有 Tool owner 共用同一 ASCII 语法与 UTF-8 byte 上限。 */
export function isRegisteredToolName(value: unknown): value is string {
  return typeof value === "string"
    && Buffer.byteLength(value, "utf8") <= MAX_TOOL_NAME_UTF8_BYTES
    && REGISTERED_TOOL_NAME_PATTERN.test(value);
}
