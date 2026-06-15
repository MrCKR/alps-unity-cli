import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const SCRIPT_TOOLS: ToolMetadata[] = [
  {
    name: "script.create",
    title: "Create Script",
    description: "Create a new C# script file in the project.",
    group: "script",
    inputSchema: objSchema({ path: { type: "string" }, contentFile: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "planned",
    cliCommand: "script create",
    docsTopic: "script"
  },
  {
    name: "script.read",
    title: "Read Script",
    description: "Read the content of a C# script file.",
    group: "script",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "planned",
    cliCommand: "script read",
    docsTopic: "script"
  },
  {
    name: "script.edit",
    title: "Edit Script",
    description: "Apply targeted edits to a C# script file.",
    group: "script",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "bridge",
    supportStatus: "planned",
    cliCommand: "script edit",
    docsTopic: "script"
  },
  {
    name: "script.validate",
    title: "Validate Script",
    description: "Validate a C# script without compiling.",
    group: "script",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "planned",
    cliCommand: "script validate",
    docsTopic: "script"
  }
];
