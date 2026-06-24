import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const ASSET_TOOLS: ToolMetadata[] = [
  {
    name: "asset.search",
    title: "Search Assets",
    description: "Search the project asset database by name or filter.",
    group: "asset",
    inputSchema: objSchema({ query: { type: "string" }, filter: { type: "string" } }),
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run asset.search",
    docsTopic: "asset"
  },
  {
    name: "asset.info",
    title: "Asset Info",
    description: "Get metadata and path information for an asset.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run asset.info",
    docsTopic: "asset"
  },
  {
    name: "asset.create",
    title: "Create Asset",
    description: "Create a folder or text asset inside the Assets directory.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string" }, type: { type: "string", enum: ["file", "folder"] }, content: { type: "string" }, overwrite: { type: "boolean" } }, ["path"]),
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run asset.create",
    docsTopic: "asset"
  },
  {
    name: "asset.import",
    title: "Import Asset",
    description: "Import an external file or supplied content into the Unity asset database.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string" }, source: { type: "string" }, content: { type: "string" }, overwrite: { type: "boolean" } }, ["path"]),
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run asset.import",
    docsTopic: "asset"
  },
  {
    name: "asset.modify",
    title: "Modify Asset",
    description: "Replace text-like asset file content and reimport it.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string" }, content: { type: "string" } }, ["path", "content"]),
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run asset.modify",
    docsTopic: "asset"
  },
  {
    name: "asset.delete",
    title: "Delete Asset",
    description: "Delete an asset permanently. Irreversible.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    destructiveHint: true,
    supportsDryRun: true,
    handler: "bridge",
    cliCommand: "tools run asset.delete",
    docsTopic: "asset"
  },
  {
    name: "asset.duplicate",
    title: "Duplicate Asset",
    description: "Copy an existing asset to a new Assets/* path.",
    group: "asset",
    inputSchema: objSchema({ source: { type: "string" }, destination: { type: "string" } }, ["source", "destination"]),
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run asset.duplicate",
    docsTopic: "asset"
  },
  {
    name: "asset.move",
    title: "Move Asset",
    description: "Move an asset from one path to another.",
    group: "asset",
    inputSchema: objSchema({ source: { type: "string" }, destination: { type: "string" } }, ["source", "destination"]),
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run asset.move",
    docsTopic: "asset"
  },
  {
    name: "asset.rename",
    title: "Rename Asset",
    description: "Rename an asset within its current folder.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string" }, name: { type: "string" } }, ["path", "name"]),
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run asset.rename",
    docsTopic: "asset"
  },
  {
    name: "asset.search-built-in",
    title: "Search Built-in Assets",
    description: "Search built-in resources (Resources/ and Packages/) by keyword and optional type filter.",
    group: "asset",
    inputSchema: objSchema({ keyword: { type: "string" }, type: { type: "string" }, pageSize: { type: "integer", default: 100 } }),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run asset.search-built-in",
    docsTopic: "asset",
    equivalentUnityMcp: "assets-find-built-in"
  },
  {
    name: "asset.get-data",
    title: "Get Asset Data",
    description: "Deep-serialize an asset's main Unity Object. Asset-friendly wrapper that reuses object.get-data serializer internally.",
    group: "asset",
    inputSchema: objSchema({ assetPath: { type: "string" }, path: { type: "string" }, maxDepth: { type: "integer", default: 4 }, includeFields: { type: "boolean", default: true }, includeProperties: { type: "boolean", default: true }, deepSerialization: { type: "boolean", default: true } }, ["assetPath"]),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run asset.get-data",
    docsTopic: "asset",
    equivalentUnityMcp: "assets-get-data"
  },
  {
    name: "asset.preview",
    title: "Asset Preview",
    description: "Generate/read the asset preview thumbnail metadata (size, dimensions, transient state).",
    group: "asset",
    inputSchema: objSchema({ assetPath: { type: "string" }, path: { type: "string" } }, ["assetPath"]),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run asset.preview",
    docsTopic: "asset",
    equivalentUnityMcp: "manage_asset.generate_preview"
  }
];
