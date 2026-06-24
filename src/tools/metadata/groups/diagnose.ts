import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const DIAGNOSE_TOOLS: ToolMetadata[] = [
  {
    name: "diagnose.preflight",
    title: "Preflight Diagnostics",
    description: "Run a quick health check on the Unity project and bridge setup.",
    group: "diagnose",
    inputSchema: objSchema({}),
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run diagnose.preflight",
    docsTopic: "diagnose"
  }
];
