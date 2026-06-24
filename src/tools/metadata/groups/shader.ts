import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const SHADER_TOOLS: ToolMetadata[] = [
  {
    name: "shader.info",
    title: "Shader Info",
    description: "Get properties and passes of a shader asset.",
    group: "shader",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run shader.info",
    docsTopic: "shader"
  },
  {
    name: "shader.read",
    title: "Read Shader Source",
    description: "Read the source code of a shader file.",
    group: "shader",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run shader.read",
    docsTopic: "shader"
  },
  {
    name: "shader.create",
    title: "Create Shader",
    description: "Create a new shader file.",
    group: "shader",
    inputSchema: objSchema({ path: { type: "string" }, source: { type: "string" } }, ["path"]),
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run shader.create",
    docsTopic: "shader"
  },
  {
    name: "shader.update",
    title: "Update Shader",
    description: "Update the source code of an existing shader.",
    group: "shader",
    inputSchema: objSchema({ path: { type: "string" }, source: { type: "string" } }, ["path"]),
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run shader.update",
    docsTopic: "shader"
  },
  {
    name: "shader.list",
    title: "List Shaders",
    description: "List all shader assets in the project with name, path, property count and render queue.",
    group: "shader",
    inputSchema: objSchema({ keyword: { type: "string" }, pageSize: { type: "integer", default: 100 } }),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run shader.list",
    docsTopic: "shader",
    equivalentUnityMcp: "shader.list"
  },
  {
    name: "shader.get-data",
    title: "Get Shader Data",
    description: "Read shader properties (name, type, flags), render queue and supported state.",
    group: "shader",
    inputSchema: objSchema({ path: { type: "string" }, assetPath: { type: "string" }, name: { type: "string" } }),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run shader.get-data",
    docsTopic: "shader",
    equivalentUnityMcp: "shader.get-data"
  }
];
