import { resolve } from "node:path";
import { PackageManagerError, packageInfo, validatePackageName } from "../../package-manager.js";
import { defineTool } from "../define.js";

export interface PackageInfoRequest {
  name: string;
  project?: string;
}

export interface PackageInfoEntry {
  name: string;
  requestedVersion?: string;
  lockedVersion?: string;
  source?: string;
  url?: string;
  hash?: string;
  dependencies?: Record<string, string>;
}

export interface PackageInfoResult {
  projectPath: string;
  package: PackageInfoEntry;
}

export default defineTool<PackageInfoRequest, PackageInfoResult>({
  name: "package.info",
  summary: "Show detailed info for one installed package by name.",
  description: "Show detailed info for one installed package by name. Returns PACKAGE_NOT_FOUND if the package is not in the manifest; use package.list first.",
  group: "package",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "Inspect one package", argv: ["tools", "run", "package.info", "--input", "{\"name\":\"com.unity_cli\",\"project\":\"./Dev/UnityProject\"}"] }
  ],
  /** 读取单个 package 条目；未找到时返回稳定 PACKAGE_NOT_FOUND。 */
  async execute(request, context) {
    const name = validatePackageName(request.name);
    const projectPath = resolve(request.project ?? context.projectPath);
    const info = await packageInfo(projectPath, name);
    if (!info) {
      throw new PackageManagerError("PACKAGE_NOT_FOUND", `Package not found: ${name}`, ["Run tools run package.list to inspect installed packages."]);
    }
    return { projectPath, package: info };
  }
});
