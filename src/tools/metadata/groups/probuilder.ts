import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const PROBUILDER_TOOLS: ToolMetadata[] = [
  {
    name: "probuilder.shape",
    title: "ProBuilder Shape",
    description: "Create a ProBuilder primitive shape in the scene.",
    group: "probuilder",
    inputSchema: objSchema({ shape: { type: "string", enum: ["cube", "sphere", "cylinder", "plane", "capsule"] }, name: { type: "string" }, parent: { type: "string" } }, ["shape"]),
    readOnlyHint: false,
    destructiveHint: false,
    longRunning: true,
    handler: "hybrid",
    cliCommand: "probuilder shape",
    docsTopic: "probuilder",
    examples: [
      { description: "Create a ProBuilder cube", argv: ["probuilder", "shape", "cube"] }
    ]
  },
  {
    name: "probuilder.create",
    title: "ProBuilder Create",
    description: "Create a ProBuilder mesh with a specified shape.",
    group: "probuilder",
    inputSchema: objSchema({ shape: { type: "string" }, name: { type: "string" } }, ["shape"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "probuilder create",
    docsTopic: "probuilder"
  },
  {
    name: "probuilder.edit",
    title: "ProBuilder Edit",
    description: "Edit a ProBuilder mesh element with an operation.",
    group: "probuilder",
    inputSchema: objSchema({ target: { type: "string" }, operation: { type: "string" }, value: { type: "string" } }, ["target", "operation"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "probuilder edit",
    docsTopic: "probuilder"
  },
  {
    name: "probuilder.csg",
    title: "ProBuilder CSG",
    description: "Boolean CSG operation between two ProBuilder meshes.",
    group: "probuilder",
    inputSchema: objSchema({ target: { type: "string" }, source: { type: "string" }, operation: { type: "string", enum: ["union", "subtract", "intersect"] } }, ["target", "source", "operation"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "hybrid",
    cliCommand: "probuilder csg",
    docsTopic: "probuilder"
  }
];
