import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const MATERIAL_TOOLS: ToolMetadata[] = [
  {
    name: "material.info",
    title: "Material Info",
    description: "Get shader, properties and texture references of a material.",
    group: "material",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run material.info",
    docsTopic: "material"
  },
  {
    name: "material.create",
    title: "Create Material",
    description: "Create a new material asset with a specified shader.",
    group: "material",
    inputSchema: objSchema({ path: { type: "string" }, shader: { type: "string" }, overwrite: { type: "boolean" } }, ["path", "shader"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run material.create",
    docsTopic: "material"
  },
  {
    name: "material.assign",
    title: "Assign Material",
    description: "Assign a material to a GameObject's renderer slot.",
    group: "material",
    inputSchema: objSchema({ target: { type: "string" }, material: { type: "string" }, slot: { type: "integer" } }, ["target", "material"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run material.assign",
    docsTopic: "material"
  },
  {
    name: "material.set",
    title: "Set Material Property",
    description: "Set a named property on a material (color, float, vector, texture).",
    group: "material",
    inputSchema: objSchema({ path: { type: "string" }, property: { type: "string" }, value: { type: "string" }, type: { type: "string", enum: ["color", "float", "vector", "texture"] } }, ["path", "property", "value"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run material.set",
    docsTopic: "material"
  }
];
