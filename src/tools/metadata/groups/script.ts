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
    description: "Create a new C# script file at an Assets/*.cs path. Provide content via --content/--content-file; if omitted a minimal MonoBehaviour template is generated from the path/class name. Triggers asset import (and possibly compilation). Use script.update-or-create to overwrite existing files.",
    group: "script",
    inputSchema: objSchema({ path: { type: "string", description: "Destination Assets/*.cs path." }, contentFile: { type: "string", description: "Path to a source file to copy as the script content." }, content: { type: "string", description: "Inline C# source text (alternative to content-file)." } }, ["path"]),
    outputSchema: objSchema({ command: { type: "string" }, script: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import", "session-affecting"],
    riskLevel: "medium",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run script.create",
    docsTopic: "script",
    equivalentUnityMcp: "manage_script.create",
    examples: [
      { description: "Create a script from a content file", argv: ["tools", "run", "script.create", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedProbe.cs\",\"contentFile\":\"./probe.cs\"}"] }
    ]
  },
  {
    name: "script.read",
    title: "Read Script",
    description: "Read a C# script file by Assets-relative path. For large files, pass 1-based lineFrom/lineTo to return only a slice; use script.hash when you only need a precondition hash, and script.find-in-file to locate a symbol before reading a narrow range. In PowerShell, wrap --input JSON in single quotes or use --input-file.",
    group: "script",
    inputSchema: objSchema({
      path: { type: "string", description: "Assets-relative .cs path, e.g. Assets/Scripts/MyBehaviour.cs." },
      lineFrom: { type: "integer", default: 1, description: "Optional 1-based first line to return." },
      lineTo: { type: "integer", default: -1, description: "Optional 1-based last line to return; -1 reads to end." }
    }, ["path"]),
    outputSchema: objSchema({ script: { type: "object" }, content: { type: "string" }, sha256: { type: "string" }, encoding: { type: "string" }, lineFrom: { type: "integer" }, lineTo: { type: "integer" }, lineCount: { type: "integer" }, truncated: { type: "boolean" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run script.read",
    docsTopic: "script",
    equivalentUnityMcp: "manage_script.read / script-read",
    examples: [
      { description: "Read a small line range", argv: ["tools", "run", "script.read", "--input", "{\"path\":\"Assets/Scripts/MyBehaviour.cs\",\"lineFrom\":1,\"lineTo\":80}"] }
    ]
  },
  {
    name: "script.hash",
    title: "Hash Script",
    description: "Return the SHA-256 hash of a C# script file so later writes can use preconditionSha256 to guard against concurrent modification. Read-only.",
    group: "script",
    inputSchema: objSchema({ path: { type: "string", description: "Existing Assets/*.cs path." } }, ["path"]),
    outputSchema: objSchema({ command: { type: "string" }, path: { type: "string" }, sha256: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run script.hash",
    docsTopic: "script",
    equivalentUnityMcp: "get_sha",
    examples: [
      { description: "Get a precondition hash", argv: ["tools", "run", "script.hash", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentScriptFixture.cs\"}"] }
    ]
  },
  {
    name: "script.edit",
    title: "Edit Script",
    description: "Apply find/replace text edits to a C# script file. Pass find/replace (use all=true to replace every occurrence) or an edits array; optionally guard with preconditionSha256 from script.hash. Destructive: it overwrites the file and triggers import/compile. Prefer script.apply-edits for structured method/class edits.",
    group: "script",
    inputSchema: scriptEditSchema,
    destructiveHint: true,
    supportsDryRun: true,
    sideEffects: ["asset-write", "asset-import", "session-affecting"],
    riskLevel: "high",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run script.edit",
    docsTopic: "script",
    equivalentUnityMcp: "apply_text_edits",
    examples: [
      { description: "Replace a symbol with precondition", argv: ["tools", "run", "script.edit", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentScriptFixture.cs\",\"preconditionSha256\":\"<sha>\",\"find\":\"UnityCliAlignment\",\"replace\":\"UnityCliAligned\"}"] }
    ]
  },
  {
    name: "script.apply-edits",
    title: "Apply Script Edits",
    description: "Apply structured text/anchor/class/method edits to a C# script with an optional SHA-256 precondition. Prefer this over raw find/replace for method/class changes — it keeps braces/signatures balanced. Each edit has a kind (text/anchor/class/method) with position (before/after/replace) and action (insert/replace/delete). Destructive: overwrites the file and triggers import/compile.",
    group: "script",
    inputSchema: scriptEditSchema,
    destructiveHint: true,
    supportsDryRun: true,
    sideEffects: ["asset-write", "asset-import", "session-affecting"],
    riskLevel: "high",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run script.apply-edits",
    docsTopic: "script",
    equivalentUnityMcp: "script_apply_edits",
    examples: [
      { description: "Insert a method via anchor edit", argv: ["tools", "run", "script.apply-edits", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentScriptFixture.cs\",\"edits\":[{\"kind\":\"anchor\",\"anchor\":\"class AlignmentScriptFixture\",\"position\":\"after\",\"content\":\"public void ProbeMethod() {}\"}]}\"}]"] }
    ]
  },
  {
    name: "script.validate",
    title: "Validate Script",
    description: "Validate a C# script's structure (balanced braces, basic syntax) without compiling. Read-only; use before script.edit/apply-edits to catch malformed inputs. For compile diagnostics run a real compile via the editor.",
    group: "script",
    inputSchema: objSchema({ path: { type: "string", description: "Existing Assets/*.cs path." } }, ["path"]),
    outputSchema: objSchema({ command: { type: "string" }, path: { type: "string" }, valid: { type: "boolean" }, errors: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run script.validate",
    docsTopic: "script",
    equivalentUnityMcp: "validate_script",
    examples: [
      { description: "Validate a script", argv: ["tools", "run", "script.validate", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentScriptFixture.cs\"}"] }
    ]
  },
  {
    name: "script.update-or-create",
    title: "Update or Create Script",
    description: "Create a script or overwrite it if it exists. Returns a created flag and before/after SHA-256. Supports --dry-run to preview. Use this for idempotent script scaffolding where overwrite is intended; prefer script.edit/apply-edits for targeted changes to an existing file.",
    group: "script",
    inputSchema: objSchema({ path: { type: "string", description: "Destination Assets/*.cs path." }, content: { type: "string", description: "Full C# source content to write." } }, ["path", "content"]),
    outputSchema: objSchema({ command: { type: "string" }, script: { type: "object" }, created: { type: "boolean" }, beforeSha256: { type: "string" }, afterSha256: { type: "string" }, plannedEffects: { type: "array", items: { type: "object" } } }),
    destructiveHint: true,
    supportsDryRun: true,
    sideEffects: ["asset-write", "asset-import", "session-affecting"],
    riskLevel: "high",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run script.update-or-create",
    docsTopic: "script",
    equivalentUnityMcp: "script-update-or-create",
    examples: [
      { description: "Idempotently write a script", argv: ["tools", "run", "script.update-or-create", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedProbe.cs\",\"content\":\"using UnityEngine; public class GeneratedProbe : MonoBehaviour {}\"}"] }
    ]
  },
  {
    name: "script.find-in-file",
    title: "Find in Script File",
    description: "Find text or regex matches in a C# script file and return line numbers with excerpts. Use this before script.read/script.edit when you need to locate a method, field, or anchor without reading the whole file. In PowerShell, wrap --input JSON in single quotes or use --input-file.",
    group: "script",
    inputSchema: objSchema({
      path: { type: "string", description: "Assets-relative .cs path to search." },
      query: { type: "string", description: "Text or regex pattern to find." },
      regex: { type: "boolean", default: false, description: "Treat query as a regular expression." },
      ignoreCase: { type: "boolean", default: true, description: "Case-insensitive matching when true." }
    }, ["path", "query"]),
    outputSchema: objSchema({ command: { type: "string" }, path: { type: "string" }, query: { type: "string" }, count: { type: "integer" }, matches: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run script.find-in-file",
    docsTopic: "script",
    equivalentUnityMcp: "find_in_file",
    examples: [
      { description: "Find a class or method name", argv: ["tools", "run", "script.find-in-file", "--input", "{\"path\":\"Assets/Scripts/MyBehaviour.cs\",\"query\":\"class MyBehaviour\"}"] }
    ]
  },
  {
    name: "script.capabilities",
    title: "Script Capabilities",
    description: "List available script.* and code.exec capabilities with risk levels and descriptions. Read-only; use this to decide which script tool fits a task and the risk involved before calling.",
    group: "script",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ command: { type: "string" }, capabilities: { type: "array", items: objSchema({ tool: { type: "string" }, risk: { type: "string" }, description: { type: "string" } }) }, count: { type: "integer" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run script.capabilities",
    docsTopic: "script",
    equivalentUnityMcp: "manage_script_capabilities"
  }
];
