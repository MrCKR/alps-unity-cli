import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const SCREENSHOT_TOOLS: ToolMetadata[] = [
  {
    name: "screenshot.capture",
    title: "Capture Screenshot",
    description: "Capture a game-view screenshot and save to a path.",
    group: "screenshot",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run screenshot.capture",
    docsTopic: "screenshot"
  },
  {
    name: "screenshot.game-view",
    title: "Game View Screenshot",
    description: "Capture the Game View by rendering all active cameras. Optionally include rendering diagnostics. Merges graphics.screenshot diagnostics capability.",
    group: "screenshot",
    inputSchema: objSchema({ path: { type: "string" }, width: { type: "integer", default: 1280 }, height: { type: "integer", default: 720 }, overwrite: { type: "boolean" }, includeDiagnostics: { type: "boolean" } }, ["path"]),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run screenshot.game-view",
    docsTopic: "screenshot",
    equivalentUnityMcp: "screenshot.game-view"
  },
  {
    name: "screenshot.scene-view",
    title: "Scene View Screenshot",
    description: "Capture the Editor SceneView camera. Aligns with IvanMurzak scene-view screenshot.",
    group: "screenshot",
    inputSchema: objSchema({ path: { type: "string" }, width: { type: "integer" }, height: { type: "integer" }, format: { type: "string", enum: ["png", "jpg"], default: "png" }, overwrite: { type: "boolean" } }, ["path"]),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run screenshot.scene-view",
    docsTopic: "screenshot",
    equivalentUnityMcp: "screenshot.scene-view"
  },
  {
    name: "screenshot.isolated-object",
    title: "Isolated Object Screenshot",
    description: "Render a single GameObject in isolation with a temporary camera. Aligns with IvanMurzak isolated render.",
    group: "screenshot",
    inputSchema: objSchema({ target: { type: "string" }, path: { type: "string" }, width: { type: "integer", default: 512 }, height: { type: "integer", default: 512 }, orthographic: { type: "boolean", default: true }, size: { type: "number", default: 2 }, overwrite: { type: "boolean" } }, ["target", "path"]),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run screenshot.isolated-object",
    docsTopic: "screenshot",
    equivalentUnityMcp: "screenshot.isolated-object"
  },
  {
    name: "screenshot.orbit",
    title: "Orbit Screenshot",
    description: "Capture multiple screenshots orbiting a target GameObject at evenly spaced angles. Aligns with CoplayDev orbit/surround.",
    group: "screenshot",
    inputSchema: objSchema({ target: { type: "string" }, "output-dir": { type: "string" }, count: { type: "integer", default: 8 }, radius: { type: "number", default: 5 }, width: { type: "integer", default: 512 }, height: { type: "integer", default: 512 } }, ["target"]),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run screenshot.orbit",
    docsTopic: "screenshot",
    equivalentUnityMcp: "screenshot.orbit"
  }
];
