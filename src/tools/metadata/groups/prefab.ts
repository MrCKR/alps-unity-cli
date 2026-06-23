import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const PREFAB_TOOLS: ToolMetadata[] = [
  {
    name: "prefab.info",
    title: "Prefab Info",
    description: "Get information about a prefab asset.",
    group: "prefab",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run prefab.info",
    docsTopic: "prefab"
  },
  {
    name: "prefab.create",
    title: "Create Prefab",
    description: "Create a prefab from a source GameObject.",
    group: "prefab",
    inputSchema: objSchema({ source: { type: "string" }, path: { type: "string" }, overwrite: { type: "boolean" } }, ["source", "path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run prefab.create",
    docsTopic: "prefab",
    examples: [
      { description: "Create prefab from Player", argv: ["tools", "run", "prefab.create", "Player", "--path", "Assets/Prefabs/Player.prefab"] }
    ]
  },
  {
    name: "prefab.modify",
    title: "Modify Prefab",
    description: "Apply small prefab asset edits such as renaming the root or adding/removing direct children.",
    group: "prefab",
    inputSchema: objSchema({ path: { type: "string" }, name: { type: "string" }, addChild: { type: "string" }, removeChild: { type: "string" }, overwrite: { type: "boolean" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run prefab.modify",
    docsTopic: "prefab"
  },
  {
    name: "prefab.stage-open",
    title: "Open Prefab Stage",
    description: "Open a prefab in isolation mode for editing.",
    group: "prefab",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run prefab.stage-open",
    docsTopic: "prefab"
  },
  {
    name: "prefab.stage-save",
    title: "Save Prefab Stage",
    description: "Save modifications in the current prefab isolation stage.",
    group: "prefab",
    inputSchema: objSchema({ path: { type: "string" } }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run prefab.stage-save",
    docsTopic: "prefab"
  },
  {
    name: "prefab.stage-close",
    title: "Close Prefab Stage",
    description: "Close the prefab isolation stage.",
    group: "prefab",
    inputSchema: objSchema({ path: { type: "string" }, save: { type: "boolean" } }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run prefab.stage-close",
    docsTopic: "prefab"
  },
  {
    name: "prefab.hierarchy",
    title: "Prefab Hierarchy",
    description: "List the child hierarchy of a prefab asset.",
    group: "prefab",
    inputSchema: objSchema({ path: { type: "string" }, pageSize: { type: "integer" }, cursor: { type: "string" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "tools run prefab.hierarchy",
    docsTopic: "prefab"
  },
  {
    name: "prefab.instantiate",
    title: "Instantiate Prefab",
    description: "Instantiate a prefab asset into the active scene, optionally under a parent and at a position.",
    group: "prefab",
    inputSchema: objSchema({ path: { type: "string" }, parent: { type: "string" }, name: { type: "string" }, x: { type: "number" }, y: { type: "number" }, z: { type: "number" } }, ["path"]),
    readOnlyHint: false,
    destructiveHint: false,
    supportsDryRun: true,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run prefab.instantiate",
    docsTopic: "prefab",
    equivalentUnityMcp: "prefab.instantiate"
  },
  {
    name: "prefab.unpack",
    title: "Unpack Prefab Instance",
    description: "Unpack a prefab instance into a regular GameObject, breaking the prefab link. Irreversible structural change.",
    group: "prefab",
    inputSchema: objSchema({ target: { type: "string" }, path: { type: "string" } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: true,
    supportsDryRun: true,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run prefab.unpack",
    docsTopic: "prefab",
    equivalentUnityMcp: "prefab.unpack"
  }
];
