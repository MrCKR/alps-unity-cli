import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

const scriptEditSchema = objSchema({
  path: { type: "string" },
  preconditionSha256: { type: "string", description: "Expected SHA-256 of the current script content before applying edits." },
  edits: {
    type: "array",
    items: {
      type: "object",
      properties: {
        kind: { type: "string", enum: ["text", "anchor", "class", "method"] },
        oldText: { type: "string" },
        newText: { type: "string" },
        anchor: { type: "string" },
        position: { type: "string", enum: ["before", "after", "replace"] },
        name: { type: "string" },
        content: { type: "string" },
        action: { type: "string", enum: ["insert", "replace", "delete"] }
      },
      required: ["kind"]
    }
  },
  find: { type: "string" },
  replace: { type: "string" },
  all: { type: "boolean" }
}, ["path"]);

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
    supportStatus: "live-bridge",
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
    supportStatus: "live-bridge",
    cliCommand: "script read",
    docsTopic: "script"
  },
  {
    name: "script.hash",
    title: "Hash Script",
    description: "Return the SHA-256 hash for a C# script file so later writes can use preconditionSha256.",
    group: "script",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "script hash",
    docsTopic: "script",
    equivalentUnityMcp: "get_sha"
  },
  {
    name: "script.edit",
    title: "Edit Script",
    description: "Apply targeted edits to a C# script file, optionally guarded by preconditionSha256.",
    group: "script",
    inputSchema: scriptEditSchema,
    readOnlyHint: false,
    destructiveHint: true,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "script edit",
    docsTopic: "script",
    equivalentUnityMcp: "apply_text_edits"
  },
  {
    name: "script.apply-edits",
    title: "Apply Script Edits",
    description: "Apply structured text, anchor, class, or method edits to a C# script with an optional SHA-256 precondition.",
    group: "script",
    inputSchema: scriptEditSchema,
    readOnlyHint: false,
    destructiveHint: true,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "script apply-edits",
    docsTopic: "script",
    equivalentUnityMcp: "script_apply_edits"
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
    supportStatus: "live-bridge",
    cliCommand: "script validate",
    docsTopic: "script"
  }
];
