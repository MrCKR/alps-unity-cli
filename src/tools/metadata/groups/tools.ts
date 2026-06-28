import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

export const TOOLS_DISCOVERY_TOOLS: ToolMetadata[] = [
  {
    name: "tools.group",
    summary: "List the available tool groups with counts.",
    description: "List the available tool groups with counts. Primary discovery entry point in the group-first model; run this first to find the group containing a capability, then tools group list <group> and tools describe <tool>.",
    group: "tools",
    inputSchema: objSchema({}),
    destructiveHint: false,
    handler: "offline",
    supportStatus: "cli-only",
    playModeSupport: "supported",
    cliCommand: "tools group",
    docsTopic: "tools",
    equivalentUnityMcp: "tools/list-groups",
    examples: [
      { description: "List all tool groups", argv: ["tools", "group"] }
    ]
  },
  {
    name: "tools.group-list",
    summary: "List tools in a specific group with short summaries (name, title, destructiveHint, supportStatus).",
    description: "List tools in a specific group with short summaries (name, title, destructiveHint, supportStatus). Does NOT expand full schema — use tools describe <tool> for the complete input/output schema. Pass includeCustom to include project custom tools.",
    group: "tools",
    inputSchema: objSchema({
      group: { type: "string", description: "Group name from tools.group output." },
      includeCustom: { type: "boolean", default: false, description: "Include project custom [UnityCliTool] tools when targeting a project instance." }
    }, ["group"]),
    destructiveHint: false,
    handler: "offline",
    supportStatus: "cli-only",
    playModeSupport: "supported",
    cliCommand: "tools group list",
    docsTopic: "tools",
    equivalentUnityMcp: "tools/list",
    examples: [
      { description: "List tools in the camera group", argv: ["tools", "group", "list", "camera"] }
    ]
  },
  {
    name: "tools.search",
    summary: "Search tools by keyword across name, title, description, and group.",
    description: "Search tools by keyword across name, title, description, and group. Use this when you do not know the exact group; results are ranked. Then tools describe <tool> for full schema.",
    group: "tools",
    inputSchema: objSchema({
      keyword: { type: "string", description: "Search keyword matched against name/title/description/group." }
    }, ["keyword"]),
    destructiveHint: false,
    handler: "offline",
    supportStatus: "cli-only",
    playModeSupport: "supported",
    cliCommand: "tools search",
    docsTopic: "tools",
    equivalentUnityMcp: "tools/search",
    examples: [
      { description: "Search for hierarchy tools", argv: ["tools", "search", "hierarchy"] }
    ]
  },
  {
    name: "tools.describe",
    summary: "Show full metadata for a specific tool: input schema, output schema, handler, support status, playModeSupport, sideEffects, riskLevel and examples.",
    description: "Show full metadata for a specific tool: input schema, output schema, handler, support status, playModeSupport, sideEffects, riskLevel and examples. This is the primary entry before tools run — read it once to construct a correct one-shot call. Unknown names return E_TOOL_NOT_FOUND_FOR_TARGET.",
    group: "tools",
    inputSchema: objSchema({
      name: { type: "string", description: "Dotted tool name, e.g. 'scene.hierarchy' or 'asset.search'." }
    }, ["name"]),
    destructiveHint: false,
    handler: "offline",
    supportStatus: "cli-only",
    playModeSupport: "supported",
    cliCommand: "tools describe",
    docsTopic: "tools",
    equivalentUnityMcp: "tools/describe",
    examples: [
      { description: "Describe a tool's full schema", argv: ["tools", "describe", "asset.search"] }
    ]
  },
  {
    name: "tools.run",
    summary: "Execute a tool by its dotted name using the unified tools contract.",
    description: "Execute a tool by its dotted name using the unified tools contract. The router determines the execution channel (bridge, runtime, job, offline, or hybrid). Bridge tools are scheduled onto Unity's main thread; if the Editor is compiling, importing, switching Play Mode, or domain reloading, inspect editor.state/waitReady and retry after the busy state clears. The Bridge listener auto-registers after domain reload and reports heartbeat/build stamp through editor.state. Runtime target tools require Play Mode; otherwise the CLI returns transient E_RUNTIME_NOT_READY.",
    group: "tools",
    inputSchema: objSchema({
      name: { type: "string", description: "Dotted tool name, e.g. 'scene.hierarchy', 'sample.echo', 'runtime.echo'" },
      input: { type: "string", description: "JSON input for the tool" },
      inputFile: { type: "string", description: "Path to JSON input file" },
      timeoutMs: { type: "integer", description: "Override default timeout" },
      stdin: { type: "boolean", description: "Read JSON input from standard input for tools that do not already use stdin for a tool-specific payload." },
      fingerprintMode: { type: "string", enum: ["block", "warn"], description: "Fingerprint mismatch handling: block (default) or warn" },
      runtime: { type: "boolean", description: "Force runtime execution path for target=both tools" }
    }, ["name"]),
    destructiveHint: false,
    handler: "hybrid",
    supportStatus: "live-bridge",
    playModeSupport: "supported",
    cliCommand: "tools run",
    docsTopic: "tool-execution",
    target: "both",
    availabilityScope: "editorSession",
    examples: [
      { description: "Run an editor-domain tool", argv: ["tools", "run", "scene.hierarchy", "--target", "my-editor"] },
      { description: "Run a runtime tool (requires Play Mode active)", argv: ["tools", "run", "runtime.echo", "--target", "my-editor"] },
      { description: "Run with input parameters as JSON", argv: ["tools", "run", "camera.list", "--input", "{\"format\": \"text\"}"] },
      { description: "Run with fingerprint warn mode", argv: ["tools", "run", "sample.echo", "--fingerprint-mode", "warn"] },
      { description: "Force runtime path for target=both tool", argv: ["tools", "run", "code.exec", "--runtime", "--target", "my-editor"] }
    ]
  }
];
