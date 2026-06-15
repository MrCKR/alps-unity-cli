import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const CAMERA_TOOLS: ToolMetadata[] = [
  {
    name: "camera.list",
    title: "Camera List",
    description: "List all cameras in the active scene.",
    group: "camera",
    inputSchema: objSchema({}),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "camera list",
    docsTopic: "camera"
  },
  {
    name: "camera.info",
    title: "Camera Info",
    description: "Get position, rotation, FOV and other properties of a camera.",
    group: "camera",
    inputSchema: objSchema({ target: { type: "string" } }, ["target"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "camera info",
    docsTopic: "camera",
    examples: [
      { description: "Inspect main camera", argv: ["camera", "info", "Main Camera"] }
    ]
  },
  {
    name: "camera.screenshot",
    title: "Camera Screenshot",
    description: "Render a screenshot from a specific camera and save to file.",
    group: "camera",
    inputSchema: objSchema({ target: { type: "string" }, output: { type: "string" }, width: { type: "integer" }, height: { type: "integer" }, format: { type: "string" }, transparent: { type: "boolean" }, overwrite: { type: "boolean" } }, ["target", "output"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "camera screenshot",
    docsTopic: "camera",
    examples: [
      { description: "Capture from Main Camera", argv: ["camera", "screenshot", "Main Camera", "--output", "cam_shot.png"] }
    ]
  }
];
