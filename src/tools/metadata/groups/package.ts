import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

/**
 * Package 域：Editor 端包操作。package.embed 对齐 CoplayDev embed_package。
 * CLI 端包管理 (list/install/remove/...) 仍在 cli-only 域作为 job。
 */
export const PACKAGE_TOOLS: ToolMetadata[] = [
  {
    name: "package.embed",
    title: "Embed Package",
    description: "Embed a Packages/ package into Assets/ as a local copy. High-risk: causes domain reload, manifest change and file copy. Aligns with CoplayDev embed_package.",
    group: "package",
    inputSchema: objSchema({ package: { type: "string" }, packageId: { type: "string" } }, ["package"]),
    readOnlyHint: false,
    destructiveHint: true,
    supportsDryRun: true,
    riskLevel: "high",
    sideEffects: ["domain-reload", "manifest-change", "file-copy"],
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: "tools run package.embed",
    docsTopic: "package",
    equivalentUnityMcp: "manage_package.embed"
  }
];
