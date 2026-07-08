import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const EDITOR_TOOLS: ToolMetadata[] = [
  {
    name: "editor.console",
    summary: "Legacy alias for reading Console entries; prefer console.get for filters and pagination.",
    description: "Read recent Unity Editor console entries through the legacy editor alias. Prefer console.get for filter/search/cursor pagination. This alias is read-only; use console.clear with --dry-run or --yes for destructive clearing.",
    group: "editor",
    inputSchema: objSchema({ type: { type: "string", enum: ["error", "warning", "log", "assert", "all"], default: "all" }, count: { type: "integer", default: 50, description: "Recent entries to return, clamped to 1..500." } }),
    outputSchema: pageSchema({ type: "object", properties: { index: { type: "integer" }, type: { type: "string" }, message: { type: "string" }, file: { type: "string" }, line: { type: "integer" } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Read recent errors", argv: ["tools", "run", "editor.console", "--input", "{\"type\":\"error\",\"count\":20}"] }
    ],
  },
  {
    name: "editor.state",
    summary: "Read Editor readiness: PlayMode, busy flags, active scene, Prefab Stage, Bridge, and runtime status.",
    description: "Query current Unity Editor readiness: Play Mode, pause, compile/import/build/domain-reload busy flags, active scene, prefab stage, bridge build stamp, and runtime tool availability. Bridge commands run on Unity's main thread; use this before expensive tools and after editor.play/editor.stop/editor.menu to decide whether to retry later or continue. The bridge listener re-registers after domain reload; heartbeat/build stamp changes here are the observable reconnect signal.",
    group: "editor",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ playMode: { type: "object" }, busy: { type: "object", description: "busy.isBusy or busy.compiling/updating/domainReloading/playModeChanging means AI should wait/retry before stateful operations." }, activeScene: { type: "object" }, prefabStage: { type: ["object", "null"] }, bridge: { type: "object" }, runtime: { type: "object" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Read editor state", argv: ["tools", "run", "editor.state", "--input", "{}"] }
    ],
  },
  {
    name: "editor.play",
    summary: "Request Unity Editor Play Mode.",
    description: "Request Unity Editor Play Mode. This changes the editor session and may trigger script reload or Play Mode transition; poll editor.state until busy.playModeChanging is false before running runtime-only tools.",
    group: "editor",
    inputSchema: objSchema({ timeoutMs: { type: "integer", default: 7000, description: "Maximum wait for the initial Play Mode transition acknowledgement." } }),
    outputSchema: objSchema({ result: { type: "object" }, state: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["playmode-transition", "domain-reload"],
    examples: [
      { description: "Enter Play Mode", argv: ["tools", "run", "editor.play", "--input", "{\"timeoutMs\":7000}"] }
    ],
  },
  {
    name: "editor.pause",
    summary: "Toggle Unity Editor pause state.",
    description: "Toggle Unity Editor pause state. This changes the current editor/runtime session; call editor.state afterwards to confirm isPaused.",
    group: "editor",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ result: { type: "object" }, state: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["playmode-pause"],
    examples: [
      { description: "Toggle pause", argv: ["tools", "run", "editor.pause", "--input", "{}"] }
    ],
  },
  {
    name: "editor.stop",
    summary: "Request exit from Unity Editor Play Mode.",
    description: "Request exit from Unity Editor Play Mode. The command returns immediately after setting isPlaying=false; poll editor.state until busy.playModeChanging is false before relying on Edit Mode state.",
    group: "editor",
    inputSchema: objSchema({ timeoutMs: { type: "integer", default: 7000, description: "Reserved timeout field for consistency with editor.play; callers should still poll editor.state." } }),
    outputSchema: objSchema({ result: { type: "object" }, state: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["playmode-transition", "domain-reload"],
    examples: [
      { description: "Exit Play Mode", argv: ["tools", "run", "editor.stop", "--input", "{}"] }
    ],
  },
  {
    name: "editor.undo",
    summary: "Run Unity Editor Undo.PerformUndo for the last undoable operation.",
    description: "Run Unity Editor Undo.PerformUndo for the last undoable operation. This can revert scene, asset, or editor state depending on the current undo stack; call editor.state or relevant context afterwards to verify the result.",
    group: "editor",
    inputSchema: objSchema({ timeoutMs: { type: "integer", default: 7000 } }),
    outputSchema: objSchema({ performed: { type: "boolean" }, state: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["undo-stack", "scene-or-asset-modify"],
    examples: [
      { description: "Undo last editor operation", argv: ["tools", "run", "editor.undo", "--input", "{}"] }
    ],
  },
  {
    name: "editor.redo",
    summary: "Run Unity Editor Undo.PerformRedo for the last redoable operation.",
    description: "Run Unity Editor Undo.PerformRedo for the last redoable operation. This can reapply scene, asset, or editor state changes from the redo stack; verify with editor.state or a domain-specific context.",
    group: "editor",
    inputSchema: objSchema({ timeoutMs: { type: "integer", default: 7000 } }),
    outputSchema: objSchema({ performed: { type: "boolean" }, state: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["undo-stack", "scene-or-asset-modify"],
    examples: [
      { description: "Redo last editor operation", argv: ["tools", "run", "editor.redo", "--input", "{}"] }
    ],
  },
  {
    name: "editor.resolution",
    summary: "Set PlayerSettings default screen width/height and optional fullscreen mode.",
    description: "Set PlayerSettings default screen width/height and optional fullscreen mode. This writes project/editor settings and may affect later Game View or Player build behavior.",
    group: "editor",
    inputSchema: objSchema({ width: { type: "integer", minimum: 1, description: "Default screen width in pixels." }, height: { type: "integer", minimum: 1, description: "Default screen height in pixels." }, fullscreen: { type: "boolean", description: "When provided, sets PlayerSettings.fullScreenMode to FullScreenWindow or Windowed." } }),
    outputSchema: objSchema({ resolution: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["player-settings", "project-settings-write"],
    examples: [
      { description: "Set default player resolution", argv: ["tools", "run", "editor.resolution", "--input", "{\"width\":1280,\"height\":720,\"fullscreen\":false}"] }
    ],
  },
  {
    name: "editor.menu",
    summary: "Invoke a Unity Editor menu item by exact path, such as Assets/Refresh or File/Save.",
    description: "Invoke a Unity Editor menu item by exact path, such as Assets/Refresh or File/Save. Menu actions are arbitrary editor commands, so this requires --yes and should be followed by editor.state or console.get when the menu can import, compile, save, or open UI.",
    group: "editor",
    inputSchema: objSchema({ path: { type: "string", description: "Exact Unity menu path, e.g. Assets/Refresh or File/Save." }, timeoutMs: { type: "integer", default: 7000, description: "Maximum synchronous menu execution time before E_MENU_TIMEOUT." } }, ["path"]),
    outputSchema: objSchema({ result: { type: "object" }, summary: { type: "object" }, state: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } } }),
    destructiveHint: true,
    playModeSupport: "session-affecting",
    sideEffects: ["editor-menu", "unknown-editor-side-effect"],
    examples: [
      { description: "Refresh assets through Unity menu", argv: ["tools", "run", "editor.menu", "--yes", "--input", "{\"path\":\"Assets/Refresh\",\"timeoutMs\":7000}"] }
    ],
  },
  {
    name: "project.tag",
    summary: "Add or remove a tag in the Unity Tag Manager.",
    description: "Add or remove a tag in the Unity Tag Manager. Returns changed=false when the tag already exists (add) or is absent (remove). Use context read project.tags first to list valid tags before assigning them to GameObjects; new tags become available for go.modify's tag field.",
    group: "project",
    inputSchema: objSchema({ action: { type: "string", enum: ["add", "remove"], description: "Whether to add or remove the tag." }, name: { type: "string", description: "Tag name. Must be non-empty." } }, ["action", "name"]),
    outputSchema: objSchema({
      action: { type: "string" },
      name: { type: "string" },
      changed: { type: "boolean", description: "True when the tag set was modified." },
      plannedEffects: { type: "array", items: objSchema({ type: { type: "string" }, action: { type: "string" }, name: { type: "string" } }) }
    }),
    destructiveHint: false,
    sideEffects: ["project-settings-write"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Add a project tag", argv: ["tools", "run", "project.tag", "--input", "{\"action\":\"add\",\"name\":\"UnityCliProbe\"}"] }
    ]
  },
  {
    name: "project.layer",
    summary: "Add or remove a user layer in the Unity Tag Manager.",
    description: "Add or remove a user layer in the Unity Tag Manager. Built-in layers (0-7) cannot be removed. Optionally pin a layer to a specific index via index (0-31). Returns changed=false when the layer already exists (add) or is absent/built-in (remove). Use context read project.layers first to list valid layers.",
    group: "project",
    inputSchema: objSchema({ action: { type: "string", enum: ["add", "remove"], description: "Whether to add or remove the user layer." }, name: { type: "string", description: "Layer name. Must be non-empty." }, index: { type: "integer", description: "Optional target layer slot index (0-31) when adding." } }, ["action", "name"]),
    outputSchema: objSchema({
      action: { type: "string" },
      name: { type: "string" },
      changed: { type: "boolean", description: "True when the layer set was modified." },
      plannedEffects: { type: "array", items: objSchema({ type: { type: "string" }, action: { type: "string" }, name: { type: "string" } }) }
    }),
    destructiveHint: false,
    sideEffects: ["project-settings-write"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Add a project layer", argv: ["tools", "run", "project.layer", "--input", "{\"action\":\"add\",\"name\":\"UnityCliProbeLayer\"}"] }
    ]
  },
  {
    name: "editor.refresh",
    summary: "Closed-project batch refresh job; prefer asset.refresh for an already open live Editor.",
    description: "Trigger AssetDatabase.Refresh in a separate Unity batch job, optionally requesting script compilation and waiting for readiness. Prefer asset.refresh for an already open live Editor; do not use this against a project already open in Unity because open projects can return UNITY_BUSY due to UnityLockfile.",
    group: "editor",
    inputSchema: objSchema({
      compile: { type: "boolean", default: false, description: "Request script compilation after refresh." },
      project: { type: "string", description: "Unity project path to open in batchmode." },
      unity: { type: "string", description: "Path to Unity executable for batchmode refresh." },
      wait: { type: "boolean", default: false, description: "Wait for the refresh job to complete before returning final status; otherwise returns the queued job immediately." },
      timeoutMs: { type: "integer", description: "Maximum Unity batchmode process lifetime in milliseconds." },
      waitTimeoutMs: { type: "integer", description: "Maximum wait time for job completion when wait=true." }
    }),
    outputSchema: objSchema({
      jobId: { type: "string" },
      kind: { type: "string" },
      status: { type: "string" },
      requestId: { type: "string" },
      projectPath: { type: "string" },
      unityPath: { type: "string" },
      logPath: { type: "string" },
      pid: { type: "integer" },
      exitCode: { type: "integer" },
      createdAt: { type: "string" },
      updatedAt: { type: "string" },
      startedAt: { type: "string" },
      finishedAt: { type: "string" },
      command: { type: "array", items: { type: "string" } },
      result: { type: "object" },
      error: { type: "object" },
      cancellation: { type: "object" },
      logTail: { type: "array", items: { type: "string" } }
    }),
    destructiveHint: false,
    playModeSupport: "editor-only",
    sideEffects: ["asset-refresh", "script-compile", "domain-reload"],
    examples: [
      { description: "Refresh assets in batchmode", argv: ["tools", "run", "editor.refresh", "--input", "{\"project\":\"./Dev/UnityProject\",\"unity\":\"C:/Program Files/Unity/Hub/Editor/2022.3.61f1/Editor/Unity.exe\",\"wait\":true}"] },
      { description: "Refresh and request compilation", argv: ["tools", "run", "editor.refresh", "--input", "{\"project\":\"./Dev/UnityProject\",\"compile\":true,\"wait\":true}"] }
    ],
  },
  {
    name: "editor.compile",
    summary: "Request script compilation in the live Unity Editor, optionally refreshing assets and waiting for readiness.",
    description: "Request script compilation in the live Unity Editor without reading or clearing Console entries. By default it first calls AssetDatabase.Refresh, then calls CompilationPipeline.RequestScriptCompilation. If Unity is already compiling or importing, it does not issue another refresh/compile request; requested=false and skippedReason explain the existing busy state, and waitReady=true waits for that state to settle. Use editor.compile-diagnostics when you also need Console compiler diagnostics in the same call. This tool does not save project files and does not start a batchmode Editor.",
    group: "editor",
    inputSchema: objSchema({
      refresh: { type: "boolean", default: true, description: "Refresh AssetDatabase before requesting script compilation." },
      waitReady: { type: "boolean", default: false, description: "Wait until compile/import/domain reload settles before returning." },
      timeoutMs: { type: "integer", default: 45000, maximum: 55000, description: "Maximum wait time when waitReady=true." }
    }),
    outputSchema: objSchema({
      command: { type: "string" },
      requested: { type: "boolean", description: "True when this call issued a new CompilationPipeline.RequestScriptCompilation request; false when Unity was already compiling or importing." },
      skippedReason: { type: ["string", "null"], enum: ["already-compiling", "already-importing", null], description: "Why no new compile request was issued." },
      refresh: { type: "boolean" },
      waitReady: { type: "boolean" },
      wasCompiling: { type: "boolean" },
      wasUpdating: { type: "boolean" },
      state: { type: "object", properties: { compiling: { type: "boolean" }, updating: { type: "boolean" }, domainReloading: { type: "boolean" }, isBusy: { type: "boolean" } } },
      plannedEffects: { type: "array", items: { type: "object" } },
      logPath: { type: "string" }
    }),
    destructiveHint: false,
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    sideEffects: ["asset-refresh", "script-compile", "domain-reload"],
    examples: [
      { description: "Request live Editor script compilation", argv: ["tools", "run", "editor.compile", "--input", "{\"refresh\":true}"] },
      { description: "Request compile and wait until Unity is ready", argv: ["tools", "run", "editor.compile", "--input", "{\"refresh\":true,\"waitReady\":true,\"timeoutMs\":45000}"] },
      { description: "Preview compile request side effects", argv: ["tools", "run", "editor.compile", "--dry-run", "--input", "{\"waitReady\":true}"] }
    ],
  },
  {
    name: "editor.compile-diagnostics",
    summary: "Refresh/wait live Editor, then return Console compile diagnostics for C# edit loops.",
    description: "Refresh the live Unity Editor AssetDatabase, wait for the Editor to become idle, and read recent Console errors in one call. This is the live-bridge compile diagnostics loop for AI workflows after editing C# files: it does not clear Console entries, does not save project files, and does not start a batchmode Editor. The CLI orchestrates existing live Bridge calls (asset.refresh, context read editor.state, console.get), retrying transient compile/import/domain-reload states within waitTimeoutMs. Use refresh=false to only wait/read Console, or pass path/assetPath to import a specific Assets/* path before collecting diagnostics. Use dryRun=true or --dry-run to preview the planned refresh/wait/console steps without calling the Bridge. job=true/--job is rejected with E_TOOL_JOB_UNSUPPORTED because live Console diagnostics require a live Editor; use editor.refresh for batchmode refresh jobs.",
    group: "editor",
    inputSchema: objSchema({
      refresh: { type: "boolean", default: true, description: "Whether to trigger a live asset.refresh before waiting and reading Console." },
      path: { type: "string", description: "Optional Assets/* path to import instead of refreshing the whole AssetDatabase." },
      assetPath: { type: "string", description: "Alias of path." },
      forceUpdate: { type: "boolean", default: false, description: "Forward ImportAssetOptions.ForceUpdate to asset.refresh when refreshing." },
      importRecursive: { type: "boolean", default: false, description: "Forward ImportAssetOptions.ImportRecursive for folder imports." },
      waitReady: { type: "boolean", default: true, description: "Wait for Unity compile/import/domain reload busy state to settle before reading Console." },
      waitTimeoutMs: { type: "integer", default: 45000, maximum: 55000, description: "Maximum milliseconds for CLI-side transient retry and editor.state wait-ready." },
      pollIntervalMs: { type: "integer", default: 1000, maximum: 5000, description: "Retry interval while Unity reports compile/import/domain reload transient states." },
      type: { type: "string", enum: ["error", "warning", "log", "assert", "all"], default: "error", description: "Console entry type to read after compile settles." },
      filter: { type: "string", description: "Optional message/path substring filter forwarded to console.get." },
      search: { type: "string", description: "Alias of filter." },
      count: { type: "integer", default: 100, maximum: 500, description: "Maximum Console entries returned newest-first." },
      dryRun: { type: "boolean", default: false, description: "Preview planned refresh/wait/console-read steps without calling the live Bridge." },
      job: { type: "boolean", default: false, description: "Unsupported for this live diagnostics loop; true returns E_TOOL_JOB_UNSUPPORTED. Use editor.refresh for batchmode refresh jobs." }
    }),
    outputSchema: objSchema({
      command: { type: "string" },
      dryRun: { type: "boolean", description: "True only when dryRun/--dry-run was requested." },
      status: { type: "string", enum: ["clean", "compile_errors", "console_errors", "wait_timeout", "dry_run"] },
      refresh: { type: "object" },
      wait: { type: "object" },
      console: { type: "object" },
      compile: { type: "object", properties: { ok: { type: "boolean" }, hasErrors: { type: "boolean" }, errorCount: { type: "integer" }, consoleErrorCount: { type: "integer" }, diagnostics: { type: "array", items: { type: "object" } } } },
      attempts: { type: "object" },
      plannedEffects: { type: "array", items: { type: "object" } },
      nextActions: { type: "array", items: { type: "string" } }
    }),
    destructiveHint: false,
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    sideEffects: ["asset-refresh", "script-compile", "domain-reload", "console-read"],
    supportStatus: "live-bridge",
    examples: [
      { description: "Refresh, wait, and read recent compiler diagnostics", argv: ["tools", "run", "editor.compile-diagnostics", "--input", "{\"waitTimeoutMs\":45000,\"count\":100}"] },
      { description: "Import one changed script before reading diagnostics", argv: ["tools", "run", "editor.compile-diagnostics", "--input", "{\"path\":\"Assets/Scripts/Player.cs\",\"forceUpdate\":true}"] },
      { description: "Only wait and read Console without triggering refresh", argv: ["tools", "run", "editor.compile-diagnostics", "--input", "{\"refresh\":false,\"count\":50}"] },
      { description: "Preview planned refresh/wait/console steps", argv: ["tools", "run", "editor.compile-diagnostics", "--dry-run", "--input", "{\"path\":\"Assets/Scripts/Player.cs\"}"] }
    ],
  },
  {
    name: "editor.launch-dialog.dismiss",
    summary: "Dismiss a visible Unity launch-errors or Safe Mode desktop dialog by clicking Ignore.",
    description: "Dismiss a visible Unity launch-errors or Safe Mode desktop dialog by clicking the Ignore button. This is a Windows desktop helper for Unity startup blocks such as Enter Safe Mode? or compiler-errors-at-launch dialogs; it does not open Unity and does not modify project files. Pass processId/pid when multiple Unity Editors are running so only that Unity.exe process is scanned. If no matching dialog is visible before timeoutMs, the tool still succeeds with dismissed=false and status=not_found. Use --dry-run to preview the planned desktop click without scanning or clicking.",
    group: "editor",
    inputSchema: objSchema({
      timeoutMs: { type: "integer", default: 5000, description: "Maximum polling time in milliseconds. Use 0 for a no-op capability probe." },
      intervalMs: { type: "integer", default: 250, description: "Polling interval in milliseconds while waiting for the dialog." },
      processId: { type: "integer", description: "Optional Unity.exe process id to limit the scan. Alias: pid." },
      pid: { type: "integer", description: "Alias for processId." }
    }),
    outputSchema: objSchema({
      command: { type: "string" },
      platform: { type: "string" },
      supported: { type: "boolean" },
      status: { type: "string", enum: ["dismissed", "not_found", "unsupported", "error", "dry_run"] },
      dismissed: { type: "boolean" },
      dryRun: { type: "boolean" },
      attempts: { type: "integer" },
      timeoutMs: { type: "integer" },
      intervalMs: { type: "integer" },
      processId: { type: "integer" },
      title: { type: "string" },
      button: { type: "string" },
      lastError: { type: "string" },
      titleFragments: { type: "array", items: { type: "string" } },
      durationMs: { type: "integer" },
      plannedEffects: { type: "array", items: { type: "object" } },
      nextActions: { type: "array", items: { type: "string" } }
    }),
    destructiveHint: false,
    supportsDryRun: true,
    playModeSupport: "editor-only",
    sideEffects: ["desktop-dialog-click"],
    examples: [
      { description: "Dismiss any visible Unity launch error dialog", argv: ["tools", "run", "editor.launch-dialog.dismiss", "--input", "{\"timeoutMs\":5000}"] },
      { description: "Limit dismiss to one Unity process", argv: ["tools", "run", "editor.launch-dialog.dismiss", "--input", "{\"processId\":44960,\"timeoutMs\":5000}"] },
      { description: "Preview the desktop click", argv: ["tools", "run", "editor.launch-dialog.dismiss", "--dry-run", "--input", "{\"pid\":44960}"] }
    ],
  },
  {
    name: "editor.selection.set",
    summary: "Set the active Unity Editor selection by instanceId, target name, or hierarchy path.",
    description: "Set the active Unity Editor selection by instanceId, target name, or hierarchy path. Reading selection is a Context (`context read editor.selection`); use --dry-run before selecting ambiguous scene objects.",
    group: "editor",
    inputSchema: objSchema({ instanceId: { type: "integer", description: "Unity instance ID from a prior tool/context result." }, target: { type: "string", description: "GameObject name or hierarchy path." }, path: { type: "string", description: "Alias for target; hierarchy path preferred for duplicate names." } }),
    outputSchema: objSchema({ command: { type: "string" }, dryRun: { type: "boolean" }, target: { type: "string" }, selected: { type: "string" }, typeName: { type: "string" }, logPath: { type: "string" } }),
    destructiveHint: false,
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    sideEffects: ["editor-selection"],
    examples: [
      { description: "Select a GameObject by path", argv: ["tools", "run", "editor.selection.set", "--yes", "--input", "{\"target\":\"Player\"}"] },
      { description: "Dry-run selection", argv: ["tools", "run", "editor.selection.set", "--dry-run", "--input", "{\"target\":\"Player\"}"] }
    ],
  },
  {
    name: "editor.active-tool.set",
    summary: "Set the Unity Editor active transform tool (View/Move/Rotate/Scale/Rect/Transform).",
    description: "Set the Unity Editor active transform tool (View/Move/Rotate/Scale/Rect/Transform). This changes editor UI state only; it is not unity_cli tool activation or tool discovery.",
    group: "editor",
    inputSchema: objSchema({ tool: { type: "string", enum: ["view", "move", "rotate", "scale", "rect", "transform"], description: "Unity Editor transform tool to activate." } }, ["tool"]),
    outputSchema: objSchema({ command: { type: "string" }, tool: { type: "string" }, editorTool: { type: "string" }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    sideEffects: ["editor-active-tool"],
    examples: [
      { description: "Activate Move tool", argv: ["tools", "run", "editor.active-tool.set", "--yes", "--input", "{\"tool\":\"move\"}"] }
    ],
  }
];
