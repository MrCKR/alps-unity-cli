import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

/**
 * Package 域：Editor 端包操作。package.embed 对齐 CoplayDev embed_package。
 * CLI 端包管理 (list/install/remove/...) 仍在 cli-only 域作为 job。
 */
export const PACKAGE_TOOLS: ToolMetadata[] = [
  {
    name: "package.embed",
    summary: "Embed a Packages/ package into Assets/ as a local copy.",
    description: "Embed an installed Packages/ package into Assets/ as a local copy. This copies package files, refreshes AssetDatabase state and can cause domain reload; use --dry-run to preview plannedEffects before --yes. After embedding, inspect nextActions before removing the manifest dependency to avoid duplicate symbols.",
    group: "package",
    inputSchema: objSchema({ package: { type: "string" }, packageId: { type: "string", description: "Compatibility alias of package." } }, ["package"]),
    outputSchema: objSchema({ command: { type: "string" }, dryRun: { type: "boolean" }, package: { type: "string" }, source: { type: "string" }, destination: { type: "string" }, plannedEffects: { type: "array", items: { type: "object" } }, nextActions: { type: "string" }, logPath: { type: "string" } }),
    destructiveHint: true,
    supportsDryRun: true,
    sideEffects: ["domain-reload", "manifest-change", "file-copy"],
    playModeSupport: "session-affecting",
    examples: [
      { description: "Preview embedding an installed package", argv: ["tools", "run", "package.embed", "--input", "{\"package\":\"com.unity.textmeshpro\"}", "--dry-run"] },
      { description: "Embed an installed package after confirmation", argv: ["tools", "run", "package.embed", "--input", "{\"package\":\"com.unity.textmeshpro\"}", "--yes"] }
    ],
  }
];
