import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const CLI_ONLY_TOOLS: ToolMetadata[] = [
  {
    name: "cli.init",
    title: "Init Project",
    description: "Deploy the Unity Bridge package into a project and optionally refresh the open editor.",
    group: "cli",
    inputSchema: objSchema({ project: { type: "string" }, unity: { type: "string" }, wait: { type: "boolean" } }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "init",
    docsTopic: "init",
    examples: [
      { description: "Init current directory", argv: ["init"] }
    ]
  },
  {
    name: "cli.package.list",
    title: "Package List",
    description: "List installed packages and their versions in the project manifest.",
    group: "cli",
    inputSchema: objSchema({ project: { type: "string" } }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "package list",
    docsTopic: "package"
  },
  {
    name: "cli.package.status",
    title: "Package Status",
    description: "Summarize manifest and lock state for Unity packages in the project.",
    group: "cli",
    inputSchema: objSchema({ project: { type: "string" } }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "package status",
    docsTopic: "package",
    equivalentUnityMcp: "manage_package.status"
  },
  {
    name: "cli.package.info",
    title: "Package Info",
    description: "Show detailed info for an installed package.",
    group: "cli",
    inputSchema: objSchema({ name: { type: "string" }, project: { type: "string" } }, ["name"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "package info",
    docsTopic: "package"
  },
  {
    name: "cli.package.search",
    title: "Package Search",
    description: "Search a Unity package registry by name.",
    group: "cli",
    inputSchema: objSchema({ query: { type: "string" }, registry: { type: "string" } }, ["query"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "package search",
    docsTopic: "package"
  },
  {
    name: "cli.package.install",
    title: "Package Install",
    description: "Install a package into the project manifest and optionally resolve.",
    group: "cli",
    inputSchema: objSchema({ name: { type: "string" }, version: { type: "string" }, project: { type: "string" }, unity: { type: "string" }, wait: { type: "boolean" } }, ["name"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "package install",
    docsTopic: "package"
  },
  {
    name: "cli.package.remove",
    title: "Package Remove",
    description: "Remove a package from the project manifest.",
    group: "cli",
    inputSchema: objSchema({ name: { type: "string" }, project: { type: "string" }, unity: { type: "string" }, wait: { type: "boolean" } }, ["name"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "hybrid",
    cliCommand: "package remove",
    docsTopic: "package"
  },
  {
    name: "cli.batch.schema",
    title: "Batch Schema",
    description: "Print the JSON Schema for first-class CLI batch orchestration files.",
    group: "cli",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ properties: { type: "object" }, required: { type: "array", items: { type: "string" } } }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "batch schema",
    docsTopic: "batch",
    examples: [
      { description: "Print batch JSON schema", argv: ["batch", "schema"] }
    ]
  },
  {
    name: "cli.batch.run",
    title: "Batch Run",
    description: "Execute a JSON batch file containing multiple CLI commands.",
    group: "cli",
    inputSchema: objSchema({ file: { type: "string" }, preflight: { type: "boolean" }, failFast: { type: "boolean" }, parallel: { type: "integer" } }, ["file"]),
    outputSchema: objSchema({ batchRequestId: { type: "string" }, summary: { type: "object" }, logIndex: { type: "object" }, steps: { type: "array" } }),
    readOnlyHint: false,
    destructiveHint: true,
    supportsDryRun: true,
    handler: "offline",
    cliCommand: "batch run",
    docsTopic: "batch",
    cancellationSemantics: "Batch orchestration itself is synchronous CLI work. Long-running steps return jobId/logPath entries; cancel those child jobs with job cancel --id <jobId>. Already completed or skipped steps are not rolled back.",
    examples: [
      { description: "Run batch with preflight", argv: ["batch", "run", "batch.json", "--preflight"] },
      { description: "Run batch and inspect child jobs", argv: ["batch", "run", "batch.json", "--fail-fast", "true", "--parallel", "1"] }
    ]
  },
  {
    name: "cli.docs.get",
    title: "Docs Get",
    description: "Fetch offline Unity API documentation for a specific API name.",
    group: "cli",
    inputSchema: objSchema({ api: { type: "string" }, docsDir: { type: "string" }, unityVersion: { type: "string" }, format: { type: "string", enum: ["json", "text"] } }, ["api"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "docs get",
    docsTopic: "docs"
  },
  {
    name: "cli.docs.search",
    title: "Docs Search",
    description: "Search the offline Unity API documentation cache.",
    group: "cli",
    inputSchema: objSchema({ query: { type: "string" }, docsDir: { type: "string" }, pageSize: { type: "integer" }, cursor: { type: "string" }, format: { type: "string" } }, ["query"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "docs search",
    docsTopic: "docs"
  },
  {
    name: "cli.reflect.type",
    title: "Reflect Type",
    description: "Look up a C# type in the offline reflection cache.",
    group: "cli",
    inputSchema: objSchema({ name: { type: "string" }, reflectDir: { type: "string" }, unityVersion: { type: "string" } }, ["name"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "reflect type",
    docsTopic: "reflect"
  },
  {
    name: "cli.reflect.member",
    title: "Reflect Member",
    description: "Look up a member on a C# type in the offline reflection cache.",
    group: "cli",
    inputSchema: objSchema({ member: { type: "string" }, reflectDir: { type: "string" } }, ["member"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "reflect member",
    docsTopic: "reflect"
  },
  {
    name: "cli.reflect.search",
    title: "Reflect Search",
    description: "Search types and members in the offline reflection cache.",
    group: "cli",
    inputSchema: objSchema({ query: { type: "string" }, reflectDir: { type: "string" } }, ["query"]),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "reflect search",
    docsTopic: "reflect"
  },
  {
    name: "cli.package.resolve",
    title: "Package Resolve",
    description: "Resolve all packages in the project manifest by running Unity PackageManager. Runs as a batch job.",
    group: "cli",
    inputSchema: objSchema({
      project: { type: "string" },
      unity: { type: "string" },
      wait: { type: "boolean", default: true }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    longRunning: true,
    handler: "hybrid",
    cliCommand: "package resolve",
    docsTopic: "package",
    examples: [
      { description: "Resolve packages and wait", argv: ["package", "resolve", "--project", "MyProject", "--unity", "Unity.exe", "--wait"] }
    ]
  },
  {
    name: "cli.package.registry",
    title: "Package Registry",
    description: "Get, set, or clear the primary registry URL in the project manifest.",
    group: "cli",
    inputSchema: objSchema({
      action: { type: "string", enum: ["get", "set", "clear"] },
      url: { type: "string" },
      project: { type: "string" }
    }, ["action"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "package registry",
    docsTopic: "package",
    examples: [
      { description: "Get current registry", argv: ["package", "registry", "get"] },
      { description: "Set registry URL", argv: ["package", "registry", "set", "--url", "https://packages.unity.com"] }
    ]
  },
  {
    name: "cli.package.scoped-registry",
    title: "Package Scoped Registry",
    description: "List, add, or remove scoped registries in the project manifest.",
    group: "cli",
    inputSchema: objSchema({
      action: { type: "string", enum: ["list", "add", "remove"] },
      name: { type: "string" },
      url: { type: "string" },
      scope: { type: "string" },
      scopes: { type: "string" },
      project: { type: "string" }
    }, ["action"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "package scoped-registry",
    docsTopic: "package",
    examples: [
      { description: "List scoped registries", argv: ["package", "scoped-registry", "list"] },
      { description: "Add scoped registry", argv: ["package", "scoped-registry", "add", "--name", "MyRegistry", "--url", "https://my.registry.com", "--scope", "com.mycompany"] }
    ]
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
    readOnlyHint: false,
    destructiveHint: false,
    longRunning: true,
    handler: "job",
    cliCommand: "test run",
    docsTopic: "test",
    cancellationSemantics: "Immediate process cancellation when a Unity batchmode pid is known; queued or pid-less jobs are marked cancelled and may already have finished or be waiting for Unity cleanup.",
    examples: [
      { description: "Run EditMode tests as job", argv: ["test", "run"] },
      { description: "Run PlayMode tests and wait", argv: ["test", "run", "--platform", "PlayMode", "--wait"] }
    ]
  },
  {
    name: "test.poll",
    title: "Test Poll",
    description: "Poll the status of a running test job. Equivalent to `job status --id <jobId>`.",
    group: "test",
    inputSchema: objSchema({ id: { type: "string" } }, ["id"]),
    outputSchema: objSchema({
      jobId: { type: "string" },
      kind: { type: "string" },
      status: { type: "string" },
      exitCode: { type: "integer" },
      logPath: { type: "string" }
    }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "test poll",
    docsTopic: "test",
    examples: [
      { description: "Poll test job status", argv: ["test", "poll", "--id", "job_abc123"] }
    ]
  },
  {
    name: "test.logs",
    title: "Test Logs",
    description: "Read tail lines from a Unity test job log. Equivalent to `job logs --id <jobId>`.",
    group: "test",
    inputSchema: objSchema({ id: { type: "string" }, tail: { type: "integer" } }, ["id"]),
    outputSchema: objSchema({ jobId: { type: "string" }, logPath: { type: "string" }, lines: { type: "array", items: { type: "string" } } }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "test logs",
    docsTopic: "test",
    examples: [
      { description: "Tail test job logs", argv: ["test", "logs", "--id", "job_abc123", "--tail", "100"] }
    ]
  },
  {
    name: "test.cancel",
    title: "Test Cancel",
    description: "Cancel a running Unity test job. Equivalent to `job cancel --id <jobId>`.",
    group: "test",
    inputSchema: objSchema({ id: { type: "string" } }, ["id"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "offline",
    cliCommand: "test cancel",
    docsTopic: "test",
    cancellationSemantics: "Delegates to job cancel --id <jobId>; attempts immediate process-tree termination when possible, otherwise records cancellation intent in the job store.",
    examples: [
      { description: "Cancel a test job", argv: ["test", "cancel", "--id", "job_abc123"] }
    ]
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
    readOnlyHint: false,
    destructiveHint: false,
    longRunning: true,
    handler: "job",
    cliCommand: "build run",
    docsTopic: "build",
    cancellationSemantics: "Immediate process cancellation when a Unity batchmode pid is known; if Unity is already inside build cleanup, the job is marked cancelled and callers should inspect job logs for final filesystem state.",
    examples: [
      { description: "Build for Windows64 as job", argv: ["build", "run"] },
      { description: "Build and wait for completion", argv: ["build", "run", "--target", "StandaloneWindows64", "--wait"] }
    ]
  },
  {
    name: "build.status",
    title: "Build Status",
    description: "Check the status of a running build job. Equivalent to `job status --id <jobId>`.",
    group: "build",
    inputSchema: objSchema({ id: { type: "string" } }, ["id"]),
    outputSchema: objSchema({
      jobId: { type: "string" },
      kind: { type: "string" },
      status: { type: "string" },
      exitCode: { type: "integer" },
      logPath: { type: "string" }
    }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "build status",
    docsTopic: "build",
    examples: [
      { description: "Check build job status", argv: ["build", "status", "--id", "job_abc123"] }
    ]
  },
  {
    name: "build.logs",
    title: "Build Logs",
    description: "Read tail lines from a Unity build job log. Equivalent to `job logs --id <jobId>`.",
    group: "build",
    inputSchema: objSchema({ id: { type: "string" }, tail: { type: "integer" } }, ["id"]),
    outputSchema: objSchema({ jobId: { type: "string" }, logPath: { type: "string" }, lines: { type: "array", items: { type: "string" } } }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "offline",
    cliCommand: "build logs",
    docsTopic: "build",
    examples: [
      { description: "Tail build job logs", argv: ["build", "logs", "--id", "job_abc123", "--tail", "100"] }
    ]
  },
  {
    name: "build.cancel",
    title: "Build Cancel",
    description: "Cancel a running build job. Equivalent to `job cancel --id <jobId>`.",
    group: "build",
    inputSchema: objSchema({ id: { type: "string" } }, ["id"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "offline",
    cliCommand: "build cancel",
    docsTopic: "build",
    cancellationSemantics: "Delegates to job cancel --id <jobId>; attempts immediate process-tree termination when possible, otherwise records cancellation intent in the job store.",
    examples: [
      { description: "Cancel a build job", argv: ["build", "cancel", "--id", "job_abc123"] }
    ]
  }
];
