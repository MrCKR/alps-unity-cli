import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const SCENE_TOOLS: ToolMetadata[] = [
  {
    name: "scene.hierarchy",
    title: "Scene Hierarchy",
    description: "List the root GameObjects in the active scene with optional pagination.",
    group: "scene",
    inputSchema: objSchema({ pageSize: { type: "integer", default: 100 }, cursor: { type: "string" } }),
    outputSchema: pageSchema(objSchema({ name: { type: "string" }, path: { type: "string" }, childCount: { type: "integer" } })),
    readOnlyHint: true,
    destructiveHint: false,
    requiresMainThread: true,
    handler: "bridge",
    cliCommand: "scene hierarchy",
    docsTopic: "scene",
    examples: [
      { description: "List all root objects", argv: ["scene", "hierarchy"] },
      { description: "Paginate with cursor", argv: ["scene", "hierarchy", "--page-size", "50", "--cursor", "0"] }
    ]
  },
  {
    name: "scene.active",
    title: "Active Scene Info",
    description: "Return name, path and build index of the currently active scene.",
    group: "scene",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ name: { type: "string" }, path: { type: "string" }, buildIndex: { type: "integer" } }),
    readOnlyHint: true,
    destructiveHint: false,
    requiresMainThread: true,
    handler: "bridge",
    cliCommand: "scene active",
    docsTopic: "scene"
  },
  {
    name: "scene.build-settings",
    title: "Build Settings",
    description: "List scenes in the Editor build settings.",
    group: "scene",
    inputSchema: objSchema({}),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "scene build-settings",
    docsTopic: "scene"
  },
  {
    name: "scene.screenshot",
    title: "Scene Screenshot",
    description: "Capture a screenshot of the active scene viewport and save to a file.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string" }, width: { type: "integer", default: 1280 }, height: { type: "integer", default: 720 }, format: { type: "string", enum: ["png", "jpg"], default: "png" }, overwrite: { type: "boolean" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "scene screenshot",
    docsTopic: "scene",
    examples: [
      { description: "Capture PNG screenshot", argv: ["scene", "screenshot", "shot.png"] }
    ]
  },
  {
    name: "scene.create",
    title: "Create Scene",
    description: "Create a new empty scene.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string" }, name: { type: "string" } }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "scene create",
    docsTopic: "scene"
  },
  {
    name: "scene.load",
    title: "Load Scene",
    description: "Load a scene by path or build index.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string" }, name: { type: "string" } }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "scene load",
    docsTopic: "scene"
  },
  {
    name: "scene.save",
    title: "Save Scene",
    description: "Save the active scene.",
    group: "scene",
    inputSchema: objSchema({ path: { type: "string" }, name: { type: "string" } }),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "bridge",
    cliCommand: "scene save",
    docsTopic: "scene"
  }
];
