import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

export const PREFAB_TOOLS: ToolMetadata[] = [
  {
    name: "prefab.info",
    summary: "Read a prefab asset's metadata: root name, guid, variant flag, source/instance roots, parents and modification status.",
    description: "Read a prefab asset's metadata: root name, guid, variant flag, source/instance roots, parents and modification status. Use asset.search with filter t:Prefab to discover paths when unknown, and prefab.hierarchy to inspect children.",
    group: "prefab",
    inputSchema: objSchema({ path: { type: "string", description: "Existing Assets/*.prefab path." } }, ["path"]),
    outputSchema: objSchema({
      prefab: objSchema({ path: { type: "string" }, guid: { type: "string" }, name: { type: "string" }, isVariant: { type: "boolean" }, rootName: { type: "string" } })
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run prefab.info",
    docsTopic: "prefab",
    equivalentUnityMcp: "manage_prefabs.get_info",
    examples: [
      { description: "Inspect a prefab asset", argv: ["tools", "run", "prefab.info", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbe.prefab\"}"] }
    ]
  },
  {
    name: "prefab.create",
    summary: "Save a scene GameObject as a new prefab asset at an Assets/*.prefab path. source resolves by name, hierarchy path or instance id; prefer path/id.",
    description: "Save a scene GameObject as a new prefab asset at an Assets/*.prefab path. source resolves by name, hierarchy path or instance id; prefer path/id. Refuses to overwrite an existing prefab unless overwrite is true. Use go.create first to build the source object.",
    group: "prefab",
    inputSchema: objSchema({
      source: { type: "string", description: "Source scene GameObject: name, hierarchy path (e.g. Parent/Child) or instance id." },
      path: { type: "string", description: "Destination Assets/*.prefab path." },
      overwrite: { type: "boolean", description: "Allow replacing an existing prefab asset." }
    }, ["source", "path"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    cliCommand: "tools run prefab.create",
    docsTopic: "prefab",
    equivalentUnityMcp: "manage_prefabs.create_from_gameobject",
    examples: [
      { description: "Save a scene object as a prefab", argv: ["tools", "run", "prefab.create", "--input", "{\"source\":\"AlignmentProbe\",\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedProbe.prefab\"}"] }
    ]
  },
  {
    name: "prefab.modify",
    summary: "Apply small prefab asset edits headlessly without opening the stage: rename the root, or add/remove a direct child by name.",
    description: "Apply small prefab asset edits headlessly without opening the stage: rename the root, or add/remove a direct child by name. For interactive editing (component fields, nested transforms) open the stage and edit inside it, then save. Use prefab.hierarchy to inspect child paths before removeChild.",
    group: "prefab",
    inputSchema: objSchema({
      path: { type: "string", description: "Existing Assets/*.prefab path." },
      name: { type: "string", description: "New root name." },
      addChild: { type: "string", description: "Name of a new empty direct child to add." },
      removeChild: { type: "string", description: "Name/path of a direct child to remove." },
      overwrite: { type: "boolean", description: "Allow overwriting the prefab asset." }
    }, ["path"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    cliCommand: "tools run prefab.modify",
    docsTopic: "prefab",
    equivalentUnityMcp: "manage_prefabs.modify_contents",
    examples: [
      { description: "Add a child to a prefab", argv: ["tools", "run", "prefab.modify", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbe.prefab\",\"addChild\":\"NewChild\"}"] }
    ]
  },
  {
    name: "prefab.stage-open",
    summary: "Open a prefab asset in Prefab isolation mode (Prefab Stage) for interactive editing with GameObject/component tools.",
    description: "Open a prefab asset in Prefab isolation mode (Prefab Stage) for interactive editing with GameObject/component tools. Only one Prefab Stage can be open at a time; an unsaved stage returns E_PREFAB_DIRTY_CONFLICT (save or close it first). Use prefab.stage-save/close to persist and exit.",
    group: "prefab",
    inputSchema: objSchema({ path: { type: "string", description: "Existing Assets/*.prefab path to open." } }, ["path"]),
    destructiveHint: false,
    sideEffects: ["editor-session"],
    riskLevel: "medium",
    playModeSupport: "editor-only",
    handler: "bridge",
    cliCommand: "tools run prefab.stage-open",
    docsTopic: "prefab",
    equivalentUnityMcp: "manage_prefabs.open_prefab_stage",
    examples: [
      { description: "Open a prefab for interactive editing", argv: ["tools", "run", "prefab.stage-open", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbe.prefab\"}"] }
    ]
  },
  {
    name: "prefab.stage-save",
    summary: "Save modifications in the currently open Prefab Stage to the prefab asset.",
    description: "Save modifications in the currently open Prefab Stage to the prefab asset. Requires a stage to be open (use prefab.stage-open first). After saving the stage remains open; use prefab.stage-close to exit.",
    group: "prefab",
    inputSchema: objSchema({ path: { type: "string", description: "Optional prefab path to confirm the open stage target." } }),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    riskLevel: "low",
    playModeSupport: "editor-only",
    handler: "bridge",
    cliCommand: "tools run prefab.stage-save",
    docsTopic: "prefab",
    equivalentUnityMcp: "manage_prefabs.save_prefab_stage",
    examples: [
      { description: "Save the current prefab stage", argv: ["tools", "run", "prefab.stage-save"] }
    ]
  },
  {
    name: "prefab.stage-close",
    summary: "Close the currently open Prefab Stage and return to the main scene.",
    description: "Close the currently open Prefab Stage and return to the main scene. Pass save=true to persist before closing; omitting it with unsaved changes returns E_PREFAB_DIRTY_CONFLICT. Requires a stage to be open.",
    group: "prefab",
    inputSchema: objSchema({ path: { type: "string", description: "Optional prefab path to confirm the open stage target." }, save: { type: "boolean", description: "Persist modifications before closing." } }),
    destructiveHint: false,
    sideEffects: ["editor-session"],
    riskLevel: "low",
    playModeSupport: "editor-only",
    handler: "bridge",
    cliCommand: "tools run prefab.stage-close",
    docsTopic: "prefab",
    equivalentUnityMcp: "manage_prefabs.close_prefab_stage",
    examples: [
      { description: "Close the prefab stage without saving", argv: ["tools", "run", "prefab.stage-close", "--input", "{\"save\":false}"] },
      { description: "Save and close the prefab stage", argv: ["tools", "run", "prefab.stage-close", "--input", "{\"save\":true}"] }
    ]
  },
  {
    name: "prefab.hierarchy",
    summary: "List the child hierarchy of a prefab asset (loaded via LoadPrefabContents and unloaded without modification).",
    description: "List the child hierarchy of a prefab asset (loaded via LoadPrefabContents and unloaded without modification). Paged by pageSize/cursor; use this before prefab.modify removeChild or before prefab.stage-open. For scene object hierarchies use scene.hierarchy instead.",
    group: "prefab",
    inputSchema: objSchema({
      path: { type: "string", description: "Existing Assets/*.prefab path." },
      pageSize: { type: "integer", default: 100, description: "Maximum children per page." },
      cursor: { type: "string", description: "Cursor from a previous pageInfo.nextCursor." }
    }, ["path"]),
    outputSchema: objSchema({
      command: { type: "string" },
      path: { type: "string" },
      count: { type: "integer" },
      children: { type: "array", items: { type: "object" } },
      pageInfo: { type: "object" }
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run prefab.hierarchy",
    docsTopic: "prefab",
    equivalentUnityMcp: "manage_prefabs.get_hierarchy",
    examples: [
      { description: "List a prefab's children", argv: ["tools", "run", "prefab.hierarchy", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbe.prefab\",\"pageSize\":50}"] }
    ]
  },
  {
    name: "prefab.instantiate",
    summary: "Instantiate a prefab asset into the active scene, optionally under a parent and at a position. parent resolves by name, hierarchy path or instance id.",
    description: "Instantiate a prefab asset into the active scene, optionally under a parent and at a position. parent resolves by name, hierarchy path or instance id. Use go.find/scene.hierarchy first to locate the parent. Supports --dry-run to preview without creating the instance.",
    group: "prefab",
    inputSchema: objSchema({
      path: { type: "string", description: "Existing Assets/*.prefab path to instantiate." },
      parent: { type: "string", description: "Optional parent GameObject: name, hierarchy path or instance id." },
      name: { type: "string", description: "Optional new instance name." },
      x: { type: "number", description: "Local position X." },
      y: { type: "number", description: "Local position Y." },
      z: { type: "number", description: "Local position Z." }
    }, ["path"]),
    destructiveHint: false,
    sideEffects: ["scene-modify"],
    riskLevel: "low",
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run prefab.instantiate",
    docsTopic: "prefab",
    equivalentUnityMcp: "prefab.instantiate",
    examples: [
      { description: "Instantiate a prefab at origin", argv: ["tools", "run", "prefab.instantiate", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbe.prefab\",\"x\":0,\"y\":0,\"z\":0}"] }
    ]
  },
  {
    name: "prefab.unpack",
    summary: "Unpack a prefab instance in the scene into a regular GameObject, breaking the prefab link irreversibly. target resolves by name, hierarchy path or instance id; prefer...",
    description: "Unpack a prefab instance in the scene into a regular GameObject, breaking the prefab link irreversibly. target resolves by name, hierarchy path or instance id; prefer path/id. Destructive and structural; always inspect the target first and use --dry-run before --yes.",
    group: "prefab",
    inputSchema: objSchema({ target: { type: "string", description: "Prefab instance in the scene: name, hierarchy path or instance id." }, path: { type: "string", description: "Alias of target." } }, ["target"]),
    destructiveHint: false,
    sideEffects: ["scene-modify", "prefab-unlink"],
    riskLevel: "medium",
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run prefab.unpack",
    docsTopic: "prefab",
    equivalentUnityMcp: "prefab.unpack",
    examples: [
      { description: "Preview unpacking a prefab instance", argv: ["tools", "run", "prefab.unpack", "--input", "{\"target\":\"AlignmentProbe(Clone)\"}", "--dry-run"] }
    ]
  }
];
