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
    cliCommand: "tools run script.create",
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
    cliCommand: "tools run script.read",
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
    cliCommand: "tools run script.hash",
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
    cliCommand: "tools run script.edit",
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
    cliCommand: "tools run script.apply-edits",
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
    cliCommand: "tools run script.validate",
    docsTopic: "script"
  },
  {
    name: "script.update-or-create",
    title: "Update or Create Script",
    description: "Create a script or overwrite it if it exists. Returns created flag and before/after SHA-256.",
    group: "script",
    inputSchema: objSchema({ path: { type: "string" }, content: { type: "string" } }, ["path", "content"]),
    readOnlyHint: false,
    destructiveHint: true,
    supportsDryRun: true,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run script.update-or-create",
    docsTopic: "script",
    equivalentUnityMcp: "script-update-or-create"
  },
  {
    name: "script.find-in-file",
    title: "Find in Script File",
    description: "Find text matches in a script file, returning line numbers and text. Supports regex and case sensitivity.",
    group: "script",
    inputSchema: objSchema({ path: { type: "string" }, query: { type: "string" }, regex: { type: "boolean", default: false }, ignoreCase: { type: "boolean", default: true } }, ["path", "query"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run script.find-in-file",
    docsTopic: "script",
    equivalentUnityMcp: "find_in_file"
  },
  {
    name: "script.capabilities",
    title: "Script Capabilities",
    description: "List available script.* and code.exec capabilities with risk levels and descriptions.",
    group: "script",
    inputSchema: objSchema({}),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run script.capabilities",
    docsTopic: "script",
    equivalentUnityMcp: "manage_script_capabilities"
  }
];
