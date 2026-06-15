import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const CODE_TOOLS: ToolMetadata[] = [
  {
    name: "code.exec",
    title: "Execute Live Code",
    description: "Execute C# code in the Unity Editor live or batchmode context.",
    group: "code",
    inputSchema: objSchema({
      file: { type: "string" },
      stdin: { type: "boolean" },
      code: { type: "string" },
      channel: { type: "string", enum: ["live", "batchmode"] },
      refs: { type: "array", items: { type: "string" } },
      define: { type: "array", items: { type: "string" } },
      args: { type: "object" },
      timeoutMs: { type: "integer" }
    }),
    readOnlyHint: false,
    destructiveHint: true,
    longRunning: true,
    handler: "hybrid",
    cliCommand: "code exec",
    docsTopic: "code",
    examples: [
      { description: "Execute a code file", argv: ["code", "exec", "--file", "script.cs"] },
      { description: "Execute inline code", argv: ["code", "exec", "--code", "Debug.Log(\"hello\");"] }
    ]
  }
];
