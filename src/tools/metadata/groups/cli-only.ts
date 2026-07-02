import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

/**
 * CLI-only 工具:不通过 Unity Bridge,在本地 CLI 完成,或经 job runner/batchmode 执行。
 * Unity 能力与离线查询仍以 `tools run <name>` 暴露。
 * target/instances/jobs/batch/doctor 属于 CLI 控制面,不得在这里重复注册为工具入口。
 */
export const CLI_ONLY_TOOLS: ToolMetadata[] = [
  {
    name: "package.list",
    summary: "List installed packages and their requested/locked versions in the project manifest.",
    description: "List installed packages and their requested/locked versions in the project manifest. Offline (reads manifest directly, no Unity needed). Use package.status for a manifest+lock summary or package.info for one package.",
    group: "package",
    inputSchema: objSchema({ project: { type: "string", description: "Unity project path. Defaults to the current target project." } }),
    outputSchema: objSchema({ manifestPath: { type: "string" }, lockPath: { type: "string" }, registry: { type: "string" }, packages: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "List installed packages", argv: ["tools", "run", "package.list", "--input", "{\"project\":\"D:/workspace/alps-unity-cli/Dev/UnityProject\"}"] }
    ]
  },
  {
    name: "package.status",
    summary: "Summarize manifest and lock state: package count, registry, scoped registries, and per-package resolved flag.",
    description: "Summarize manifest and lock state: package count, registry, scoped registries, and per-package resolved flag. Offline.",
    group: "package",
    inputSchema: objSchema({ project: { type: "string", description: "Unity project path." } }),
    outputSchema: objSchema({ projectPath: { type: "string" }, manifestPath: { type: "string" }, packageCount: { type: "integer" }, registry: { type: "string" }, scopedRegistryCount: { type: "integer" }, packages: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Show package status", argv: ["tools", "run", "package.status", "--input", "{\"project\":\"D:/workspace/alps-unity-cli/Dev/UnityProject\"}"] }
    ]
  },
  {
    name: "package.info",
    summary: "Show detailed info for one installed package by name.",
    description: "Show detailed info for one installed package by name. Returns PACKAGE_NOT_FOUND if the package is not in the manifest; use package.list first.",
    group: "package",
    inputSchema: objSchema({ name: { type: "string", description: "Package name, e.g. com.unity.render-pipelines.universal." }, project: { type: "string", description: "Unity project path." } }, ["name"]),
    outputSchema: objSchema({ projectPath: { type: "string" }, package: { type: "object" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Inspect one package", argv: ["tools", "run", "package.info", "--input", "{\"name\":\"com.unity_cli\",\"project\":\"D:/workspace/alps-unity-cli/Dev/UnityProject\"}"] }
    ]
  },
  {
    name: "package.search",
    summary: "Search a Unity package registry by name and return candidate packages with versions/descriptions.",
    description: "Search a Unity package registry by name and return candidate packages with versions/descriptions. Offline (HTTP to registry, no Unity needed). Use this before package.install.",
    group: "package",
    inputSchema: objSchema({ query: { type: "string", description: "Package name search text." }, registry: { type: "string", description: "Registry URL. Defaults to the project registry or https://packages.unity.com." } }, ["query"]),
    outputSchema: objSchema({ registry: { type: "string" }, query: { type: "string" }, packages: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Search a registry", argv: ["tools", "run", "package.search", "--input", "{\"query\":\"textmeshpro\"}"] }
    ]
  },
  {
    name: "package.install",
    summary: "Install a package into the project manifest and optionally resolve via Unity PackageManager.",
    description: "Install a package into the project manifest and optionally resolve via Unity PackageManager. Destructive: manifest change + possible Unity resolve/domain reload. Requires --yes and an explicit version because Unity manifests do not accept 'latest'. Refuses numeric-version downgrades from the current manifest version; use package.info first when unsure.",
    group: "package",
    inputSchema: objSchema({ name: { type: "string", description: "Package name to install. May include @version, e.g. com.unity.textmeshpro@3.0.7." }, version: { type: "string", description: "Required unless name includes @version. Use a SemVer package version accepted by Unity." }, project: { type: "string", description: "Unity project path." }, unity: { type: "string", description: "Unity executable path for batch resolve." }, wait: { type: "boolean", description: "Wait for resolve job to finish." } }, ["name"]),
    outputSchema: objSchema({ action: { type: "string" }, package: { type: "string" }, manifestPath: { type: "string" }, installedVersion: { type: "string" }, previousVersion: { type: "string" }, changed: { type: "boolean" } }),
    destructiveHint: true,
    sideEffects: ["manifest-change", "session-affecting"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Install a package", argv: ["tools", "run", "package.install", "--input", "{\"name\":\"com.unity.textmeshpro\",\"version\":\"3.0.7\"}"] }
    ]
  },
  {
    name: "package.remove",
    summary: "Remove a package from the project manifest and optionally resolve.",
    description: "Remove a package from the project manifest and optionally resolve. Destructive: manifest change + possible Unity resolve/domain reload. Requires explicit confirmation; inspect with package.list first. By default it blocks when packages-lock.json shows other packages depending on the target; pass force=true only when you intentionally want Unity PackageManager to resolve that dependency break. Supports --dry-run to return plannedEffects without writing manifest.json.",
    group: "package",
    inputSchema: objSchema({ name: { type: "string", description: "Package name to remove." }, force: { type: "boolean", default: false, description: "Allow removal even when packages-lock.json lists dependent packages." }, project: { type: "string", description: "Unity project path." }, unity: { type: "string", description: "Unity executable path for batch resolve." }, wait: { type: "boolean", description: "Wait for resolve job to finish." } }, ["name"]),
    outputSchema: objSchema({ action: { type: "string" }, package: { type: "string" }, manifestPath: { type: "string" }, removedVersion: { type: "string" }, dependents: { type: "array", items: { type: "object" } }, forced: { type: "boolean" }, dryRun: { type: "boolean" }, blocked: { type: "boolean" }, plannedEffects: { type: "array", items: { type: "object" } } }),
    destructiveHint: true,
    supportsDryRun: true,
    sideEffects: ["manifest-change", "session-affecting"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Preview removing a package", argv: ["tools", "run", "package.remove", "--input", "{\"name\":\"com.unity.textmeshpro\"}", "--dry-run"] }
    ]
  },
  {
    name: "package.bridge-remove",
    summary: "Remove the copied com.unity_cli Bridge package installed by init.",
    description: "Remove the copied com.unity_cli Bridge package installed by init. Destructive: deletes Packages/com.unity_cli and unity_cli.install.json, and removes any legacy com.unity_cli manifest dependency. Supports --dry-run to preview files and manifest effects without deleting anything. Use this only when intentionally uninstalling the Unity CLI Bridge from a project.",
    group: "package",
    inputSchema: objSchema({ project: { type: "string", description: "Unity project path." } }),
    outputSchema: objSchema({ action: { type: "string" }, projectPath: { type: "string" }, manifestPath: { type: "string" }, bridgePath: { type: "string" }, dependencyName: { type: "string" }, packageVersion: { type: "string" }, installMetadataPath: { type: "string" }, managedFileCount: { type: "integer" }, removedManifestDependency: { type: "boolean" }, dryRun: { type: "boolean" }, plannedEffects: { type: "array", items: { type: "object" } } }),
    destructiveHint: true,
    supportsDryRun: true,
    sideEffects: ["project-file-delete", "manifest-change", "session-affecting"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Preview Bridge package removal", argv: ["tools", "run", "package.bridge-remove", "--input", "{\"project\":\"D:/workspace/alps-unity-cli/Dev/UnityProject\"}", "--dry-run"] }
    ]
  },
  {
    name: "package.deploy",
    summary: "Deploy a local Unity package source folder into Packages/<packageName> with an automatic restore backup.",
    description: "Deploy a local Unity package source folder into the target project's Packages/<packageName> directory. The source folder must contain package.json with name/version and must be outside the target project's Packages directory. If a package directory already exists, package.deploy backs it up under Packages/.unity_cli_deploy_backups before copying the source. Destructive: writes project package files and can trigger Unity import/compile. Supports --dry-run to preview backup/copy effects without writing.",
    group: "package",
    inputSchema: objSchema({ source: { type: "string", description: "Local Unity package source folder containing package.json." }, packageName: { type: "string", description: "Optional expected package name. Defaults to source package.json name." }, project: { type: "string", description: "Unity project path." } }, ["source"]),
    outputSchema: objSchema({ action: { type: "string" }, projectPath: { type: "string" }, manifestPath: { type: "string" }, packageName: { type: "string" }, packageVersion: { type: "string" }, sourcePath: { type: "string" }, sourceHash: { type: "string" }, targetPath: { type: "string" }, backupPath: { type: "string" }, backupCreated: { type: "boolean" }, repeatedDeployment: { type: "boolean" }, copiedFileCount: { type: "integer" }, dryRun: { type: "boolean" }, plannedEffects: { type: "array", items: { type: "object" } }, liveRefresh: { type: "object" } }),
    destructiveHint: true,
    supportsDryRun: true,
    sideEffects: ["project-file-write", "project-file-delete", "session-affecting"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Preview deploying a local package", argv: ["tools", "run", "package.deploy", "--input", "{\"project\":\"D:/workspace/alps-unity-cli/Dev/UnityProject\",\"source\":\"D:/workspace/my-package\"}", "--dry-run"] },
      { description: "Deploy and request an open Editor refresh", argv: ["tools", "run", "package.deploy", "--input", "{\"source\":\"D:/workspace/my-package\"}", "--yes"] }
    ]
  },
  {
    name: "package.restore",
    summary: "Restore a package directory from the latest package.deploy backup.",
    description: "Restore a package previously deployed by package.deploy. If package.deploy backed up an existing package directory, package.restore replaces Packages/<packageName> with that backup. If deploy created a new package with no previous directory, restore removes the deployed package directory. Destructive: writes/deletes project package files and can trigger Unity import/compile. Supports --dry-run to preview effects without writing.",
    group: "package",
    inputSchema: objSchema({ packageName: { type: "string", description: "Package name to restore, e.g. com.acme.tool." }, project: { type: "string", description: "Unity project path." } }, ["packageName"]),
    outputSchema: objSchema({ action: { type: "string" }, projectPath: { type: "string" }, manifestPath: { type: "string" }, packageName: { type: "string" }, targetPath: { type: "string" }, backupPath: { type: "string" }, restoredFromBackup: { type: "boolean" }, removedDeployedPackage: { type: "boolean" }, copiedFileCount: { type: "integer" }, dryRun: { type: "boolean" }, plannedEffects: { type: "array", items: { type: "object" } }, liveRefresh: { type: "object" } }),
    destructiveHint: true,
    supportsDryRun: true,
    sideEffects: ["project-file-write", "project-file-delete", "session-affecting"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Preview restoring a deployed package", argv: ["tools", "run", "package.restore", "--input", "{\"packageName\":\"com.acme.tool\"}", "--dry-run"] },
      { description: "Restore a deployed package", argv: ["tools", "run", "package.restore", "--input", "{\"packageName\":\"com.acme.tool\"}", "--yes"] }
    ]
  },
  {
    name: "package.resolve",
    summary: "Resolve all packages in the project manifest by running Unity PackageManager (batch job).",
    description: "Resolve all packages in the project manifest by running Unity PackageManager as a batch job. Use this after manual manifest edits or install/remove when no live Editor refresh is available; it is long-running and can trigger import, compile or domain reload.",
    group: "package",
    inputSchema: objSchema({ project: { type: "string", description: "Unity project path." }, unity: { type: "string", description: "Unity executable path." }, wait: { type: "boolean", default: true, description: "Wait for the resolve job to finish." } }),
    outputSchema: objSchema({ jobId: { type: "string" }, status: { type: "string" }, projectPath: { type: "string" }, command: { type: "string" }, startedAt: { type: "string" }, finishedAt: { type: "string" }, exitCode: { type: "integer" }, logPath: { type: "string" }, bridge: { type: "object" }, nextActions: { type: "array", items: { type: "string" } } }),
    destructiveHint: false,
    sideEffects: ["session-affecting"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Start a package resolve job", argv: ["tools", "run", "package.resolve", "--input", "{\"project\":\"D:/workspace/alps-unity-cli/Dev/UnityProject\",\"wait\":false}"] },
      { description: "Run and wait for package resolve", argv: ["tools", "run", "package.resolve", "--input", "{\"project\":\"D:/workspace/alps-unity-cli/Dev/UnityProject\",\"wait\":true}"] }
    ],
  },
  {
    name: "package.registry",
    summary: "Get, set, or clear the primary registry URL in the project manifest. action=get reads, set writes url, clear removes.",
    description: "Get, set, or clear the primary registry URL in the project manifest. action=get reads, set writes url, clear removes. Offline (manifest edit).",
    group: "package",
    inputSchema: objSchema({ action: { type: "string", enum: ["get", "set", "clear"], description: "Registry action." }, url: { type: "string", description: "Registry URL (required for set)." }, project: { type: "string", description: "Unity project path." } }, ["action"]),
    destructiveHint: false,
    sideEffects: ["manifest-change"],
    playModeSupport: "supported",
    examples: [
      { description: "Read the primary registry", argv: ["tools", "run", "package.registry", "--input", "{\"action\":\"get\"}"] }
    ]
  },
  {
    name: "package.scoped-registry",
    summary: "List, add, or remove scoped registries in the project manifest. action=list reads all; add requires name/url/(scope|scopes); remove requires name.",
    description: "List, add, or remove scoped registries in the project manifest. action=list reads all; add requires name/url/(scope|scopes); remove requires name. Write actions edit manifest.json and require --yes; remove blocks when manifest dependencies still match the registry scopes unless force=true. remove supports --dry-run to preview matched packages without writing.",
    group: "package",
    inputSchema: objSchema({ action: { type: "string", enum: ["list", "add", "remove"], description: "Scoped registry action." }, name: { type: "string", description: "Scoped registry name (required for add/remove)." }, url: { type: "string", description: "Registry URL (required for add)." }, scope: { type: "string", description: "Single scope (add)." }, scopes: { type: "string", description: "Comma-separated scopes (add)." }, force: { type: "boolean", default: false, description: "For remove, allow deleting a registry whose scopes still match manifest packages." }, project: { type: "string", description: "Unity project path." } }, ["action"]),
    outputSchema: objSchema({ projectPath: { type: "string" }, manifestPath: { type: "string" }, scopedRegistries: { type: "array", items: { type: "object" } }, registry: { type: "object" }, removed: { type: "object" }, matchedPackages: { type: "array", items: { type: "string" } }, forced: { type: "boolean" }, dryRun: { type: "boolean" }, blocked: { type: "boolean" }, plannedEffects: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    sideEffects: ["manifest-change"],
    playModeSupport: "supported",
    examples: [
      { description: "List scoped registries", argv: ["tools", "run", "package.scoped-registry", "--input", "{\"action\":\"list\"}"] },
      { description: "Preview removing a scoped registry", argv: ["tools", "run", "package.scoped-registry", "--input", "{\"action\":\"remove\",\"name\":\"Acme\"}", "--dry-run"] }
    ]
  },
      {
    name: "docs.list",
    summary: "List built-in Unity CLI documentation topics without requiring a Unity Editor connection.",
    description: "List built-in Unity CLI documentation topics without requiring a Unity Editor connection. Use this before docs.topic when the topic key is unknown.",
    group: "docs",
    inputSchema: objSchema({ format: { type: "string", enum: ["json", "text"], description: "Output mode for human-readable text or structured JSON." } }),
    outputSchema: objSchema({ command: { type: "string" }, mode: { type: "string" }, requiresUnity: { type: "boolean" }, count: { type: "integer" }, topics: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "List built-in CLI doc topics", argv: ["tools", "run", "docs.list", "--input", "{}"] }
    ],
  },
  {
    name: "docs.topic",
    summary: "Read one built-in Unity CLI documentation topic by key.",
    description: "Read one built-in Unity CLI documentation topic by key. This is offline CLI guidance, not Unity ScriptReference content.",
    group: "docs",
    inputSchema: objSchema({ topic: { type: "string", description: "Built-in topic key from docs.list, e.g. scene, component, tools, package." }, format: { type: "string", enum: ["json", "text"], description: "Output mode for human-readable text or structured JSON." } }, ["topic"]),
    outputSchema: objSchema({ command: { type: "string" }, mode: { type: "string" }, requiresUnity: { type: "boolean" }, topic: { type: "string" }, title: { type: "string" }, summary: { type: "string" }, tools: { type: "array", items: { type: "string" } }, guide: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Read the scene topic", argv: ["tools", "run", "docs.topic", "--input", "{\"topic\":\"scene\"}"] }
    ],
  },
  {
    name: "docs.get",
    summary: "Read one Unity API document offline first, with docs.unity3d.com fallback when cache lookup misses.",
    description: "Read one Unity API document from the offline docs cache first, with docs.unity3d.com fallback when cache lookup misses. A populated docs cache via --docs-dir or UNITY_CLI_DOCS_DIR is preferred; pass offlineOnly=true to forbid network fallback. If --docs-dir points to a missing cache, the command preserves DOCS_CACHE_MISSING unless onlineFallback=true is set explicitly.",
    group: "docs",
    inputSchema: objSchema({ api: { type: "string", description: "Exact or unique Unity API name, e.g. UnityEngine.Camera." }, docsDir: { type: "string", description: "Path to a populated offline Unity docs cache containing docs-index.json, unity-docs.json, docs.json, index.json, or Markdown files." }, unityVersion: { type: "string", description: "Optional Unity version hint used to select a versioned docs cache." }, format: { type: "string", enum: ["json", "text"], description: "Use text for compact human-readable output; JSON includes structured doc fields." }, offlineOnly: { type: "boolean", default: false, description: "Disable docs.unity3d.com online fallback." }, onlineFallback: { type: "boolean", default: false, description: "Allow online fallback even when an explicitly supplied docsDir is missing." } }, ["api"]),
    outputSchema: objSchema({ command: { type: "string" }, mode: { type: "string" }, requiresUnity: { type: "boolean" }, source: { type: "object" }, unityVersion: { type: "object" }, api: { type: "string" }, format: { type: "string" }, doc: { type: "object" }, text: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Read cached Camera docs", argv: ["tools", "run", "docs.get", "--input", "{\"api\":\"UnityEngine.Camera\",\"docsDir\":\".cache/unity-docs\",\"format\":\"text\"}"] }
    ],
  },
  {
    name: "docs.search",
    summary: "Search Unity API documentation offline first, with docs.unity3d.com fallback when cache lookup misses.",
    description: "Search Unity API documentation offline first, with cursor pagination and docs.unity3d.com fallback when cache lookup misses. Pass offlineOnly=true to keep the command strictly offline.",
    group: "docs",
    inputSchema: objSchema({ query: { type: "string", description: "Search text, usually a Unity type/member name fragment." }, docsDir: { type: "string", description: "Path to a populated offline Unity docs cache." }, pageSize: { type: "integer", default: 20, description: "Maximum results to return in one call." }, cursor: { type: "string", default: "0", description: "Cursor from pageInfo.nextCursor for the next page." }, format: { type: "string", enum: ["json", "text"], description: "Output mode for compact text or structured JSON." }, offlineOnly: { type: "boolean", default: false, description: "Disable docs.unity3d.com online fallback." }, onlineFallback: { type: "boolean", default: false, description: "Allow online fallback even when an explicitly supplied docsDir is missing." } }, ["query"]),
    outputSchema: pageSchema({ type: "object", properties: { name: { type: "string" }, title: { type: "string" }, summary: { type: "string" }, path: { type: "string" } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Search cached Unity docs", argv: ["tools", "run", "docs.search", "--input", "{\"query\":\"Camera\",\"docsDir\":\".cache/unity-docs\",\"pageSize\":5}"] }
    ],
  },
  {
    name: "reflect.type",
    summary: "Look up a C# type and its member names in the offline Unity reflection cache.",
    description: "Look up a C# type and its member names in the offline Unity reflection cache. Use this to verify a type exists and read its public member names before writing C# or calling reflection.method.*. Use reflect.search first when the exact type name is unknown.",
    group: "reflect",
    inputSchema: objSchema({ name: { type: "string", description: "Fully-qualified or simple C# type name, e.g. UnityEngine.Camera." }, reflectDir: { type: "string", description: "Offline reflection cache directory. Defaults to UNITY_CLI_REFLECT_DIR or <dataDir>/reflect." }, unityVersion: { type: "string", description: "Optional Unity version filter for the cache." }, format: { type: "string", enum: ["text", "json"], description: "Output format. text adds a readable signature; json is structured." } }, ["name"]),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Read a type's member names", argv: ["tools", "run", "reflect.type", "UnityEngine.Camera", "--format", "text"] }
    ]
  },
  {
    name: "reflect.member",
    summary: "Look up a single member's full signature on a C# type in the offline reflection cache.",
    description: "Look up a single member's full signature on a C# type in the offline reflection cache. Pass the member as Type.Member (last dot splits type and member). Use this for detailed parameter/return info; use reflect.type for the whole member list.",
    group: "reflect",
    inputSchema: objSchema({ member: { type: "string", description: "Member target as Type.Member, e.g. UnityEngine.Camera.Render." }, reflectDir: { type: "string", description: "Offline reflection cache directory." }, format: { type: "string", enum: ["text", "json"], description: "Output format." } }, ["member"]),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Read one member's full signature", argv: ["tools", "run", "reflect.member", "UnityEngine.Camera.Render"] }
    ]
  },
  {
    name: "reflect.search",
    summary: "Search cached C# types and members by query, returning ranked, paged results.",
    description: "Search cached C# types and members by query, returning ranked, paged results. Use this to discover exact type/member names before reflect.type or reflect.member. Results include both type and member matches scored by the query.",
    group: "reflect",
    inputSchema: objSchema({ query: { type: "string", description: "Search text matched against type and member names." }, reflectDir: { type: "string", description: "Offline reflection cache directory." }, pageSize: { type: "integer", default: 100, description: "Maximum results per page." }, cursor: { type: "integer", default: 0, description: "Zero-based result offset." }, format: { type: "string", enum: ["text", "json"], description: "Output format." } }, ["query"]),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Search for Render-related members", argv: ["tools", "run", "reflect.search", "Render", "--page-size", "10"] }
    ]
  },
                        {
    name: "diagnose.capabilities",
    summary: "Summarize built-in CLI capabilities, tool/context catalog counts, extension availability, and optional live Unity bridge capabilities when --bridge-endpoint is provided.",
    description: "Summarize built-in CLI capabilities, tool/context catalog counts, extension availability, and optional live Unity bridge capabilities when --bridge-endpoint is provided. Safe to run offline; runtime data is best-effort.",
    group: "diagnose",
    inputSchema: objSchema({ json: { type: "boolean", description: "Return JSON mode even when the caller requested text output." }, bridgeEndpoint: { type: "string", description: "Optional live Unity bridge endpoint used to include diagnose.preflight runtime data." }, project: { type: "string", description: "Optional Unity project path used for extension and custom context discovery." } }),
    outputSchema: objSchema({ command: { type: "string" }, mode: { type: "string" }, requiresUnity: { type: "boolean" }, static: { type: "object" }, runtime: { type: "object" }, contextDiscovery: { type: "object" }, extensions: { type: "object" }, bridgeEndpoint: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Read offline CLI capabilities", argv: ["tools", "run", "diagnose.capabilities", "--input", "{}"] },
      { description: "Include live bridge capabilities", argv: ["tools", "run", "diagnose.capabilities", "--input", "{\"bridgeEndpoint\":\"http://127.0.0.1:47395\"}"] }
    ],
  },
  {
    name: "test.run",
    summary: "Run Unity EditMode or PlayMode tests as a batch job.",
    description: "Run Unity EditMode or PlayMode tests as a batch job. Returns a job ID that can be polled for status.",
    group: "test",
    inputSchema: objSchema({
      platform: { type: "string", enum: ["EditMode", "PlayMode"], default: "EditMode" },
      project: { type: "string" },
      unity: { type: "string" },
      wait: { type: "boolean" },
      timeoutMs: { type: "integer" }
    }),
    destructiveHint: false,
    sideEffects: ["session-affecting"],
    playModeSupport: "session-affecting",
    examples: [{ description: "Run EditMode tests", argv: ["tools", "run", "test.run", "--project", ".", "--platform", "EditMode"] }],
  },
        {
    name: "build.run",
    summary: "Trigger a Unity batchmode player build as a long-running job.",
    description: "Trigger a Unity batchmode player build as a long-running job. Returns a job ID; poll job status/logs instead of rerunning. This writes build output and can import/compile assets.",
    group: "build",
    inputSchema: objSchema({
      target: { type: "string", enum: ["StandaloneWindows64", "StandaloneWindows", "StandaloneOSX", "StandaloneLinux64", "WebGL", "iOS", "Android"], default: "StandaloneWindows64", description: "BuildTarget enum name for the batchmode build." },
      output: { type: "string", description: "Filesystem output path for the built player. With targets, use {target} placeholder or a directory prefix." },
      targets: { type: "string", description: "Comma-separated BuildTarget names for multi-platform batch builds, e.g. StandaloneWindows64,WebGL." },
      project: { type: "string", description: "Unity project path." },
      unity: { type: "string", description: "Unity executable path when not discoverable from defaults." },
      wait: { type: "boolean", description: "Wait for job completion instead of returning immediately." },
      timeoutMs: { type: "integer", description: "Maximum wait time when wait=true." }
    }),
    destructiveHint: false,
    sideEffects: ["build-output-write", "asset-import", "compile"],
    playModeSupport: "editor-only",
    examples: [
      { description: "Start a Windows build job", argv: ["tools", "run", "build.run", "--input", "{\"project\":\".\",\"target\":\"StandaloneWindows64\",\"output\":\"Builds/App.exe\"}"] },
      { description: "Start multi-platform build jobs", argv: ["tools", "run", "build.run", "--input", "{\"project\":\".\",\"targets\":\"StandaloneWindows64,WebGL\",\"output\":\"Builds/{target}/Player\"}"] }
    ],
  },
  {
    name: "build.status",
    summary: "Read build job status using build-domain language.",
    description: "Read build job status using build-domain language. This wraps the generic job store but only accepts jobs created by build.run, returning target/outputPath/logPath/result fields without requiring AI to switch to top-level jobs status.",
    group: "build",
    inputSchema: objSchema({ jobId: { type: "string", description: "Build jobId returned by build.run." }, id: { type: "string", description: "Alias of jobId." } }, ["jobId"]),
    destructiveHint: false,
    sideEffects: [],
    playModeSupport: "supported",
    examples: [{ description: "Check a build job", argv: ["tools", "run", "build.status", "--input", "{\"jobId\":\"job_xxx\"}"] }],
  },
  {
    name: "build.cancel",
    summary: "Cancel a build job by build jobId.",
    description: "Cancel a build job by build jobId. This is the build-domain equivalent of jobs cancel and only accepts jobs created by build.run; it attempts immediate process termination when a pid is known and returns cancellation semantics.",
    group: "build",
    inputSchema: objSchema({ jobId: { type: "string", description: "Build jobId returned by build.run." }, id: { type: "string", description: "Alias of jobId." } }, ["jobId"]),
    destructiveHint: false,
    sideEffects: ["job-cancel"],
    playModeSupport: "session-affecting",
    examples: [{ description: "Cancel a build job", argv: ["tools", "run", "build.cancel", "--input", "{\"jobId\":\"job_xxx\"}"] }],
  },
      ];
