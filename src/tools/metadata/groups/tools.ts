import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

export const TOOLS_DISCOVERY_TOOLS: ToolMetadata[] = [
  {
    name: "tools.list",
    title: "List Tools",
    description: "List all available Unity CLI tools, optionally filtered by group, with optional custom/project tool discovery.",
    group: "tools",
    inputSchema: objSchema({
      group: { type: "string" },
      includeCustom: { type: "boolean", default: false }
    }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools list",
    docsTopic: "tools"
  },
  {
    name: "tools.groups",
    title: "Tool Groups",
    description: "List the available tool category groups.",
    group: "tools",
    inputSchema: objSchema({}),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools groups",
    docsTopic: "tools"
  },
  {
    name: "tools.search",
    title: "Search Tools",
    description: "Search tools by keyword across name, title, description, and group.",
    group: "tools",
    inputSchema: objSchema({
      keyword: { type: "string" }
    }, ["keyword"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools search",
    docsTopic: "tools"
  },
  {
    name: "tools.describe",
    title: "Describe Tool",
    description: "Show full metadata for a specific tool, including input schema, handler type, and examples.",
    group: "tools",
    inputSchema: objSchema({
      name: { type: "string" }
    }, ["name"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools describe",
    docsTopic: "tools"
  },
  {
    name: "tools.run",
    title: "Run Tool (Unified)",
    description: "Execute a tool by its dotted name using the unified tools contract. Reserved for P3; currently returns E_TOOL_NOT_FOUND_FOR_TARGET. Runtime target tools (e.g. sample.runtime-echo) require the Editor to be in Play Mode and accept --play-mode-session-id and --fingerprint-mode flags.",
    group: "tools",
    inputSchema: objSchema({
      name: { type: "string" },
      playModeSessionId: { type: "string", description: "Session ID for Play Mode runtime tool execution." },
      fingerprintMode: { type: "string", enum: ["strict", "loose", "off"], description: "Fingerprint verification mode for runtime tool execution." },
      timeoutMs: { type: "integer" }
    }, ["name"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "offline",
    supportStatus: "planned",
    cliCommand: "tools run",
    docsTopic: "tools",
    architectureSensitive: true,
    frozenDecision: "ALP-365-P6",
    coverageScope: ["tools-run", "runtime-target", "play-mode-session"],
    examples: [
      { description: "Run a runtime tool in Play Mode", argv: ["tools", "run", "sample.runtime-echo", "--message", "hello", "--play-mode-session-id", "session_001"] },
      { description: "Run a runtime tool with fingerprint verification", argv: ["tools", "run", "runtime.wait", "--fingerprint-mode", "strict"] }
    ]
  }
];
