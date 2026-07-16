import { resolve } from "node:path";
import { readSnapshot } from "../../package-manager.js";
import { defineTool } from "../define.js";

export interface PackageStatusRequest {
  project?: string;
}

export interface PackageStatusEntry {
  name: string;
  requestedVersion?: string | null;
  lockedVersion?: string | null;
  source?: string | null;
  resolved: boolean;
}

export interface PackageStatusResult {
  projectPath: string;
  manifestPath: string;
  lockPath: string;
  packageCount: number;
  registry?: string | null;
  scopedRegistryCount: number;
  packages: PackageStatusEntry[];
}

export default defineTool<PackageStatusRequest, PackageStatusResult>({
  name: "package.status",
  summary: "Summarize manifest and lock state: package count, registry, scoped registries, and per-package resolved flag.",
  description: "Summarize manifest and lock state: package count, registry, scoped registries, and per-package resolved flag. Offline.",
  group: "package",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "Show package status", argv: ["tools", "run", "package.status", "--input", "{\"project\":\"./Dev/UnityProject\"}"] }
  ],
  /** 汇总 manifest/lock 快照，不触发 Unity Editor 或 PackageManager resolve。 */
  async execute(request, context) {
    const projectPath = resolve(request.project ?? context.projectPath);
    const snapshot = await readSnapshot(projectPath);
    return {
      projectPath,
      manifestPath: snapshot.manifestPath,
      lockPath: snapshot.lockPath,
      packageCount: snapshot.packages.length,
      registry: snapshot.registry ?? null,
      scopedRegistryCount: snapshot.scopedRegistries.length,
      packages: snapshot.packages.map((entry) => ({
        name: entry.name,
        requestedVersion: entry.requestedVersion ?? null,
        lockedVersion: entry.lockedVersion ?? null,
        source: entry.source ?? null,
        resolved: entry.lockedVersion !== undefined
      }))
    };
  }
});
