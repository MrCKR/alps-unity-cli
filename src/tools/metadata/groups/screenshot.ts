import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const SCREENSHOT_TOOLS: ToolMetadata[] = [
  {
    name: "screenshot.capture",
    title: "Capture Screenshot",
    description: "Capture a game-view screenshot and save to a path.",
    group: "screenshot",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "screenshot capture",
    docsTopic: "screenshot"
  }
];
