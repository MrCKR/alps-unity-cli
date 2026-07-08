import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

export const TOOLS_DISCOVERY_TOOLS: ToolMetadata[] = [
  {
    name: "tools.group",
    summary: "List the available tool groups with counts.",
    description: "List the available tool groups with counts. Primary discovery entry point in the group-first model; run this first to find the group containing a capability, then tools group list <group> and tools describe <tool>. Pass includeCustom with a target, instance, or bridgeEndpoint to include project custom tool groups.",
    group: "tools",
    inputSchema: objSchema({
      includeCustom: { type: "boolean", default: false, description: "Include project custom IUnityCliTool tools when targeting a project instance." },
      bridgeEndpoint: { type: "string", description: "Bridge endpoint used for project custom tool discovery when includeCustom is true." },
      target: { type: "string", description: "Named Unity target used for project custom tool discovery." },
      instance: { type: "string", description: "Unity instance id used for project custom tool discovery." },
      runtime: { type: "boolean", description: "Force runtime discovery for custom tools when the bridge supports it." }
    }),
    outputSchema: objSchema({
      command: { type: "string" },
      count: { type: "number" },
      groups: {
        type: "array",
        items: objSchema({
          name: { type: "string" },
          summary: { type: "string" },
          toolCount: { type: "number" },
          source: { type: "string" },
          cliCommand: { type: "string" }
        })
      },
      nextActions: { type: "array", items: { type: "string" } }
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "List all tool groups", argv: ["tools", "group"] },
      { description: "List tool groups including project custom tools", argv: ["tools", "run", "tools.group", "--input", "{\"includeCustom\":true,\"bridgeEndpoint\":\"http://127.0.0.1:47395\"}"] }
    ]
  },
  {
    name: "tools.group-list",
    summary: "List tools in a specific group with short routing summaries and support status.",
    description: "List tools in a specific group with short routing summaries, destructiveHint, source, target, playModeSupport, and supportStatus. Does NOT expand full schema; use tools describe <tool> for the complete input/output schema. Pass includeCustom with a target, instance, or bridgeEndpoint to include project custom IUnityCliTool tools.",
    group: "tools",
    inputSchema: objSchema({
      group: { type: "string", description: "Group name from tools.group output." },
      includeCustom: { type: "boolean", default: false, description: "Include project custom IUnityCliTool tools when targeting a project instance." },
      bridgeEndpoint: { type: "string", description: "Bridge endpoint used for project custom tool discovery when includeCustom is true." },
      target: { type: "string", description: "Named Unity target used for project custom tool discovery." },
      instance: { type: "string", description: "Unity instance id used for project custom tool discovery." },
      runtime: { type: "boolean", description: "Force runtime discovery for custom tools when the bridge supports it." }
    }, ["group"]),
    outputSchema: objSchema({
      command: { type: "string" },
      group: { type: "string" },
      count: { type: "number" },
      tools: {
        type: "array",
        items: objSchema({
          name: { type: "string" },
          summary: { type: "string" },
          group: { type: "string" },
          source: { type: "string" },
          destructiveHint: { type: "boolean" },
          playModeSupport: { type: "string" },
          supportStatus: { type: "string" },
          target: { type: "string" },
          requiresPackages: { type: "array", items: { type: "string" } },
          cliCommand: { type: "string" }
        })
      },
      nextActions: { type: "array", items: { type: "string" } }
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "List tools in the camera group", argv: ["tools", "group", "list", "camera"] },
      { description: "List project custom sample tools", argv: ["tools", "run", "tools.group-list", "--input", "{\"group\":\"sample\",\"includeCustom\":true,\"bridgeEndpoint\":\"http://127.0.0.1:47395\"}"] }
    ]
  },
  {
    name: "tools.search",
    summary: "Search tools by keyword across name, summary, description, and group.",
    description: "Search tools by keyword across name, summary, description, and group. Use this when you do not know the exact group; results are ranked. Then tools describe <tool> for full schema. Pass includeCustom with a target, instance, or bridgeEndpoint to include project custom tools from the active Unity project.",
    group: "tools",
    inputSchema: objSchema({
      keyword: { type: "string", description: "Search keyword matched against name/summary/description/group." },
      format: { type: "string", enum: ["json", "text"], default: "json", description: "Output format. json returns machine-readable results; text adds a legacy human-readable text field." },
      includeCustom: { type: "boolean", default: false, description: "Include project custom IUnityCliTool tools when targeting a project instance." },
      bridgeEndpoint: { type: "string", description: "Bridge endpoint used for project custom tool discovery when includeCustom is true." },
      target: { type: "string", description: "Named Unity target used for project custom tool discovery." },
      instance: { type: "string", description: "Unity instance id used for project custom tool discovery." },
      runtime: { type: "boolean", description: "Force runtime discovery for custom tools when the bridge supports it." }
    }, ["keyword"]),
    outputSchema: objSchema({
      command: { type: "string" },
      keyword: { type: "string" },
      count: { type: "number" },
      tools: {
        type: "array",
        items: objSchema({
          name: { type: "string" },
          summary: { type: "string" },
          group: { type: "string" },
          source: { type: "string" },
          destructiveHint: { type: "boolean" },
          playModeSupport: { type: "string" },
          supportStatus: { type: "string" },
          cliCommand: { type: "string" },
          target: { type: "string" },
          availabilityScope: { type: "string" },
          requiresPackages: { type: "array", items: { type: "string" } }
        })
      },
      text: { type: "string" }
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Search for hierarchy tools", argv: ["tools", "search", "hierarchy"] },
      { description: "Search with text output", argv: ["tools", "run", "tools.search", "--input", "{\"keyword\":\"hierarchy\",\"format\":\"text\"}"] }
    ]
  },
  {
    name: "tools.describe",
    summary: "Show full metadata for a specific tool: input schema, output schema, support status, risks, and examples.",
    description: "Show full metadata for a specific tool: input schema, output schema, support status, playModeSupport, sideEffects, riskLevel, and examples. This is the primary entry before tools run — read it once to construct a correct one-shot call. Unknown names return E_TOOL_NOT_FOUND_FOR_TARGET. Pass includeCustom with a target, instance, or bridgeEndpoint to describe project custom tools. Pass project when you need extension package availability for a Unity project.",
    group: "tools",
    inputSchema: objSchema({
      name: { type: "string", description: "Dotted tool name, e.g. 'scene.hierarchy' or 'asset.search'." },
      includeCustom: { type: "boolean", default: false, description: "Include project custom IUnityCliTool tools when targeting a project instance." },
      bridgeEndpoint: { type: "string", description: "Bridge endpoint used for project custom tool discovery when includeCustom is true." },
      target: { type: "string", description: "Named Unity target used for project custom tool discovery." },
      instance: { type: "string", description: "Unity instance id used for project custom tool discovery." },
      runtime: { type: "boolean", description: "Force runtime discovery for custom tools when the bridge supports it." },
      project: { type: "string", description: "Unity project path used to report optional extension package availability." }
    }, ["name"]),
    outputSchema: objSchema({
      command: { type: "string" },
      name: { type: "string" },
      summary: { type: "string" },
      description: { type: "string" },
      group: { type: "string" },
      inputSchema: { type: "object" },
      outputSchema: { type: "object" },
      destructiveHint: { type: "boolean" },
      sideEffects: { type: "array", items: { type: "string" } },
      playModeSupport: { type: "string" },
      supportsDryRun: { type: "boolean" },
      permissions: { type: "array", items: { type: "string" } },
      examples: {
        type: "array",
        items: objSchema({
          description: { type: "string" },
          argv: { type: "array", items: { type: "string" } },
          output: { type: "string" }
        })
      },
      source: { type: "string" },
      riskLevel: { type: "string" },
      longRunning: { type: "boolean" },
      cancellation: { type: "string" },
      cliCommand: { type: "string" },
      target: { type: "string" },
      availabilityScope: { type: "string" },
      supportStatus: { type: "string" },
      requiresPackages: { type: "array", items: { type: "string" } },
      extensionAvailability: { type: "string" },
      extensionId: { type: "string" },
      missingPackageIds: { type: "array", items: { type: "string" } },
      nextActions: { type: "array", items: { type: "string" } }
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Describe a tool's full schema", argv: ["tools", "describe", "asset.search"] },
      { description: "Describe a project custom tool", argv: ["tools", "run", "tools.describe", "--input", "{\"name\":\"sample.echo\",\"includeCustom\":true,\"bridgeEndpoint\":\"http://127.0.0.1:47395\"}"] }
    ]
  },
  {
    name: "tools.run",
    summary: "Execute another tool by dotted name through the unified router.",
    description: "Execute another tool by dotted name through the unified tools router. This wrapper preserves the same routing as direct tools run <name> calls: migrated defineTool handlers, legacy offline handlers, Bridge tools, job handlers, runtime tools, and project custom tools. Put the target tool payload in input as a JSON string or object; outer router fields such as name, target, instance, runtime, job, dryRun, and yes control execution routing and safety. Bridge tools are scheduled onto Unity's main thread; if the Editor is compiling, importing, switching Play Mode, or domain reloading, inspect editor.state/waitReady and retry after the busy state clears. Runtime target tools require Play Mode; otherwise the CLI returns transient E_RUNTIME_NOT_READY.",
    group: "tools",
    inputSchema: objSchema({
      name: { type: "string", description: "Dotted tool name, e.g. 'scene.hierarchy', 'sample.echo', 'runtime.echo'" },
      input: { anyOf: [{ type: "string" }, { type: "object" }], description: "Target tool JSON input as a string or object. This becomes the target tool's --input payload, not the outer tools.run request." },
      inputFile: { type: "string", description: "Path to JSON input file for the target tool." },
      timeoutMs: { type: "integer", description: "Override default timeout" },
      stdin: { type: "boolean", description: "Read JSON input from standard input for tools that do not already use stdin for a tool-specific payload." },
      fingerprintMode: { type: "string", enum: ["block", "warn"], description: "Fingerprint mismatch handling: block (default) or warn" },
      runtime: { type: "boolean", description: "Force runtime execution path for target=both tools" },
      target: { type: "string", description: "Named Unity target or instance alias used for Bridge-backed target tools." },
      instance: { type: "string", description: "Unity instance id or alias used for Bridge-backed target tools." },
      bridgeEndpoint: { type: "string", description: "Explicit Unity Bridge endpoint for target tool execution." },
      project: { type: "string", description: "Unity project path for offline tools or extension availability checks." },
      artifactDir: { type: "string", description: "Artifact directory forwarded to target tools that produce files." },
      job: { type: "boolean", description: "Run target bridge tools as a CLI job when the target descriptor supports job execution." },
      dryRun: { type: "boolean", description: "Forward dry-run mode to target tools that support it; also satisfies destructive confirmation for dry-run previews." },
      yes: { type: "boolean", description: "Forward explicit confirmation to destructive target tools." },
      positionalTarget: { type: "string", description: "Optional single positional argument forwarded to target tools that accept one." }
    }, ["name"]),
    outputSchema: objSchema({
      command: { type: "string", description: "Target tool command name when the target result includes it." }
    }),
    destructiveHint: false,
    supportStatus: "live-bridge",
    playModeSupport: "supported",
    target: "both",
    examples: [
      { description: "Run an editor-domain tool directly", argv: ["tools", "run", "scene.hierarchy", "--target", "my-editor"] },
      { description: "Run tools.run as a wrapper around an offline tool", argv: ["tools", "run", "tools.run", "--input", "{\"name\":\"docs.topic\",\"input\":{\"topic\":\"scene\"}}"] },
      { description: "Run a runtime tool (requires Play Mode active)", argv: ["tools", "run", "runtime.echo", "--target", "my-editor"] },
      { description: "Run with input parameters as JSON", argv: ["tools", "run", "camera.list", "--input", "{\"format\": \"text\"}"] },
      { description: "Run with fingerprint warn mode", argv: ["tools", "run", "sample.echo", "--fingerprint-mode", "warn"] }
    ]
  }
];
