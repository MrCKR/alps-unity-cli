import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const VFX_TOOLS: ToolMetadata[] = [
  {
    name: "vfx.graph",
    summary: "Get info on a Visual Effect graph asset, optionally create one.",
    description: "Get info on a Visual Effect graph asset, optionally create one. Requires the Visual Effect Graph package (com.unity.visualeffectgraph) to be installed in the target project.",
    group: "vfx",
    inputSchema: objSchema({ path: { type: "string" }, action: { type: "string", enum: ["info", "create"] }, create: { type: "boolean" }, overwrite: { type: "boolean" } }, ["path"]),
    destructiveHint: false,
    requiresPackages: ["com.unity.visualeffectgraph"]
  },
  {
    name: "vfx.effect",
    summary: "Manage a Visual Effect instance on a GameObject.",
    description: "Manage a Visual Effect instance on a GameObject. Requires the Visual Effect Graph package (com.unity.visualeffectgraph) to be installed in the target project.",
    group: "vfx",
    inputSchema: objSchema({ target: { type: "string" }, graph: { type: "string" }, add: { type: "boolean" } }, ["target"]),
    destructiveHint: false,
    requiresPackages: ["com.unity.visualeffectgraph"]
  }
];
