import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const CODE_TOOLS: ToolMetadata[] = [
  {
    name: "code.exec",
    summary: "Execute trusted C# code in the Unity Editor live bridge or a Unity batchmode job.",
    description: "Execute trusted C# code in the Unity Editor live bridge or a Unity batchmode job. This is high risk: code can modify scenes/assets, call Unity APIs, write artifacts, hang the Editor, or trigger import/compile. Use --dry-run first to inspect source hash/channel/artifactDir; real execution requires --yes.",
    group: "code",
    inputSchema: objSchema({
      file: { type: "string", description: "UTF-8 C# source file to execute. Exactly one of file, stdin, or code is required." },
      stdin: { type: "boolean", description: "Read C# source from stdin. Exactly one of file, stdin, or code is required." },
      code: { type: "string", description: "Inline trusted C# source. Exactly one of file, stdin, or code is required." },
      channel: { type: "string", enum: ["live", "batchmode"], default: "live", description: "live executes through the open Unity Editor bridge; batchmode starts a long-running Unity job." },
      refs: { type: "array", items: { type: "string" }, description: "Optional extra assembly reference paths." },
      define: { type: "array", items: { type: "string" }, description: "Optional compiler define symbols." },
      args: { type: "object", description: "JSON object passed to the executed code." },
      timeoutMs: { type: "integer", description: "Execution timeout; live code cannot always be externally interrupted after Unity starts it." }
    }),
    outputSchema: objSchema({
      command: { type: "string" },
      dryRun: { type: "boolean" },
      preview: { type: "object", description: "Dry-run source hash, channel, timeout, project path, artifact directory and args." },
      plannedEffects: { type: "array", items: { type: "object" } },
      artifactDir: { type: "string" },
      manifestPath: { type: "string" },
      status: { type: "string" },
      jobId: { type: "string" }
    }),
    destructiveHint: true,
    supportsDryRun: true,
    sideEffects: ["user-code-execution", "artifact-write", "asset-import", "scene-modify", "compile"],
    playModeSupport: "session-affecting",
    permissions: ["execute-user-code", "write-artifacts", "unity-editor-api"],
    target: "editor",
    examples: [
      { description: "Dry-run inline code before execution", argv: ["tools", "run", "code.exec", "--code", "Debug.Log(\"hello\");", "--dry-run"] },
      { description: "Execute a trusted code file after review", argv: ["tools", "run", "code.exec", "--file", "script.cs", "--yes"] },
      { description: "Start a batchmode code job", argv: ["tools", "run", "code.exec", "--file", "script.cs", "--channel", "batchmode", "--project", ".", "--yes"] }
    ]
  }
];
