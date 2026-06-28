import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const EDITOR_TOOLS: ToolMetadata[] = [
  {
    name: "editor.console",
    summary: "Read recent Unity Editor console entries through the legacy editor alias.",
    description: "Read recent Unity Editor console entries through the legacy editor alias. Prefer console.get for filter/search/cursor pagination. This alias is read-only; use console.clear with --dry-run or --yes for destructive clearing.",
    group: "editor",
    inputSchema: objSchema({ type: { type: "string", enum: ["error", "warning", "log", "assert", "all"], default: "all" }, count: { type: "integer", default: 50, description: "Recent entries to return, clamped to 1..500." } }),
    outputSchema: pageSchema({ type: "object", properties: { index: { type: "integer" }, type: { type: "string" }, message: { type: "string" }, file: { type: "string" }, line: { type: "integer" } } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run editor.console",
    docsTopic: "editor",
    examples: [
      { description: "Read recent errors", argv: ["tools", "run", "editor.console", "--input", "{\"type\":\"error\",\"count\":20}"] }
    ],
    equivalentUnityMcp: "read_console / console-get-logs"
  },
  {
    name: "editor.state",
    summary: "Query current Unity Editor readiness: Play Mode, pause, compile/import/build/domain-reload busy flags, active scene, prefab stage, bridge build stamp, and runtime...",
    description: "Query current Unity Editor readiness: Play Mode, pause, compile/import/build/domain-reload busy flags, active scene, prefab stage, bridge build stamp, and runtime tool availability. Use this before expensive tools and after editor.play/editor.stop/editor.menu to decide whether to retry later or continue.",
    group: "editor",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ playMode: { type: "object" }, busy: { type: "object", description: "busy.isBusy or busy.compiling/updating/domainReloading/playModeChanging means AI should wait/retry before stateful operations." }, activeScene: { type: "object" }, prefabStage: { type: ["object", "null"] }, bridge: { type: "object" }, runtime: { type: "object" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run editor.state",
    docsTopic: "editor",
    examples: [
      { description: "Read editor state", argv: ["tools", "run", "editor.state", "--input", "{}"] }
    ],
    equivalentUnityMcp: "editor-application-get-state / editor_state"
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
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run editor.play",
    docsTopic: "editor",
    examples: [
      { description: "Enter Play Mode", argv: ["tools", "run", "editor.play", "--input", "{\"timeoutMs\":7000}"] }
    ],
    equivalentUnityMcp: "manage_editor.play / editor-application-set-state"
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
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run editor.pause",
    docsTopic: "editor",
    examples: [
      { description: "Toggle pause", argv: ["tools", "run", "editor.pause", "--input", "{}"] }
    ],
    equivalentUnityMcp: "manage_editor.pause / editor-application-set-state"
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
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run editor.stop",
    docsTopic: "editor",
    examples: [
      { description: "Exit Play Mode", argv: ["tools", "run", "editor.stop", "--input", "{}"] }
    ],
    equivalentUnityMcp: "manage_editor.stop / editor-application-set-state"
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
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run editor.undo",
    docsTopic: "editor",
    examples: [
      { description: "Undo last editor operation", argv: ["tools", "run", "editor.undo", "--input", "{}"] }
    ],
    equivalentUnityMcp: "manage_editor.undo"
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
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run editor.redo",
    docsTopic: "editor",
    examples: [
      { description: "Redo last editor operation", argv: ["tools", "run", "editor.redo", "--input", "{}"] }
    ],
    equivalentUnityMcp: "manage_editor.redo"
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
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run editor.resolution",
    docsTopic: "editor",
    examples: [
      { description: "Set default player resolution", argv: ["tools", "run", "editor.resolution", "--input", "{\"width\":1280,\"height\":720,\"fullscreen\":false}"] }
    ],
    equivalentUnityMcp: "manage_editor.set_resolution"
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
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run editor.menu",
    docsTopic: "editor",
    examples: [
      { description: "Refresh assets through Unity menu", argv: ["tools", "run", "editor.menu", "--yes", "--input", "{\"path\":\"Assets/Refresh\",\"timeoutMs\":7000}"] }
    ],
    equivalentUnityMcp: "execute_menu_item"
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
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run project.tag",
    docsTopic: "project",
    equivalentUnityMcp: "manage_editor.add_tag/remove_tag",
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
    riskLevel: "low",
    playModeSupport: "session-affecting",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run project.layer",
    docsTopic: "project",
    equivalentUnityMcp: "manage_editor.add_layer/remove_layer",
    examples: [
      { description: "Add a project layer", argv: ["tools", "run", "project.layer", "--input", "{\"action\":\"add\",\"name\":\"UnityCliProbeLayer\"}"] }
    ]
  },
  {
    name: "editor.refresh",
    summary: "Trigger AssetDatabase.Refresh in a separate Unity batch job, optionally requesting script compilation and waiting for readiness.",
    description: "Trigger AssetDatabase.Refresh in a separate Unity batch job, optionally requesting script compilation and waiting for readiness. Do not use this against a project already open in Unity; open projects can return UNITY_BUSY due to UnityLockfile.",
    group: "editor",
    inputSchema: objSchema({
      compile: { type: "boolean", default: false, description: "Request script compilation after refresh." },
      project: { type: "string", description: "Unity project path to open in batchmode." },
      unity: { type: "string", description: "Path to Unity executable for batchmode refresh." },
      wait: { type: "boolean", default: true, description: "Wait for the refresh job to complete before returning final status." },
      waitTimeoutMs: { type: "integer", description: "Maximum wait time for job completion." }
    }),
    outputSchema: objSchema({ jobId: { type: "string" }, status: { type: "string" }, command: { type: "string" }, logPath: { type: "string" } }),
    destructiveHint: false,
    longRunning: true,
    playModeSupport: "editor-only",
    sideEffects: ["asset-refresh", "script-compile", "domain-reload"],
    handler: "job",
    supportStatus: "batch-job",
    cliCommand: "tools run editor.refresh",
    docsTopic: "editor",
    examples: [
      { description: "Refresh assets in batchmode", argv: ["tools", "run", "editor.refresh", "--input", "{\"project\":\"./Dev/UnityProject\",\"unity\":\"C:/Program Files/Unity/Hub/Editor/2022.3.61f1/Editor/Unity.exe\",\"wait\":true}"] },
      { description: "Refresh and request compilation", argv: ["tools", "run", "editor.refresh", "--input", "{\"project\":\"./Dev/UnityProject\",\"compile\":true,\"wait\":true}"] }
    ],
    equivalentUnityMcp: "refresh_unity"
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
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run editor.selection.set",
    docsTopic: "editor",
    examples: [
      { description: "Select a GameObject by path", argv: ["tools", "run", "editor.selection.set", "--yes", "--input", "{\"target\":\"Player\"}"] },
      { description: "Dry-run selection", argv: ["tools", "run", "editor.selection.set", "--dry-run", "--input", "{\"target\":\"Player\"}"] }
    ],
    equivalentUnityMcp: "editor-selection-set"
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
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run editor.active-tool.set",
    docsTopic: "editor",
    examples: [
      { description: "Activate Move tool", argv: ["tools", "run", "editor.active-tool.set", "--yes", "--input", "{\"tool\":\"move\"}"] }
    ],
    equivalentUnityMcp: "manage_editor.set_active_tool"
  }
];
