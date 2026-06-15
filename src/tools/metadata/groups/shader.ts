import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const SHADER_TOOLS: ToolMetadata[] = [
  {
    name: "shader.info",
    title: "Shader Info",
    description: "Get properties and passes of a shader asset.",
    group: "shader",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "shader info",
    docsTopic: "shader"
  },
  {
    name: "shader.read",
    title: "Read Shader Source",
    description: "Read the source code of a shader file.",
    group: "shader",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "shader read",
    docsTopic: "shader"
  },
  {
    name: "shader.create",
    title: "Create Shader",
    description: "Create a new shader file.",
    group: "shader",
    inputSchema: objSchema({ path: { type: "string" }, source: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "shader create",
    docsTopic: "shader"
  },
  {
    name: "shader.update",
    title: "Update Shader",
    description: "Update the source code of an existing shader.",
    group: "shader",
    inputSchema: objSchema({ path: { type: "string" }, source: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "shader update",
    docsTopic: "shader"
  }
];
