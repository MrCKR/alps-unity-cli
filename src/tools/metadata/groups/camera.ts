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
  },
  {
    name: "camera.create",
    title: "Create Camera",
    description: "Create a new camera with optional position, FOV, and projection settings.",
    group: "camera",
    inputSchema: objSchema({
      name: { type: "string" },
      parent: { type: "string" },
      position: { type: "string" },
      rotation: { type: "string" },
      fieldOfView: { type: "number" },
      orthographic: { type: "boolean" },
      orthographicSize: { type: "number" },
      clearFlags: { type: "string" },
      depth: { type: "number" },
      tag: { type: "string" }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "camera create",
    docsTopic: "camera",
    examples: [
      { description: "Create a perspective camera", argv: ["camera", "create", "--name", "MyCamera", "--field-of-view", "60"] },
      { description: "Create an orthographic camera", argv: ["camera", "create", "--name", "TopView", "--orthographic", "--orthographic-size", "5"] }
    ]
  },
  {
    name: "camera.target",
    title: "Camera Target",
    description: "Set camera look-at target, position, or rotation.",
    group: "camera",
    inputSchema: objSchema({
      target: { type: "string" },
      lookAt: { type: "string" },
      position: { type: "string" },
      rotation: { type: "string" }
    }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "camera target",
    docsTopic: "camera",
    examples: [
      { description: "Point camera at a target", argv: ["camera", "target", "Main Camera", "--look-at", "Player"] }
    ]
  },
  {
    name: "camera.lens",
    title: "Camera Lens Settings",
    description: "Set camera lens parameters: FOV, projection, clip planes, clear flags, depth.",
    group: "camera",
    inputSchema: objSchema({
      target: { type: "string" },
      fieldOfView: { type: "number" },
      orthographic: { type: "boolean" },
      orthographicSize: { type: "number" },
      nearClipPlane: { type: "number" },
      farClipPlane: { type: "number" },
      clearFlags: { type: "string" },
      depth: { type: "number" }
    }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "camera lens",
    docsTopic: "camera",
    examples: [
      { description: "Set camera FOV", argv: ["camera", "lens", "Main Camera", "--field-of-view", "45"] },
      { description: "Switch to orthographic", argv: ["camera", "lens", "Main Camera", "--orthographic", "--orthographic-size", "10"] }
    ]
  },
  {
    name: "camera.multiview",
    title: "Camera Multiview Screenshot",
    description: "Capture screenshots from multiple cameras simultaneously.",
    group: "camera",
    inputSchema: objSchema({
      targets: { type: "string" },
      output: { type: "string" },
      width: { type: "integer" },
      height: { type: "integer" },
      format: { type: "string" }
    }, ["targets"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "camera multiview",
    docsTopic: "camera",
    examples: [
      { description: "Capture from two cameras", argv: ["camera", "multiview", "--targets", "Main Camera,Top Camera", "--output", "Screenshots"] }
    ]
  }
];
