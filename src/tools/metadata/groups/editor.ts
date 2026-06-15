import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const EDITOR_TOOLS: ToolMetadata[] = [
  {
    name: "editor.console",
    title: "Editor Console Logs",
    description: "Read recent log entries from the Unity Editor console.",
    group: "editor",
    inputSchema: objSchema({}),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "editor console",
    docsTopic: "editor"
  },
  {
    name: "editor.state",
    title: "Editor State",
    description: "Query the current Editor state (play mode, pause, etc.).",
    group: "editor",
    inputSchema: objSchema({}),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "editor state",
    docsTopic: "editor"
  },
  {
    name: "editor.play",
    title: "Enter Play Mode",
    description: "Start the Unity Editor play mode.",
    group: "editor",
    inputSchema: objSchema({}),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "editor play",
    docsTopic: "editor"
  },
  {
    name: "editor.pause",
    title: "Pause Play Mode",
    description: "Pause the currently running play mode.",
    group: "editor",
    inputSchema: objSchema({}),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "editor pause",
    docsTopic: "editor"
  },
  {
    name: "editor.stop",
    title: "Stop Play Mode",
    description: "Stop play mode and return to edit mode.",
    group: "editor",
    inputSchema: objSchema({}),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "editor stop",
    docsTopic: "editor"
  },
  {
    name: "editor.menu",
    title: "Execute Editor Menu",
    description: "Invoke a Unity Editor menu item by path.",
    group: "editor",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "bridge",
    cliCommand: "editor menu",
    docsTopic: "editor"
  },
  {
    name: "editor.refresh",
    title: "Refresh Editor Assets",
    description: "Trigger asset refresh in the Unity Editor, optionally recompiling scripts. Runs as a Unity batch job.",
    group: "editor",
    inputSchema: objSchema({
      compile: { type: "boolean", default: false },
      project: { type: "string" },
      unity: { type: "string" },
      wait: { type: "boolean", default: true }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    longRunning: true,
    handler: "job",
    cliCommand: "editor refresh",
    docsTopic: "editor",
    examples: [
      { description: "Refresh assets without recompile", argv: ["editor", "refresh"] },
      { description: "Refresh and recompile, then wait", argv: ["editor", "refresh", "--compile", "--wait"] }
    ]
  }
];
