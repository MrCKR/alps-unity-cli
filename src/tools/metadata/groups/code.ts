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
    destructiveHint: true,
    supportsDryRun: true,
    longRunning: true,
    handler: "hybrid",
    cliCommand: "tools run code.exec",
    cancellationSemantics: "Live channel cannot be externally interrupted once Unity starts executing user code; batchmode/job channel can be cancelled with job cancel --id <jobId>, which attempts process-tree termination and records cancellation details.",
    docsTopic: "code",
    examples: [
      { description: "Execute a code file", argv: ["tools", "run", "code.exec", "--file", "script.cs"] },
      { description: "Execute inline code", argv: ["tools", "run", "code.exec", "--code", "Debug.Log(\"hello\");"] }
    ]
  }
];
