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
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run build.scenes",
    docsTopic: "build",
    equivalentUnityMcp: "manage_build.scenes",
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
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run build.platform",
    docsTopic: "build",
    equivalentUnityMcp: "manage_build.platform",
    examples: [
      { description: "Read active build platform", argv: ["tools", "run", "build.platform", "--input", "{\"action\":\"get\"}"] },
      { description: "Preview a platform switch", argv: ["tools", "run", "build.platform", "--input", "{\"action\":\"set\",\"target\":\"StandaloneWindows64\"}", "--dry-run"] }
    ]
  },
  {
    name: "build.settings",
    summary: "Read key PlayerSettings build configuration such as productName, bundleVersion, applicationIdentifier, scriptingBackend, and API compatibility level.",
    description: "Read key PlayerSettings build configuration such as productName, bundleVersion, applicationIdentifier, scriptingBackend, and API compatibility level.",
    group: "build",
    inputSchema: objSchema({}),
    destructiveHint: false,
    playModeSupport: "supported",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run build.settings",
    docsTopic: "build",
    equivalentUnityMcp: "manage_build.settings",
    examples: [
      { description: "Read PlayerSettings build configuration", argv: ["tools", "run", "build.settings", "--input", "{}"] }
    ]
  }
];
