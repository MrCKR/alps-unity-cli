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
    title: "Package List",
    description: "List installed packages and their versions in the project manifest.",
    group: "package",
    inputSchema: objSchema({ project: { type: "string" } }),
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools run package.list",
    docsTopic: "package",
    equivalentUnityMcp: "manage_packages.list_packages"
  },
  {
    name: "package.status",
    title: "Package Status",
    description: "Summarize manifest and lock state for Unity packages in the project.",
    group: "package",
    inputSchema: objSchema({ project: { type: "string" } }),
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools run package.status",
    docsTopic: "package",
    equivalentUnityMcp: "manage_packages.status"
  },
  {
    name: "package.info",
    title: "Package Info",
    description: "Show detailed info for an installed package.",
    group: "package",
    inputSchema: objSchema({ name: { type: "string" }, project: { type: "string" } }, ["name"]),
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools run package.info",
    docsTopic: "package",
    equivalentUnityMcp: "manage_packages.get_package_info"
  },
  {
    name: "package.search",
    title: "Package Search",
    description: "Search a Unity package registry by name.",
    group: "package",
    inputSchema: objSchema({ query: { type: "string" }, registry: { type: "string" } }, ["query"]),
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools run package.search",
    docsTopic: "package",
    equivalentUnityMcp: "manage_packages.search_packages"
  },
  {
    name: "package.install",
    title: "Package Install",
    description: "Install a package into the project manifest and optionally resolve.",
    group: "package",
    inputSchema: objSchema({ name: { type: "string" }, version: { type: "string" }, project: { type: "string" }, unity: { type: "string" }, wait: { type: "boolean" } }, ["name"]),
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "tools run package.install",
    docsTopic: "package",
    equivalentUnityMcp: "manage_packages.add_package"
  },
  {
    name: "package.remove",
    title: "Package Remove",
    description: "Remove a package from the project manifest.",
    group: "package",
    inputSchema: objSchema({ name: { type: "string" }, project: { type: "string" }, unity: { type: "string" }, wait: { type: "boolean" } }, ["name"]),
    destructiveHint: true,
    handler: "hybrid",
    cliCommand: "tools run package.remove",
    docsTopic: "package",
    equivalentUnityMcp: "manage_packages.remove_package"
  },
  {
    name: "package.resolve",
    title: "Package Resolve",
    description: "Resolve all packages in the project manifest by running Unity PackageManager. Runs as a batch job.",
    group: "package",
    inputSchema: objSchema({ project: { type: "string" }, unity: { type: "string" }, wait: { type: "boolean", default: true } }),
    destructiveHint: false,
    longRunning: true,
    handler: "hybrid",
    cliCommand: "tools run package.resolve",
    docsTopic: "package",
    equivalentUnityMcp: "manage_packages.resolve_packages"
  },
  {
    name: "package.registry",
    title: "Package Registry",
    description: "Get, set, or clear the primary registry URL in the project manifest.",
    group: "package",
    inputSchema: objSchema({ action: { type: "string", enum: ["get", "set", "clear"] }, url: { type: "string" }, project: { type: "string" } }, ["action"]),
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools run package.registry",
    docsTopic: "package",
    equivalentUnityMcp: "manage_packages.list_registries"
  },
  {
    name: "package.scoped-registry",
    title: "Package Scoped Registry",
    description: "List, add, or remove scoped registries in the project manifest.",
    group: "package",
    inputSchema: objSchema({ action: { type: "string", enum: ["list", "add", "remove"] }, name: { type: "string" }, url: { type: "string" }, scope: { type: "string" }, scopes: { type: "string" }, project: { type: "string" } }, ["action"]),
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools run package.scoped-registry",
    docsTopic: "package",
    equivalentUnityMcp: "manage_packages.add_registry"
  },
      {
    name: "docs.list",
    title: "Docs List",
    description: "List built-in CLI documentation topics.",
    group: "docs",
    inputSchema: objSchema({ format: { type: "string", enum: ["json", "text"] } }),
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools run docs.list",
    docsTopic: "docs",
    equivalentUnityMcp: "unity_docs"
  },
  {
    name: "docs.topic",
    title: "Docs Topic",
    description: "Read a built-in CLI documentation topic.",
    group: "docs",
    inputSchema: objSchema({ topic: { type: "string" }, format: { type: "string", enum: ["json", "text"] } }, ["topic"]),
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools run docs.topic",
    docsTopic: "docs",
    equivalentUnityMcp: "unity_docs"
  },
  {
    name: "docs.get",
    title: "Docs Get",
    description: "Fetch offline Unity API documentation for a specific API name.",
    group: "docs",
    inputSchema: objSchema({ api: { type: "string" }, docsDir: { type: "string" }, unityVersion: { type: "string" }, format: { type: "string", enum: ["json", "text"] } }, ["api"]),
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools run docs.get",
    docsTopic: "docs",
    equivalentUnityMcp: "unity_docs.get_doc"
  },
  {
    name: "docs.search",
    title: "Docs Search",
    description: "Search the offline Unity API documentation cache.",
    group: "docs",
    inputSchema: objSchema({ query: { type: "string" }, docsDir: { type: "string" }, pageSize: { type: "integer" }, cursor: { type: "string" }, format: { type: "string" } }, ["query"]),
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools run docs.search",
    docsTopic: "docs",
    equivalentUnityMcp: "unity_docs.lookup"
  },
  {
    name: "reflect.type",
    title: "Reflect Type",
    description: "Look up a C# type in the offline reflection cache.",
    group: "reflect",
    inputSchema: objSchema({ name: { type: "string" }, reflectDir: { type: "string" }, unityVersion: { type: "string" } }, ["name"]),
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools run reflect.type",
    docsTopic: "reflect",
    equivalentUnityMcp: "unity_reflect.get_type"
  },
  {
    name: "reflect.member",
    title: "Reflect Member",
    description: "Look up a member on a C# type in the offline reflection cache.",
    group: "reflect",
    inputSchema: objSchema({ member: { type: "string" }, reflectDir: { type: "string" } }, ["member"]),
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools run reflect.member",
    docsTopic: "reflect",
    equivalentUnityMcp: "unity_reflect.get_member"
  },
  {
    name: "reflect.search",
    title: "Reflect Search",
    description: "Search types and members in the offline reflection cache.",
    group: "reflect",
    inputSchema: objSchema({ query: { type: "string" }, reflectDir: { type: "string" } }, ["query"]),
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools run reflect.search",
    docsTopic: "reflect",
    equivalentUnityMcp: "unity_reflect.search"
  },
                        {
    name: "diagnose.capabilities",
    title: "Capabilities",
    description: "Summarize available CLI capabilities, tool catalog fingerprint, and extension availability.",
    group: "diagnose",
    inputSchema: objSchema({ json: { type: "boolean" }, bridgeEndpoint: { type: "string" } }),
    destructiveHint: false,
    handler: "offline",
    cliCommand: "tools run diagnose.capabilities",
    docsTopic: "diagnose"
  },
  {
    name: "test.run",
    title: "Test Run",
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
    handler: "job",
    cliCommand: "tools run test.run",
    docsTopic: "test",
    examples: [{ description: "Run EditMode tests", argv: ["tools", "run", "test.run", "--project", ".", "--platform", "EditMode"] }],
    cancellationSemantics: "Immediate process cancellation when a Unity batchmode pid is known; queued or pid-less jobs are marked cancelled and may already have finished or be waiting for Unity cleanup.",
    equivalentUnityMcp: "run_tests"
  },
        {
    name: "build.run",
    title: "Build Run",
    description: "Trigger a Unity project build as a batch job. Returns a job ID that can be polled for status.",
    group: "build",
    inputSchema: objSchema({
      target: { type: "string", enum: ["StandaloneWindows64", "StandaloneWindows", "StandaloneOSX", "StandaloneLinux64", "WebGL", "iOS", "Android"], default: "StandaloneWindows64" },
      output: { type: "string" },
      project: { type: "string" },
      unity: { type: "string" },
      wait: { type: "boolean" },
      timeoutMs: { type: "integer" }
    }),
    destructiveHint: false,
    longRunning: true,
    handler: "job",
    cliCommand: "tools run build.run",
    docsTopic: "build",
    examples: [{ description: "Build a Windows player", argv: ["tools", "run", "build.run", "--project", ".", "--target", "StandaloneWindows64", "--output", "Builds/App.exe"] }],
    cancellationSemantics: "Immediate process cancellation when a Unity batchmode pid is known; if Unity is already inside build cleanup, the job is marked cancelled and callers should inspect job logs for final filesystem state.",
    equivalentUnityMcp: "manage_build"
  },
      ];
