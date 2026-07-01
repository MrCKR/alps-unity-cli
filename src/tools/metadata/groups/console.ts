import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

/**
 * Console 域：拆分 editor.console 为独立 console.get/clear。
 * editor.console 保留为兼容 alias。
 */
export const CONSOLE_TOOLS: ToolMetadata[] = [
  {
    name: "console.get",
    summary: "Read Unity Console logs with type/search filters and cursor pagination; preferred over editor.console.",
    description: "Read Unity Editor console logs newest-first with optional type filter, message/path search, count and cursor pagination. Use small count values when checking compile errors; entries include message, file and line but not full stack traces. Use console.clear only when explicitly asked to clear ephemeral Console UI state.",
    group: "console",
    inputSchema: objSchema({
      type: { type: "string", enum: ["error", "warning", "log", "assert", "all"], default: "all", description: "Console entry type to include." },
      filter: { type: "string", description: "Substring filter applied to message or file." },
      search: { type: "string", description: "Alias of filter." },
      count: { type: "integer", default: 50, maximum: 500, description: "Page size, clamped to 1..500." },
      cursor: { type: "integer", default: 0, description: "Zero-based offset into newest-first filtered entries." }
    }),
    outputSchema: objSchema({ command: { type: "string" }, type: { type: "string" }, filter: { type: ["string", "null"] }, items: { type: "array", items: { type: "object" } }, pageInfo: { type: "object" }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Read recent errors", argv: ["tools", "run", "console.get", "--input", "{\"type\":\"error\",\"count\":20}"] },
      { description: "Search recent compile messages", argv: ["tools", "run", "console.get", "--input", "{\"search\":\"CS\",\"count\":20,\"cursor\":0}"] }
    ]
  },
  {
    name: "console.clear",
    summary: "Clear Unity Console UI state; use only when explicitly asked and confirm with --yes.",
    description: "Clear the Unity Editor Console UI state. This is destructive for ephemeral diagnostics: use console.get first, prefer --dry-run to preview, and run with --yes only when old logs are no longer needed.",
    group: "console",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ command: { type: "string" }, dryRun: { type: "boolean" }, cleared: { type: "boolean" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    sideEffects: ["console-clear"],
    examples: [
      { description: "Preview clearing Console", argv: ["tools", "run", "console.clear", "--dry-run"] },
      { description: "Clear Console after reviewing logs", argv: ["tools", "run", "console.clear", "--yes"] }
    ]
  }
];
