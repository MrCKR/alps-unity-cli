import { resolve } from "node:path";
import { readSnapshot } from "../../package-manager.js";
import { defineTool } from "../define.js";

export interface PackageListRequest {
  project?: string;
}

export interface PackageListScopedRegistry {
  name: string;
  url: string;
  scopes: string[];
}

export interface PackageListEntry {
  name: string;
  requestedVersion?: string;
  lockedVersion?: string;
  source?: string;
  url?: string;
  hash?: string;
  dependencies?: Record<string, string>;
}

export interface PackageListResult {
  projectPath: string;
  manifestPath: string;
  lockPath: string;
  registry?: string;
  scopedRegistries: PackageListScopedRegistry[];
  packages: PackageListEntry[];
}

export default defineTool<PackageListRequest, PackageListResult>({
  name: "package.list",
  summary: "List installed packages and their requested/locked versions in the project manifest.",
  description: "List installed packages and their requested/locked versions in the project manifest. Offline (reads manifest directly, no Unity needed). Use package.status for a manifest+lock summary or package.info for one package.",
  group: "package",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "List installed packages", argv: ["tools", "run", "package.list", "--input", "{\"project\":\"D:/workspace/alps-unity-cli/Dev/UnityProject\"}"] }
  ],
  /** 读取 Unity manifest/lock 快照，不触发 Unity Editor 或 PackageManager resolve。 */
  async execute(request, context) {
    return readSnapshot(resolve(request.project ?? context.projectPath));
  }
});
