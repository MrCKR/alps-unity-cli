import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const ASSET_TOOLS: ToolMetadata[] = [
  {
    name: "asset.search",
    summary: "Search the Unity AssetDatabase with Unity's native filter syntax.",
    description: "Search the Unity AssetDatabase with Unity's native filter syntax. Use filter for labels (l:), types (t:), bundles (b:), area (a:) or glob: expressions; use path/searchInFolders only for Assets/ or Packages/ folder scopes. Use this before asset.get-data when the exact assetPath is unknown. Results are paged; keep pageSize/maxResults small before deep-reading assets.",
    group: "asset",
    inputSchema: objSchema({
      query: { type: "string", description: "Simple name/text search. Prefer filter when using Unity syntax such as t:Material or l:ui." },
      filter: { type: "string", description: "Unity AssetDatabase filter string. Supports name words, l:label, t:Type, b:bundle, a:assets/packages/all and glob: patterns." },
      type: { type: "string", description: "Optional Unity type filter appended as t:<type>; do not include t:." },
      path: { type: "string", description: "Optional single search root under Assets/ or Packages/. Not search text." },
      searchInFolders: { type: "array", items: { type: "string" }, description: "Optional folder scopes under Assets/ or Packages/. Use instead of path when searching multiple folders." },
      pageSize: { type: "integer", default: 100, description: "Maximum results per page, clamped to 1..500." },
      maxResults: { type: "integer", description: "Alias of pageSize for IvanMurzak-style calls; clamped to 1..500." },
      cursor: { type: "integer", default: 0, description: "Zero-based result offset from a previous pageInfo.nextCursor." }
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    cliCommand: "tools run asset.search",
    docsTopic: "asset",
    equivalentUnityMcp: "assets-find",
    examples: [
      { description: "Search materials in a fixture folder", argv: ["tools", "run", "asset.search", "--input", "{\"filter\":\"Alignment t:Material\",\"path\":\"Assets/UnityCliAlignmentFixtures\",\"pageSize\":20}"] },
      { description: "Search by label with a small cap", argv: ["tools", "run", "asset.search", "--input", "{\"filter\":\"l:ui\",\"searchInFolders\":[\"Assets\"],\"maxResults\":10}"] }
    ]
  },
  {
    name: "asset.info",
    summary: "Get metadata and path information for an existing Assets/* asset.",
    description: "Get metadata and path information for an existing Assets/* asset. Use this before mutate/delete operations when you need the GUID, type, folder flag, or file size.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string", description: "Existing Assets/* asset or folder path." } }, ["path"]),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    cliCommand: "tools run asset.info",
    docsTopic: "asset",
    equivalentUnityMcp: "manage_asset.get_info",
    examples: [
      { description: "Inspect a fixture asset", argv: ["tools", "run", "asset.info", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/AlignmentControllerCore.controller\"}"] }
    ]
  },
  {
    name: "asset.create",
    summary: "Create a folder or UTF-8 text asset inside Assets/.",
    description: "Create a folder or UTF-8 text asset inside Assets/. Folder creation is idempotent; file creation refuses existing files unless overwrite is true.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string", description: "Destination Assets/* path." }, type: { type: "string", enum: ["file", "folder"], description: "Create a text file or folder. If omitted, extensionless paths are treated as folders." }, content: { type: "string", description: "Text content for file assets." }, overwrite: { type: "boolean", description: "Allow replacing an existing text file." } }, ["path"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    playModeSupport: "session-affecting",
    handler: "bridge",
    cliCommand: "tools run asset.create",
    docsTopic: "asset",
    equivalentUnityMcp: "manage_asset.create/create_folder",
    examples: [
      { description: "Create a fixture text asset", argv: ["tools", "run", "asset.create", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedConfig.json\",\"type\":\"file\",\"content\":\"{}\",\"overwrite\":true}"] },
      { description: "Create a fixture folder", argv: ["tools", "run", "asset.create", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedFolder\",\"type\":\"folder\"}"] }
    ]
  },
  {
    name: "asset.import",
    summary: "Copy an external file or supplied text content into Assets/ and force Unity to import it.",
    description: "Copy an external file or supplied text content into Assets/ and force Unity to import it. Use overwrite only when replacing the destination is intended.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string", description: "Destination Assets/* path." }, source: { type: "string", description: "External file path or existing Assets/* source file to copy." }, content: { type: "string", description: "Inline text content when no source file is supplied." }, overwrite: { type: "boolean", description: "Allow replacing an existing destination file." } }, ["path"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    playModeSupport: "session-affecting",
    handler: "bridge",
    cliCommand: "tools run asset.import",
    docsTopic: "asset",
    equivalentUnityMcp: "manage_asset.import",
    examples: [
      { description: "Import inline text into a fixture asset", argv: ["tools", "run", "asset.import", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/ImportedNote.txt\",\"content\":\"hello\",\"overwrite\":true}"] }
    ]
  },
  {
    name: "asset.modify",
    summary: "Replace text-like asset file content and force a reimport.",
    description: "Replace text-like asset file content and force a reimport. This is for text assets; use typed tools for Unity object assets such as Materials or AnimatorControllers.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string", description: "Existing Assets/* text-like asset path." }, content: { type: "string", description: "Replacement UTF-8 text content." } }, ["path", "content"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    playModeSupport: "session-affecting",
    handler: "bridge",
    cliCommand: "tools run asset.modify",
    docsTopic: "asset",
    equivalentUnityMcp: "manage_asset.modify",
    examples: [
      { description: "Replace a fixture text asset", argv: ["tools", "run", "asset.modify", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedConfig.json\",\"content\":\"{\\\"updated\\\":true}\"}"] }
    ]
  },
  {
    name: "asset.delete",
    summary: "Delete an Assets/* asset or folder permanently.",
    description: "Delete an Assets/* asset or folder permanently. Always inspect with asset.info first and prefer --dry-run before passing --yes.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string", description: "Existing Assets/* asset or folder path to delete." } }, ["path"]),
    destructiveHint: true,
    sideEffects: ["asset-delete", "asset-import"],
    playModeSupport: "session-affecting",
    supportsDryRun: true,
    handler: "bridge",
    cliCommand: "tools run asset.delete",
    docsTopic: "asset",
    equivalentUnityMcp: "manage_asset.delete",
    examples: [
      { description: "Preview a fixture asset delete", argv: ["tools", "run", "asset.delete", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedConfig.json\"}", "--dry-run"] },
      { description: "Delete a fixture asset after confirmation", argv: ["tools", "run", "asset.delete", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/GeneratedConfig.json\"}", "--yes"] }
    ]
  },
  {
    name: "asset.duplicate",
    summary: "Copy an existing asset to a new Assets/* path.",
    description: "Copy an existing asset to a new Assets/* path. The destination must not already exist.",
    group: "asset",
    inputSchema: objSchema({ source: { type: "string", description: "Existing source Assets/* path." }, destination: { type: "string", description: "New destination Assets/* path." } }, ["source", "destination"]),
    destructiveHint: false,
    sideEffects: ["asset-write", "asset-import"],
    playModeSupport: "session-affecting",
    handler: "bridge",
    cliCommand: "tools run asset.duplicate",
    docsTopic: "asset",
    equivalentUnityMcp: "manage_asset.duplicate",
    examples: [
      { description: "Duplicate a fixture text asset", argv: ["tools", "run", "asset.duplicate", "--input", "{\"source\":\"Assets/UnityCliAlignmentFixtures/GeneratedConfig.json\",\"destination\":\"Assets/UnityCliAlignmentFixtures/GeneratedConfigCopy.json\"}"] }
    ]
  },
  {
    name: "asset.move",
    summary: "Move an existing asset from one Assets/* path to another.",
    description: "Move an existing asset from one Assets/* path to another. This changes the asset path while preserving Unity's GUID when Unity can move it successfully.",
    group: "asset",
    inputSchema: objSchema({ source: { type: "string", description: "Existing source Assets/* path." }, destination: { type: "string", description: "Destination Assets/* path." } }, ["source", "destination"]),
    destructiveHint: false,
    sideEffects: ["asset-move", "asset-import"],
    playModeSupport: "session-affecting",
    handler: "bridge",
    cliCommand: "tools run asset.move",
    docsTopic: "asset",
    equivalentUnityMcp: "manage_asset.move",
    examples: [
      { description: "Move a fixture text asset", argv: ["tools", "run", "asset.move", "--input", "{\"source\":\"Assets/UnityCliAlignmentFixtures/GeneratedConfigCopy.json\",\"destination\":\"Assets/UnityCliAlignmentFixtures/MovedConfig.json\"}"] }
    ]
  },
  {
    name: "asset.rename",
    summary: "Rename an existing asset within its current folder.",
    description: "Rename an existing asset within its current folder. Pass only the new file name; use asset.move when changing directories.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string", description: "Existing Assets/* path." }, name: { type: "string", description: "New file name without folder separators." } }, ["path", "name"]),
    destructiveHint: false,
    sideEffects: ["asset-move", "asset-import"],
    playModeSupport: "session-affecting",
    handler: "bridge",
    cliCommand: "tools run asset.rename",
    docsTopic: "asset",
    equivalentUnityMcp: "manage_asset.rename",
    examples: [
      { description: "Rename a fixture text asset", argv: ["tools", "run", "asset.rename", "--input", "{\"path\":\"Assets/UnityCliAlignmentFixtures/MovedConfig.json\",\"name\":\"RenamedConfig.json\"}"] }
    ]
  },
  {
    name: "asset.search-built-in",
    summary: "Search Resources/ and Packages/ assets by keyword and optional type filter.",
    description: "Search Resources/ and Packages/ assets by keyword and optional type filter. Results are capped by pageSize; use asset.search for regular project Assets/* searches.",
    group: "asset",
    inputSchema: objSchema({ keyword: { type: "string", description: "AssetDatabase search text." }, type: { type: "string", description: "Optional main asset type name, e.g. Texture2D or Material." }, pageSize: { type: "integer", default: 100, description: "Maximum returned assets, clamped to 1..500." } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run asset.search-built-in",
    docsTopic: "asset",
    equivalentUnityMcp: "assets-find-built-in",
    examples: [
      { description: "Search package resources", argv: ["tools", "run", "asset.search-built-in", "--input", "{\"keyword\":\"Default\",\"pageSize\":25}"] }
    ]
  },
  {
    name: "asset.get-data",
    summary: "Deep-serialize an asset's main Unity Object.",
    description: "Deep-serialize an asset's main Unity Object. Use asset.search first when the path is unknown. Control payload with exact paths or one viewQuery; they are mutually exclusive. Avoid default-deep reads on large prefabs, textures, or ScriptableObjects.",
    group: "asset",
    inputSchema: objSchema({
      assetPath: { type: "string", description: "Exact Assets/* path to the asset main object." },
      path: { type: "string", description: "Backward-compatible alias of assetPath." },
      maxDepth: { type: "integer", default: 4, description: "Maximum serialization depth for full reads or viewQuery subtree." },
      includeFields: { type: "boolean", default: true },
      includeProperties: { type: "boolean", default: true },
      deepSerialization: { type: "boolean", default: true },
      paths: { type: "array", items: { type: "string" }, description: "Exact member paths to read as a small aggregate, e.g. [\"name\"]. Mutually exclusive with viewQuery." },
      viewQuery: { type: "object", description: "Optional subtree/filter read: {path?, namePattern?, typeFilter?, maxDepth?}. Mutually exclusive with paths." },
      namePattern: { type: "string", description: "Comma-separated exact member-name filter for full reads; prefer paths/viewQuery for token safety." }
    }, ["assetPath"]),
    outputSchema: objSchema({
      command: { type: "string" },
      assetPath: { type: "string" },
      guid: { type: "string" },
      typeName: { type: "string", description: "Serialized type or UnityCli.PathReadAggregate for paths reads." },
      path: { type: "string", description: "SerializedMember root path." },
      fields: { type: "array", items: { type: "object" }, description: "Present for path aggregate or expanded field data." },
      props: { type: "array", items: { type: "object" }, description: "Present for property data when included." },
      unityObjectRef: { type: "object" }
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run asset.get-data",
    docsTopic: "asset",
    equivalentUnityMcp: "assets-get-data",
    examples: [
      { description: "Read exact fields only", argv: ["tools", "run", "asset.get-data", "--input", "{\"assetPath\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeMaterial.mat\",\"paths\":[\"name\"],\"maxDepth\":2}"] },
      { description: "Read a filtered subtree", argv: ["tools", "run", "asset.get-data", "--input", "{\"assetPath\":\"Assets/UnityCliAlignmentFixtures/AlignmentProbeMaterial.mat\",\"viewQuery\":{\"namePattern\":\"name\",\"maxDepth\":1}}"] }
    ]
  },
  {
    name: "asset.get-components",
    summary: "Read importer and Prefab/GameObject component summaries for an asset without deep-serializing the whole object graph.",
    description: "Read importer and Prefab/GameObject component summaries for an asset without deep-serializing the whole object graph. For prefab assets, returns paged component rows with hierarchy path, GameObject active state, component type, and componentIndex. For non-GameObject assets, returns importer/main object metadata with componentCount=0.",
    group: "asset",
    inputSchema: objSchema({ assetPath: { type: "string", description: "Exact Assets/* path to inspect." }, path: { type: "string", description: "Alias of assetPath." }, includeInactive: { type: "boolean", default: true }, pageSize: { type: "integer", default: 200, maximum: 1000 }, cursor: { type: "integer", default: 0 } }, ["assetPath"]),
    outputSchema: pageSchema({ type: "object", properties: { path: { type: "string" }, gameObject: { type: "string" }, componentIndex: { type: "integer" }, type: { type: "string" }, fullName: { type: "string" }, enabled: { type: "boolean" } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run asset.get-components",
    docsTopic: "asset",
    equivalentUnityMcp: "manage_asset.get_components",
    examples: [
      { description: "List components in a prefab asset", argv: ["tools", "run", "asset.get-components", "--input", "{\"assetPath\":\"Assets/Prefabs/Player.prefab\",\"pageSize\":50}"] }
    ]
  },
  {
    name: "asset.refresh",
    summary: "Refresh the AssetDatabase in the live Editor, or import one asset path with refresh options.",
    description: "Refresh the AssetDatabase in the live Editor, or import one asset path with refresh options. This is the asset-domain live equivalent of editor.refresh batch jobs; use path for one asset or omit path to refresh the whole AssetDatabase. Supports dry-run for planned effects.",
    group: "asset",
    inputSchema: objSchema({ path: { type: "string", description: "Optional Assets/* path to import instead of refreshing the whole AssetDatabase." }, assetPath: { type: "string", description: "Alias of path." }, forceUpdate: { type: "boolean", default: false, description: "Use ImportAssetOptions.ForceUpdate." }, importRecursive: { type: "boolean", default: false, description: "Use ImportAssetOptions.ImportRecursive for folder paths." } }),
    outputSchema: objSchema({ command: { type: "string" }, path: { type: "string" }, options: { type: "string" }, refreshed: { type: "boolean" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    sideEffects: ["asset-import", "asset-refresh"],
    playModeSupport: "session-affecting",
    supportsDryRun: true,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run asset.refresh",
    docsTopic: "asset",
    equivalentUnityMcp: "assets-refresh",
    examples: [
      { description: "Refresh the AssetDatabase", argv: ["tools", "run", "asset.refresh", "--input", "{}"] },
      { description: "Preview importing one asset", argv: ["tools", "run", "asset.refresh", "--input", "{\"path\":\"Assets/Generated/config.json\",\"forceUpdate\":true}", "--dry-run"] }
    ]
  },
  {
    name: "asset.preview",
    summary: "Generate/read the asset preview thumbnail metadata (size, dimensions, transient state).",
    description: "Generate/read the asset preview thumbnail metadata (size, dimensions, transient state). Preview generation can be session-affecting because Unity may refresh transient preview cache.",
    group: "asset",
    inputSchema: objSchema({ assetPath: { type: "string" }, path: { type: "string" } }, ["assetPath"]),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run asset.preview",
    docsTopic: "asset",
    equivalentUnityMcp: "manage_asset.generate_preview"
  }
];
