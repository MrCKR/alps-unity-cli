import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

export const DIAGNOSE_TOOLS: ToolMetadata[] = [
  {
    name: "diagnose.preflight",
    title: "Preflight Diagnostics",
    description: "Run a live Unity Editor health check for project path, editor busy state, bridge capabilities, custom tool/context fingerprints, and recoverable checks. Use this before live bridge work when Unity may be compiling, importing, or using stale custom tool/context metadata.",
    group: "diagnose",
    inputSchema: objSchema({ command: { type: "string", description: "Optional target command name to echo in diagnostics, useful when preflighting a specific tool." } }),
    outputSchema: objSchema({ command: { type: "string" }, targetCommand: { type: ["string", "null"] }, project: { type: "object" }, editor: { type: "object" }, bridge: { type: "object" }, customTools: { type: "object" }, customContexts: { type: "object" }, checks: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run diagnose.preflight",
    docsTopic: "diagnose",
    examples: [
      { description: "Run live preflight", argv: ["tools", "run", "diagnose.preflight", "--input", "{}"] },
      { description: "Preflight before component work", argv: ["tools", "run", "diagnose.preflight", "--input", "{\"command\":\"component.add\"}"] }
    ],
    equivalentUnityMcp: "manage_tools / debug_request_context"
  }
];
