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
    handler: "offline",
    supportStatus: "cli-only",
    cliCommand: "tools run package.list",
    docsTopic: "package",
    equivalentUnityMcp: "manage_packages.list_packages",
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
    handler: "offline",
    supportStatus: "cli-only",
    cliCommand: "tools run package.status",
    docsTopic: "package",
    equivalentUnityMcp: "manage_packages.status",
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
    handler: "offline",
    supportStatus: "cli-only",
    cliCommand: "tools run package.info",
    docsTopic: "package",
    equivalentUnityMcp: "manage_packages.get_package_info",
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
    handler: "offline",
    supportStatus: "cli-only",
    cliCommand: "tools run package.search",
    docsTopic: "package",
    equivalentUnityMcp: "manage_packages.search_packages",
    examples: [
      { description: "Search a registry", argv: ["tools", "run", "package.search", "--input", "{\"query\":\"textmeshpro\"}"] }
    ]
  },
  {
    name: "package.install",
    summary: "Install a package into the project manifest and optionally resolve via Unity PackageManager.",
    description: "Install a package into the project manifest and optionally resolve via Unity PackageManager. Destructive: manifest change + possible Unity resolve/domain reload. Requires an explicit version because Unity manifests do not accept 'latest'. Use package.search to confirm the name/version first.",
    group: "package",
    inputSchema: objSchema({ name: { type: "string", description: "Package name to install. May include @version, e.g. com.unity.textmeshpro@3.0.7." }, version: { type: "string", description: "Required unless name includes @version. Use a SemVer package version accepted by Unity." }, project: { type: "string", description: "Unity project path." }, unity: { type: "string", description: "Unity executable path for batch resolve." }, wait: { type: "boolean", description: "Wait for resolve job to finish." } }, ["name"]),
    outputSchema: objSchema({ action: { type: "string" }, package: { type: "string" }, manifestPath: { type: "string" }, installedVersion: { type: "string" }, previousVersion: { type: "string" } }),
    destructiveHint: true,
    sideEffects: ["manifest-change", "session-affecting"],
    riskLevel: "high",
    playModeSupport: "session-affecting",
    handler: "hybrid",
    supportStatus: "bridge-job",
    cliCommand: "tools run package.install",
    docsTopic: "package",
    equivalentUnityMcp: "manage_packages.add_package",
    examples: [
      { description: "Install a package", argv: ["tools", "run", "package.install", "--input", "{\"name\":\"com.unity.textmeshpro\",\"version\":\"3.0.7\"}"] }
    ]
  },
  {
    name: "package.remove",
    summary: "Remove a package from the project manifest and optionally resolve.",
    description: "Remove a package from the project manifest and optionally resolve. Destructive: manifest change + possible Unity resolve/domain reload. Requires explicit confirmation; inspect with package.list first.",
    group: "package",
    inputSchema: objSchema({ name: { type: "string", description: "Package name to remove." }, project: { type: "string", description: "Unity project path." }, unity: { type: "string", description: "Unity executable path for batch resolve." }, wait: { type: "boolean", description: "Wait for resolve job to finish." } }, ["name"]),
    outputSchema: objSchema({ action: { type: "string" }, package: { type: "string" }, manifestPath: { type: "string" }, removedVersion: { type: "string" } }),
    destructiveHint: true,
    supportsDryRun: true,
    sideEffects: ["manifest-change", "session-affecting"],
    riskLevel: "high",
    playModeSupport: "session-affecting",
    handler: "hybrid",
    supportStatus: "bridge-job",
    cliCommand: "tools run package.remove",
    docsTopic: "package",
    equivalentUnityMcp: "manage_packages.remove_package",
    examples: [
      { description: "Preview removing a package", argv: ["tools", "run", "package.remove", "--input", "{\"name\":\"com.unity.textmeshpro\"}", "--dry-run"] }
    ]
  },
  {
    name: "package.resolve",
    summary: "Resolve all packages in the project manifest by running Unity PackageManager (batch job).",
    description: "Resolve all packages in the project manifest by running Unity PackageManager (batch job). Long-running; use after manual manifest edits or install/remove. Triggers import/domain reload in Unity.",
    group: "package",
    inputSchema: objSchema({ project: { type: "string", description: "Unity project path." }, unity: { type: "string", description: "Unity executable path." }, wait: { type: "boolean", default: true, description: "Wait for the resolve job to finish." } }),
    destructiveHint: false,
    longRunning: true,
    sideEffects: ["session-affecting"],
    riskLevel: "medium",
    playModeSupport: "session-affecting",
    handler: "hybrid",
    supportStatus: "bridge-job",
    cliCommand: "tools run package.resolve",
    docsTopic: "package",
    equivalentUnityMcp: "manage_packages.resolve_packages"
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
    handler: "offline",
    supportStatus: "cli-only",
    cliCommand: "tools run package.registry",
    docsTopic: "package",
    equivalentUnityMcp: "manage_packages.list_registries",
    examples: [
      { description: "Read the primary registry", argv: ["tools", "run", "package.registry", "--input", "{\"action\":\"get\"}"] }
    ]
  },
  {
    name: "package.scoped-registry",
    summary: "List, add, or remove scoped registries in the project manifest. action=list reads all; add requires name/url/(scope|scopes); remove requires name.",
    description: "List, add, or remove scoped registries in the project manifest. action=list reads all; add requires name/url/(scope|scopes); remove requires name. Offline (manifest edit).",
    group: "package",
    inputSchema: objSchema({ action: { type: "string", enum: ["list", "add", "remove"], description: "Scoped registry action." }, name: { type: "string", description: "Scoped registry name (required for add/remove)." }, url: { type: "string", description: "Registry URL (required for add)." }, scope: { type: "string", description: "Single scope (add)." }, scopes: { type: "string", description: "Comma-separated scopes (add)." }, project: { type: "string", description: "Unity project path." } }, ["action"]),
    destructiveHint: false,
    sideEffects: ["manifest-change"],
    playModeSupport: "supported",
    handler: "offline",
    supportStatus: "cli-only",
    cliCommand: "tools run package.scoped-registry",
    docsTopic: "package",
    equivalentUnityMcp: "manage_packages.add_registry",
    examples: [
      { description: "List scoped registries", argv: ["tools", "run", "package.scoped-registry", "--input", "{\"action\":\"list\"}"] }
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
    handler: "offline",
    cliCommand: "tools run docs.list",
    docsTopic: "docs",
    examples: [
      { description: "List built-in CLI doc topics", argv: ["tools", "run", "docs.list", "--input", "{}"] }
    ],
    equivalentUnityMcp: "unity_docs"
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
    handler: "offline",
    cliCommand: "tools run docs.topic",
    docsTopic: "docs",
    examples: [
      { description: "Read the scene topic", argv: ["tools", "run", "docs.topic", "--input", "{\"topic\":\"scene\"}"] }
    ],
    equivalentUnityMcp: "unity_docs"
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
    handler: "offline",
    cliCommand: "tools run docs.get",
    docsTopic: "docs",
    examples: [
      { description: "Read cached Camera docs", argv: ["tools", "run", "docs.get", "--input", "{\"api\":\"UnityEngine.Camera\",\"docsDir\":\".cache/unity-docs\",\"format\":\"text\"}"] }
    ],
    equivalentUnityMcp: "unity_docs.get_doc"
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
    handler: "offline",
    cliCommand: "tools run docs.search",
    docsTopic: "docs",
    examples: [
      { description: "Search cached Unity docs", argv: ["tools", "run", "docs.search", "--input", "{\"query\":\"Camera\",\"docsDir\":\".cache/unity-docs\",\"pageSize\":5}"] }
    ],
    equivalentUnityMcp: "unity_docs.lookup"
  },
  {
    name: "reflect.type",
    summary: "Look up a C# type and its member names in the offline Unity reflection cache.",
    description: "Look up a C# type and its member names in the offline Unity reflection cache. Use this to verify a type exists and read its public member names before writing C# or calling reflection.method.*. Use reflect.search first when the exact type name is unknown.",
    group: "reflect",
    inputSchema: objSchema({ name: { type: "string", description: "Fully-qualified or simple C# type name, e.g. UnityEngine.Camera." }, reflectDir: { type: "string", description: "Offline reflection cache directory. Defaults to UNITY_CLI_REFLECT_DIR or <dataDir>/reflect." }, unityVersion: { type: "string", description: "Optional Unity version filter for the cache." }, format: { type: "string", enum: ["text", "json"], description: "Output format. text adds a readable signature; json is structured." } }, ["name"]),
    destructiveHint: false,
    handler: "offline",
    supportStatus: "cli-only",
    playModeSupport: "supported",
    cliCommand: "tools run reflect.type",
    docsTopic: "reflect",
    equivalentUnityMcp: "unity_reflect.get_type",
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
    handler: "offline",
    supportStatus: "cli-only",
    playModeSupport: "supported",
    cliCommand: "tools run reflect.member",
    docsTopic: "reflect",
    equivalentUnityMcp: "unity_reflect.get_member",
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
    handler: "offline",
    supportStatus: "cli-only",
    playModeSupport: "supported",
    cliCommand: "tools run reflect.search",
    docsTopic: "reflect",
    equivalentUnityMcp: "unity_reflect.search",
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
    handler: "offline",
    cliCommand: "tools run diagnose.capabilities",
    docsTopic: "diagnose",
    examples: [
      { description: "Read offline CLI capabilities", argv: ["tools", "run", "diagnose.capabilities", "--input", "{}"] },
      { description: "Include live bridge capabilities", argv: ["tools", "run", "diagnose.capabilities", "--input", "{\"bridgeEndpoint\":\"http://127.0.0.1:47395\"}"] }
    ],
    equivalentUnityMcp: "manage_tools / debug_request_context"
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
    longRunning: true,
    sideEffects: ["session-affecting"],
    riskLevel: "medium",
    playModeSupport: "session-affecting",
    handler: "job",
    supportStatus: "batch-job",
    cliCommand: "tools run test.run",
    docsTopic: "test",
    examples: [{ description: "Run EditMode tests", argv: ["tools", "run", "test.run", "--project", ".", "--platform", "EditMode"] }],
    cancellationSemantics: "Immediate process cancellation when a Unity batchmode pid is known; queued or pid-less jobs are marked cancelled and may already have finished or be waiting for Unity cleanup.",
    equivalentUnityMcp: "run_tests"
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
    longRunning: true,
    handler: "job",
    cliCommand: "tools run build.run",
    docsTopic: "build",
    examples: [
      { description: "Start a Windows build job", argv: ["tools", "run", "build.run", "--input", "{\"project\":\".\",\"target\":\"StandaloneWindows64\",\"output\":\"Builds/App.exe\"}"] },
      { description: "Start multi-platform build jobs", argv: ["tools", "run", "build.run", "--input", "{\"project\":\".\",\"targets\":\"StandaloneWindows64,WebGL\",\"output\":\"Builds/{target}/Player\"}"] }
    ],
    cancellationSemantics: "Immediate process cancellation when a Unity batchmode pid is known; if Unity is already inside build cleanup, the job is marked cancelled and callers should inspect job logs for final filesystem state.",
    equivalentUnityMcp: "manage_build.build"
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
    handler: "offline",
    supportStatus: "cli-only",
    cliCommand: "tools run build.status",
    docsTopic: "build",
    examples: [{ description: "Check a build job", argv: ["tools", "run", "build.status", "--input", "{\"jobId\":\"job_xxx\"}"] }],
    equivalentUnityMcp: "manage_build.status"
  },
  {
    name: "build.cancel",
    summary: "Cancel a build job by build jobId.",
    description: "Cancel a build job by build jobId. This is the build-domain equivalent of jobs cancel and only accepts jobs created by build.run; it attempts immediate process termination when a pid is known and returns cancellation semantics.",
    group: "build",
    inputSchema: objSchema({ jobId: { type: "string", description: "Build jobId returned by build.run." }, id: { type: "string", description: "Alias of jobId." } }, ["jobId"]),
    destructiveHint: false,
    sideEffects: ["job-cancel"],
    riskLevel: "medium",
    playModeSupport: "session-affecting",
    handler: "offline",
    supportStatus: "cli-only",
    cliCommand: "tools run build.cancel",
    docsTopic: "build",
    examples: [{ description: "Cancel a build job", argv: ["tools", "run", "build.cancel", "--input", "{\"jobId\":\"job_xxx\"}"] }],
    cancellationSemantics: "Attempts immediate process-tree termination when the Unity batchmode pid is known; queued or pid-less jobs are marked cancelled.",
    equivalentUnityMcp: "manage_build.cancel"
  },
      ];
