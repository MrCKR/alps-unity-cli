import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const PROBUILDER_TOOLS: ToolMetadata[] = [
  {
    name: "probuilder.shape",
    title: "ProBuilder Shape",
    description: "Create a ProBuilder primitive shape in the scene. Requires the ProBuilder package (com.unity.probuilder) to be installed in the target project.",
    group: "probuilder",
    inputSchema: objSchema({ shape: { type: "string", enum: ["cube", "sphere", "cylinder", "plane", "capsule"] }, name: { type: "string" }, parent: { type: "string" } }, ["shape"]),
    readOnlyHint: false,
    destructiveHint: false,
    longRunning: true,
    handler: "hybrid",
    cliCommand: "probuilder shape",
    cancellationSemantics: "Live bridge execution is cooperative and usually short; --job execution can be cancelled with job cancel --id <jobId> using immediate Unity batchmode process termination semantics.",
    docsTopic: "probuilder",
    requiresPackages: ["com.unity.probuilder"],
    examples: [
      { description: "Create a ProBuilder cube", argv: ["probuilder", "shape", "cube"] }
    ]
  },
  {
    name: "probuilder.create",
    title: "ProBuilder Create",
    description: "Create a ProBuilder mesh with a specified shape. Requires the ProBuilder package (com.unity.probuilder) to be installed in the target project.",
    group: "probuilder",
    inputSchema: objSchema({ shape: { type: "string" }, name: { type: "string" } }, ["shape"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "probuilder create",
    cancellationSemantics: "Live bridge execution is cooperative and usually short; --job execution can be cancelled with job cancel --id <jobId> using immediate Unity batchmode process termination semantics.",
    docsTopic: "probuilder",
    requiresPackages: ["com.unity.probuilder"]
  },
  {
    name: "probuilder.edit",
    title: "ProBuilder Edit",
    description: "Edit a ProBuilder mesh element with an operation. Requires the ProBuilder package (com.unity.probuilder) to be installed in the target project.",
    group: "probuilder",
    inputSchema: objSchema({ target: { type: "string" }, operation: { type: "string" }, value: { type: "string" } }, ["target", "operation"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "probuilder edit",
    cancellationSemantics: "Live bridge execution is cooperative and usually short; --job execution can be cancelled with job cancel --id <jobId> using immediate Unity batchmode process termination semantics.",
    docsTopic: "probuilder",
    requiresPackages: ["com.unity.probuilder"]
  },
  {
    name: "probuilder.csg",
    title: "ProBuilder CSG",
    description: "Boolean CSG operation between two ProBuilder meshes. Requires the ProBuilder package (com.unity.probuilder) to be installed in the target project.",
    group: "probuilder",
    inputSchema: objSchema({ target: { type: "string" }, source: { type: "string" }, operation: { type: "string", enum: ["union", "subtract", "intersect"] } }, ["target", "source", "operation"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "hybrid",
    cliCommand: "probuilder csg",
    cancellationSemantics: "Live bridge execution is cooperative and may leave scene changes if already applied; --job execution can be cancelled with job cancel --id <jobId> using immediate Unity batchmode process termination semantics.",
    docsTopic: "probuilder",
    requiresPackages: ["com.unity.probuilder"]
  }
];
