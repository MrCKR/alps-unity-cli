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
    playModeSupport: "session-affecting",
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
    playModeSupport: "session-affecting",
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
    playModeSupport: "editor-only",
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
    playModeSupport: "editor-only",
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
    playModeSupport: "editor-only",
    examples: [
      { description: "Close the prefab stage without saving", argv: ["tools", "run", "prefab.stage-close", "--input", "{\"save\":false}"] },
      { description: "Save and close the prefab stage", argv: ["tools", "run", "prefab.stage-close", "--input", "{\"save\":true}"] }
    ]
  },
  {
    name: "prefab.hierarchy",
    summary: "List a prefab asset hierarchy as a small, paged summary.",
    description: "List a prefab asset hierarchy as a small, paged summary. Defaults return only the prefab root or one parent's direct children, with child counts and component type names; use parent plus pageInfo.nextCursor to continue. dependencies.items and nestedPrefabs.items are omitted by default and only returned when includeSummaryDetails=true.",
    group: "prefab",
    inputSchema: objSchema({
      path: { type: "string", description: "Existing Assets/*.prefab path." },
      parent: { type: "string", description: "Optional returned node path/name whose direct children should be listed." },
      query: { type: "string", description: "Optional name/path substring filter for returned nodes." },
      componentType: { type: "string", description: "Optional component type filter, e.g. Animator or UnityEngine.Camera." },
      includeSummaryDetails: { type: "boolean", default: false, description: "Include dependency/nested prefab item arrays. Defaults false to keep output small." },
      pageSize: { type: "integer", default: 25, maximum: 500, description: "Maximum nodes returned in this page." },
      cursor: { type: "integer", default: 0, description: "Cursor from a previous pageInfo.nextCursor." }
    }, ["path"]),
    outputSchema: objSchema({
      prefab: { type: "object" },
      parent: { type: "string" },
      query: { type: "string" },
      componentType: { type: "string" },
      items: { type: "array", items: { type: "object" } },
      children: { type: "array", items: { type: "object" }, description: "Compatibility alias of items." },
      pageInfo: { type: "object" }
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "List a small prefab hierarchy page", argv: ["tools", "run", "prefab.hierarchy", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbe.prefab\",\"pageSize\":25}"] },
      { description: "Continue from one parent", argv: ["tools", "run", "prefab.hierarchy", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbe.prefab\",\"parent\":\"AlignmentProbe/Child\",\"pageSize\":25}"] }
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
    supportsDryRun: true,
    playModeSupport: "session-affecting",
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
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    examples: [
      { description: "Preview unpacking a prefab instance", argv: ["tools", "run", "prefab.unpack", "--input", "{\"target\":\"AlignmentProbe(Clone)\"}", "--dry-run"] }
    ]
  }
];
