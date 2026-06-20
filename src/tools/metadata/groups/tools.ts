import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

export const TOOLS_DISCOVERY_TOOLS: ToolMetadata[] = [
  {
    name: "tools.group",
    title: "Tool Groups",
    description: "List the available tool category groups. Primary discovery entry point in the group-first model.",
    group: "tools",
    inputSchema: objSchema({}),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools group",
    docsTopic: "tools"
  },
  {
    name: "tools.group-list",
    title: "List Tools in Group",
    description: "List tools in a specific group with short summaries. Does NOT expand full schema.",
    group: "tools",
    inputSchema: objSchema({
      group: { type: "string" },
      includeCustom: { type: "boolean", default: false }
    }, ["group"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools group list",
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
    description: "Execute a tool by its dotted name using the unified tools contract. This is the primary execution entry point — the router determines the execution channel (bridge, bridge-runtime, job, offline, or hybrid) based on the tool's handler and target descriptor. Runtime target tools require the Editor to be in Play Mode; if not in Play Mode, the CLI returns E_RUNTIME_NOT_READY (transient).",
    group: "tools",
    inputSchema: objSchema({
      name: { type: "string", description: "Dotted tool name, e.g. 'scene.hierarchy', 'sample.echo', 'runtime.echo'" },
      input: { type: "string", description: "JSON input for the tool" },
      inputFile: { type: "string", description: "Path to JSON input file" },
      timeoutMs: { type: "integer", description: "Override default timeout" },
      fingerprintMode: { type: "string", enum: ["block", "warn"], description: "Fingerprint mismatch handling: block (default) or warn" },
      runtime: { type: "boolean", description: "Force runtime execution path for target=both tools" }
    }, ["name"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    supportStatus: "live-bridge",
    cliCommand: "tools run",
    docsTopic: "tool-execution",
    target: "both",
    availabilityScope: "editorSession",
    architectureSensitive: true,
    frozenDecision: "ALP-365-P6",
    coverageScope: ["tools-run", "runtime-target", "play-mode-session"],
    examples: [
      { description: "Run an editor-domain tool", argv: ["tools", "run", "scene.hierarchy", "--target", "my-editor"] },
      { description: "Run a runtime tool (requires Play Mode active)", argv: ["tools", "run", "runtime.echo", "--target", "my-editor"] },
      { description: "Run with input parameters as JSON", argv: ["tools", "run", "camera.list", "--input", "{\"format\": \"text\"}"] },
      { description: "Run with fingerprint warn mode", argv: ["tools", "run", "sample.echo", "--fingerprint-mode", "warn"] },
      { description: "Force runtime path for target=both tool", argv: ["tools", "run", "code.exec", "--runtime", "--target", "my-editor"] }
    ]
  }
];
