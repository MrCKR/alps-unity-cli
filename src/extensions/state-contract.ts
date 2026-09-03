import { open } from "node:fs/promises";
import { TextDecoder } from "node:util";
import type { ExtensionId } from "./types.js";

export const EXTENSION_ID_MAX_UTF8_BYTES = 128;
export const EXTENSION_STATE_MAX_ENTRIES = 256;
export const EXTENSION_STATE_MAX_UTF8_BYTES = 256 * 1024;
export const EXTENSION_DIAGNOSTIC_SAMPLE_LIMIT = 32;

const extensionIdPattern = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*(?![\s\S])/;

/** extension 状态中的 ID 使用统一 kebab-case 语法和单值 UTF-8 上限。 */
export function isValidExtensionId(value: unknown): value is ExtensionId {
  return typeof value === "string"
    && Buffer.byteLength(value, "utf8") <= EXTENSION_ID_MAX_UTF8_BYTES
    && extensionIdPattern.test(value);
}

/** 在固定字节预算内读取状态文件；返回 undefined 仅表示文件不存在。 */
export async function readBoundedExtensionStateFile(path: string): Promise<string | undefined> {
  let handle;
  try {
    handle = await open(path, "r");
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return undefined;
    throw error;
  }

  try {
    const buffer = Buffer.alloc(EXTENSION_STATE_MAX_UTF8_BYTES + 1);
    let total = 0;
    while (total < buffer.length) {
      const { bytesRead } = await handle.read(buffer, total, buffer.length - total, total);
      if (bytesRead === 0) break;
      total += bytesRead;
    }
    if (total > EXTENSION_STATE_MAX_UTF8_BYTES) {
      throw new ExtensionStateContractError(`State file exceeds ${EXTENSION_STATE_MAX_UTF8_BYTES} UTF-8 bytes.`);
    }
    try {
      return new TextDecoder("utf-8", { fatal: true }).decode(buffer.subarray(0, total));
    } catch {
      throw new ExtensionStateContractError("State file is not valid UTF-8.");
    }
  } finally {
    await handle.close();
  }
}

export class ExtensionStateContractError extends Error {}
