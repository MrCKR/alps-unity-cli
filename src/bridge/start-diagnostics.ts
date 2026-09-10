/**
 * Bridge 连接失败时的 Unity 侧根因读取。
 * CLI 与 Unity 完全断连时，Unity 进程内阻止 bridge 启动的根因只会写入项目内
 * Library/UnityCli/UnityCliBridge.log；这里只读该日志尾部，把可行动的根因透出到 CLI 错误，
 * 绝不掩盖或替换原始连接失败错误。
 */

import { open, stat } from "node:fs/promises";
import { join } from "node:path";
import { readRegistryInstanceDiagnostics } from "../instances.js";

/** 只读取日志尾部固定窗口，避免读入不受控增长的历史日志。 */
const bridgeLogTailBytes = 64 * 1024;
const maxDetailLength = 300;
const timestampPattern = /^(\d{4}-\d{2}-\d{2}T[0-9:.]+Z) (.*)$/;

export interface BridgeStartBlockerDetails {
  code: "bridge_start_blocked";
  catalogDetail?: string;
  handoffDetail?: string;
  loggedAtUtc: string;
}

export interface BridgeStartBlockerDiagnostics {
  message: string;
  nextActions: string[];
  details: BridgeStartBlockerDetails;
}

interface BridgeLogLine {
  timestamp: string;
  text: string;
}

async function readBridgeLogTailLines(logPath: string): Promise<string[]> {
  try {
    const info = await stat(logPath);
    if (!info.isFile()) return [];
    const start = Math.max(0, info.size - bridgeLogTailBytes);
    const handle = await open(logPath, "r");
    try {
      const buffer = Buffer.allocUnsafe(info.size - start);
      await handle.read(buffer, 0, buffer.length, start);
      const lines = buffer.toString("utf8").split(/\r?\n/);
      // 窗口起点可能切在半行中间，丢弃首段避免截断行干扰匹配。
      return start > 0 && lines.length > 0 ? lines.slice(1) : lines;
    } finally {
      await handle.close();
    }
  } catch {
    // 诊断读取失败永远不影响原始连接失败错误。
    return [];
  }
}

function findLastLine(lines: string[], needle: string): BridgeLogLine | undefined {
  for (let index = lines.length - 1; index >= 0; index -= 1) {
    const match = timestampPattern.exec(lines[index] ?? "");
    // Unity bridge 日志为 `key ...` 行；只按行首 key 匹配，避免 handoff 行 error= 引用其他 code 时串扰。
    if (match && lineStartsWithKey(match[2]!, needle)) return { timestamp: match[1]!, text: match[2]! };
  }
  return undefined;
}

function lineStartsWithKey(text: string, key: string): boolean {
  return text === key || text.startsWith(`${key} `) || text.startsWith(`${key}:`);
}

/** C# 侧日志行为 `key: detail` 或 `key key=value... error=<detail>`；提取可读根因细节并截断到有界长度。 */
function detailAfterKey(text: string, key: string): string | undefined {
  let detail = lineStartsWithKey(text, key) ? text.slice(text === key ? key.length : key.length + 1) : text;
  const marker = "error=";
  const index = detail.indexOf(marker);
  detail = (index >= 0 ? detail.slice(index + marker.length) : detail).trim();
  if (detail.length === 0) return undefined;
  return detail.length > maxDetailLength ? `${detail.slice(0, maxDetailLength)}…` : detail;
}

/** 读取项目 bridge 日志尾部，提取最近一次 bridge 启动被阻止的根因；无记录或记录已过期时返回 undefined。 */
export async function readBridgeStartBlockerDiagnostics(dataDir: string, instanceId: string): Promise<BridgeStartBlockerDiagnostics | undefined> {
  const instance = await readRegistryInstanceDiagnostics(dataDir, instanceId);
  if (!instance) return undefined;
  const lines = await readBridgeLogTailLines(join(instance.canonicalProjectPath, "Library", "UnityCli", "UnityCliBridge.log"));
  const catalogFailure = findLastLine(lines, "tool_catalog_initialization_failed");
  const startBlocked = findLastLine(lines, "bridge_start_blocked");
  if (!catalogFailure && !startBlocked) return undefined;
  const loggedAtUtc = startBlocked?.timestamp ?? catalogFailure?.timestamp ?? "";
  if (instance.processStartedAtMs !== undefined && loggedAtUtc.length > 0) {
    const loggedAtMs = Date.parse(loggedAtUtc);
    // 旧 Unity 进程留下的 blocked 记录与本次启动无关，不得误导当前连接失败诊断。
    if (Number.isFinite(loggedAtMs) && loggedAtMs < instance.processStartedAtMs - 5_000) return undefined;
  }
  const catalogDetail = catalogFailure ? detailAfterKey(catalogFailure.text, "tool_catalog_initialization_failed") : undefined;
  const handoffDetail = startBlocked ? detailAfterKey(startBlocked.text, "bridge_start_blocked") : undefined;
  const rootDetail = catalogDetail ?? handoffDetail;
  const details: BridgeStartBlockerDetails = {
    code: "bridge_start_blocked",
    ...(catalogDetail ? { catalogDetail } : {}),
    ...(handoffDetail ? { handoffDetail } : {}),
    loggedAtUtc,
  };
  const message = rootDetail
    ? `Unity blocked Bridge startup: ${rootDetail}`
    : "Unity blocked Bridge startup; the root cause is recorded in Library/UnityCli/UnityCliBridge.log.";
  const nextActions = [
    `Run \`unitycli doctor --project "${instance.canonicalProjectPath}"\` for extension and compile diagnostics.`,
    ...(rootDetail && rootDetail.includes("UnityCliExtensionAssembly")
      ? [`Reinstall the affected extension so Unity recompiles it: \`unitycli extensions remove <id>\` then \`unitycli extensions install <id> --project "${instance.canonicalProjectPath}"\`.`]
      : []),
  ];
  return { message, nextActions, details };
}
