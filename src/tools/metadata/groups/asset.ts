import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const ASSET_TOOLS: ToolMetadata[] = [
  {
    name: "asset.search",
    title: "Search Assets",
    description: "Search the project asset database by name or filter.",
    group: "asset",
    inputSchema: objSchema({ query: { type: "string" }, filter: { type: "string" } }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "asset search",
    docsTopic: "asset"
  },
  {
    name: "asset.info",
    title: "Asset Info",
    description: "Get metadata and path information for an asset.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "asset info",
    docsTopic: "asset"
  },
  {
    name: "asset.create",
    title: "Create Asset",
    description: "Create a folder or text asset inside the Assets directory.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string" }, type: { type: "string", enum: ["file", "folder"] }, content: { type: "string" }, overwrite: { type: "boolean" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "asset create",
    docsTopic: "asset"
  },
  {
    name: "asset.import",
    title: "Import Asset",
    description: "Import an external file or supplied content into the Unity asset database.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string" }, source: { type: "string" }, content: { type: "string" }, overwrite: { type: "boolean" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "asset import",
    docsTopic: "asset"
  },
  {
    name: "asset.modify",
    title: "Modify Asset",
    description: "Replace text-like asset file content and reimport it.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string" }, content: { type: "string" } }, ["path", "content"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "asset modify",
    docsTopic: "asset"
  },
  {
    name: "asset.delete",
    title: "Delete Asset",
    description: "Delete an asset permanently. Irreversible.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "bridge",
    cliCommand: "asset delete",
    docsTopic: "asset"
  },
  {
    name: "asset.duplicate",
    title: "Duplicate Asset",
    description: "Copy an existing asset to a new Assets/* path.",
    group: "asset",
    inputSchema: objSchema({ source: { type: "string" }, destination: { type: "string" } }, ["source", "destination"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "asset duplicate",
    docsTopic: "asset"
  },
  {
    name: "asset.move",
    title: "Move Asset",
    description: "Move an asset from one path to another.",
    group: "asset",
    inputSchema: objSchema({ source: { type: "string" }, destination: { type: "string" } }, ["source", "destination"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "asset move",
    docsTopic: "asset"
  },
  {
    name: "asset.rename",
    title: "Rename Asset",
    description: "Rename an asset within its current folder.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string" }, name: { type: "string" } }, ["path", "name"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "asset rename",
    docsTopic: "asset"
  }
];
