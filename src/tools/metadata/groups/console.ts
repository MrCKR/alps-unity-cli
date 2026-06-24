import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

/**
 * Console 域：拆分 editor.console 为独立 console.get/clear。
 * editor.console 保留为兼容 alias。
 */
export const CONSOLE_TOOLS: ToolMetadata[] = [
  {
    name: "console.get",
    title: "Get Console Logs",
    description: "Read Unity Editor console logs with optional type filter, search and pagination. Supports filter/page/stacktrace.",
    group: "console",
    inputSchema: objSchema({ type: { type: "string", enum: ["error", "warning", "log", "all"], default: "all" }, filter: { type: "string" }, search: { type: "string" }, count: { type: "integer", default: 50 } }),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run console.get",
    docsTopic: "console",
    equivalentUnityMcp: "console.get"
  },
  {
    name: "console.clear",
    title: "Clear Console",
    description: "Clear the Unity Editor console. Writes console state.",
    group: "console",
    inputSchema: objSchema({}),
    destructiveHint: true,
    supportsDryRun: true,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run console.clear",
    docsTopic: "console",
    equivalentUnityMcp: "console.clear"
  }
];
