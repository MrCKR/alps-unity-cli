import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const TEXTURE_TOOLS: ToolMetadata[] = [
  {
    name: "texture.info",
    title: "Texture Info",
    description: "Get dimensions, format and metadata of a texture asset.",
    group: "texture",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "texture info",
    docsTopic: "texture"
  },
  {
    name: "texture.create",
    title: "Create Texture",
    description: "Create a new texture asset.",
    group: "texture",
    inputSchema: objSchema({ path: { type: "string" }, width: { type: "integer" }, height: { type: "integer" }, format: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "texture create",
    docsTopic: "texture"
  },
  {
    name: "texture.sprite",
    title: "Texture Sprite",
    description: "Create or update a sprite from a texture.",
    group: "texture",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "texture sprite",
    docsTopic: "texture"
  },
  {
    name: "texture.import-settings",
    title: "Texture Import Settings",
    description: "Get or set import settings for a texture asset.",
    group: "texture",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "texture import-settings",
    docsTopic: "texture"
  }
];
