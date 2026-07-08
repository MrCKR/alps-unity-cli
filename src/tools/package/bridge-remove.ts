import { resolve } from "node:path";
import { removePackageBridge } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";

export interface PackageBridgeRemoveRequest {
  project?: string;
  dryRun?: boolean;
}

export interface PackageBridgeRemovePlannedEffect {
  type?: string;
  package?: string;
  bridgePath?: string;
  installMetadataPath?: string;
  managedFileCount?: number;
  previousVersion?: string;
}

export interface PackageBridgeRemoveResult {
  action: "bridge-remove";
  projectPath: string;
  manifestPath: string;
  bridgePath: string;
  dependencyName: string;
  packageVersion: string;
  installMetadataPath: string;
  fileManifestPath: string;
  managedFileCount: number;
  removedManifestDependency: boolean;
  previousManifestDependency?: string;
  dryRun: boolean;
  plannedEffects: PackageBridgeRemovePlannedEffect[];
}

// 执行真正的 Bridge 移除；destructive 确认由 tools run 分发层在进入 defineTool 前完成。
async function runPackageBridgeRemove(request: PackageBridgeRemoveRequest, context: ToolContext): Promise<PackageBridgeRemoveResult> {
  const projectPath = resolve(request.project ?? context.projectPath);
  const removal = await removePackageBridge(projectPath, { dryRun: request.dryRun === true });
  return { action: "bridge-remove", ...removal };
}

export default defineTool<PackageBridgeRemoveRequest, PackageBridgeRemoveResult>({
  name: "package.bridge-remove",
  summary: "Remove the copied com.unity_cli Bridge package installed by init.",
  description: "Remove the copied com.unity_cli Bridge package installed by init. Destructive: deletes Packages/com.unity_cli and unity_cli.install.json, and removes any legacy com.unity_cli manifest dependency. Supports --dry-run to preview files and manifest effects without deleting anything. Use this only when intentionally uninstalling the Unity CLI Bridge from a project.",
  group: "package",
  destructiveHint: true,
  supportsDryRun: true,
  sideEffects: ["project-file-delete", "manifest-change", "session-affecting"],
  playModeSupport: "session-affecting",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "Preview Bridge package removal", argv: ["tools", "run", "package.bridge-remove", "--input", "{\"project\":\"D:/workspace/alps-unity-cli/Dev/UnityProject\"}", "--dry-run"] }
  ],
  async execute(request, context) {
    return runPackageBridgeRemove(request, context);
  }
});
