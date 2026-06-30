import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

/**
 * Build 域（Editor 端）：构建配置读写。对齐 CoplayDev manage_build。
 * build.run/status/logs/cancel 仍在 cli-only 域作为 job；这里是 Editor bridge 端配置工具。
 */
export const BUILD_TOOLS: ToolMetadata[] = [
  {
    name: "build.scenes",
    summary: "Read or modify Editor Build Settings scenes list.",
    description: "Read or modify Editor Build Settings scenes list. Use action=get for a safe read; add/remove/enable/disable change EditorBuildSettings.scenes and support --dry-run.",
    group: "build",
    inputSchema: objSchema({ action: { type: "string", enum: ["get", "add", "remove", "enable", "disable"], default: "get", description: "Use get for read-only inspection; add/remove/enable/disable mutate Build Settings scenes." }, path: { type: "string", description: "Scene asset path, required for add/remove/enable/disable." } }),
    destructiveHint: false,
    sideEffects: ["build-settings-modify"],
    playModeSupport: "session-affecting",
    supportsDryRun: true,
    examples: [
      { description: "Read Build Settings scenes", argv: ["tools", "run", "build.scenes", "--input", "{\"action\":\"get\"}"] },
      { description: "Preview adding a scene", argv: ["tools", "run", "build.scenes", "--input", "{\"action\":\"add\",\"path\":\"Assets/UnityCliAlignmentFixtures/BuildScene.unity\"}", "--dry-run"] }
    ]
  },
  {
    name: "build.platform",
    summary: "Read or set the active BuildTarget.",
    description: "Read or set the active BuildTarget. Setting a target can trigger platform import, compilation, or domain reload; prefer action=get or --dry-run before switching.",
    group: "build",
    inputSchema: objSchema({ action: { type: "string", enum: ["get", "set"], default: "get", description: "Use get for read-only inspection; set switches EditorUserBuildSettings active target." }, target: { type: "string", description: "BuildTarget enum name, e.g. StandaloneWindows64, Android, iOS, WebGL." } }),
    destructiveHint: false,
    sideEffects: ["build-target-switch", "asset-import", "domain-reload"],
    playModeSupport: "session-affecting",
    supportsDryRun: true,
    examples: [
      { description: "Read active build platform", argv: ["tools", "run", "build.platform", "--input", "{\"action\":\"get\"}"] },
      { description: "Preview a platform switch", argv: ["tools", "run", "build.platform", "--input", "{\"action\":\"set\",\"target\":\"StandaloneWindows64\"}", "--dry-run"] }
    ]
  },
  {
    name: "build.profiles",
    summary: "List, create, switch, or rename Unity Build Profile assets when the Editor version exposes Build Profile APIs.",
    description: "List, create, switch, or rename Unity Build Profile assets when the Editor version exposes Build Profile APIs. action=list/get is safe and returns available=false with diagnostics on Unity versions without Build Profiles; create/switch/modify support dry-run and return stable errors when the API is unavailable.",
    group: "build",
    inputSchema: objSchema({ action: { type: "string", enum: ["list", "get", "create", "switch", "modify"], default: "list", description: "list/get reads profiles; create writes a new profile asset; switch activates an existing profile; modify currently supports renaming." }, path: { type: "string", description: "Build Profile asset path. Required for create/switch/modify." }, name: { type: "string", description: "Profile name for create/modify." }, target: { type: "string", description: "Reserved BuildTarget hint for future profile field mapping." } }),
    outputSchema: objSchema({ command: { type: "string" }, available: { type: "boolean" }, action: { type: "string" }, activePath: { type: "string" }, profiles: { type: "array", items: { type: "object" } }, diagnostics: { type: "array", items: { type: "object" } }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    sideEffects: ["build-profile-asset-write", "build-profile-switch"],
    playModeSupport: "session-affecting",
    supportsDryRun: true,
    examples: [
      { description: "List Build Profiles or report API availability", argv: ["tools", "run", "build.profiles", "--input", "{\"action\":\"list\"}"] },
      { description: "Preview creating a Build Profile", argv: ["tools", "run", "build.profiles", "--input", "{\"action\":\"create\",\"path\":\"Assets/BuildProfiles/Windows.asset\",\"name\":\"Windows\"}", "--dry-run"] }
    ]
  },
  {
    name: "build.settings",
    summary: "Read or modify key PlayerSettings build configuration (productName, bundleVersion, applicationIdentifier, scriptingBackend, API compatibility level).",
    description: "Read or modify key PlayerSettings build configuration. action=get (default) returns productName, bundleVersion, companyId, applicationIdentifier, scriptingBackend, and API compatibility level. action=set writes provided fields to PlayerSettings (persisted to ProjectSettings/ProjectSettings.asset); pass only the fields you want to change. Use --dry-run to preview a set without writing.",
    group: "build",
    inputSchema: objSchema({ action: { type: "string", enum: ["get", "set"], default: "get", description: "Use get for read-only inspection; set writes provided fields to PlayerSettings." }, productName: { type: "string", description: "PlayerSettings.productName. Set only." }, bundleVersion: { type: "string", description: "PlayerSettings.bundleVersion. Set only." }, companyId: { type: "string", description: "PlayerSettings.companyName. Set only." }, applicationIdentifier: { type: "string", description: "Reverse-DNS application identifier. Set only." }, scriptingBackend: { type: "string", enum: ["Mono2x", "IL2CPP"], description: "ScriptingBackend enum. Set only." }, apiCompatibilityLevel: { type: "string", description: "ApiCompatibilityLevel enum name, e.g. NetStandard21, NetWeb, NetStandard. Set only." } }),
    destructiveHint: false,
    sideEffects: ["player-settings-write"],
    playModeSupport: "supported",
    supportsDryRun: true,
    examples: [
      { description: "Read PlayerSettings build configuration", argv: ["tools", "run", "build.settings", "--input", "{\"action\":\"get\"}"] },
      { description: "Preview changing product name", argv: ["tools", "run", "build.settings", "--input", "{\"action\":\"set\",\"productName\":\"MyGame\"}", "--dry-run"] }
    ]
  }
];
