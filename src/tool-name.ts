import { Buffer } from "node:buffer";

export const MAX_TOOL_NAME_UTF8_BYTES = 128;

export type RegisteredToolNameProfile = "native" | "official-attribute";

const NATIVE_TOOL_NAME_PATTERN = /^[a-z][a-z0-9-]*(?:\.[a-z0-9][a-z0-9-]*)*(?![\s\S])/;
const OFFICIAL_ATTRIBUTE_PREFIX = "UnityPipeline_";
const OFFICIAL_ATTRIBUTE_SUFFIX_PATTERN = /^[a-z][a-z0-9_-]*(?:\.[a-z0-9][a-z0-9_-]*)*(?![\s\S])/;

/** Tool identity 先按 profile 校验精确语法，再共享同一 UTF-8 byte 上限。 */
export function isRegisteredToolName(value: unknown, profile: RegisteredToolNameProfile = "native"): value is string {
  if (typeof value !== "string" || Buffer.byteLength(value, "utf8") > MAX_TOOL_NAME_UTF8_BYTES) return false;
  if (profile === "native") return NATIVE_TOOL_NAME_PATTERN.test(value);
  return value.startsWith(OFFICIAL_ATTRIBUTE_PREFIX)
    && OFFICIAL_ATTRIBUTE_SUFFIX_PATTERN.test(value.slice(OFFICIAL_ATTRIBUTE_PREFIX.length));
}
