import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

/**
 * Build 域（Editor 端）：构建配置读写。对齐 CoplayDev manage_build。
 * build.run/status/logs/cancel 仍在 cli-only 域作为 job；这里是 Editor bridge 端配置工具。
 */
export const BUILD_TOOLS: ToolMetadata[] = [
  {
    name: "build.scenes",
    title: "Build Scenes",
    description: "Read or modify Build Settings scenes list (get/add/remove/enable/disable).",
    group: "build",
    inputSchema: objSchema({ action: { type: "string", enum: ["get", "add", "remove", "enable", "disable"], default: "get" }, path: { type: "string" } }),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run build.scenes",
    docsTopic: "build",
    equivalentUnityMcp: "manage_build.scenes"
  },
  {
    name: "build.platform",
    title: "Build Platform",
    description: "Read or set the active build target (BuildTarget). Setting switches platform, which may trigger a domain reload.",
    group: "build",
    inputSchema: objSchema({ action: { type: "string", enum: ["get", "set"], default: "get" }, target: { type: "string", description: "BuildTarget enum name, e.g. StandaloneWindows64." } }),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run build.platform",
    docsTopic: "build",
    equivalentUnityMcp: "manage_build.platform"
  },
  {
    name: "build.settings",
    title: "Build Settings",
    description: "Read key PlayerSettings build configuration (productName, bundleVersion, scriptingBackend, etc.).",
    group: "build",
    inputSchema: objSchema({}),
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run build.settings",
    docsTopic: "build",
    equivalentUnityMcp: "manage_build.settings"
  }
];
