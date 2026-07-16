import { resolve } from "node:path";
import { clearRegistry, readSnapshot } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";

export interface PackageRegistryClearRequest {
  project?: string;
}

export interface PackageRegistryClearResult {
  projectPath: string;
  manifestPath: string;
  previousRegistry?: string;
  dryRun?: boolean;
  plannedEffects?: Array<{ type: string; registry?: string }>;
}

export default defineTool<PackageRegistryClearRequest, PackageRegistryClearResult>({
  name: "package.registry.clear",
  summary: "Clear the primary registry URL from the project manifest.",
  description: "Remove the primary registry URL from Packages/manifest.json. Requires --yes and supports --dry-run.",
  group: "package",
  destructiveHint: true,
  supportsDryRun: true,
  sideEffects: ["manifest-change"],
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "Clear the primary registry", argv: ["tools", "run", "package.registry.clear", "--input", "{}", "--yes"] }
  ],
  async execute(request, context) {
    const projectPath = resolve(request.project ?? context.projectPath);
    if (context.execution.dryRun) {
      const snapshot = await readSnapshot(projectPath);
      return { projectPath, manifestPath: snapshot.manifestPath, previousRegistry: snapshot.registry, dryRun: true, plannedEffects: [{ type: "package.registry.clear", registry: snapshot.registry }] };
    }
    return { projectPath, ...(await clearRegistry(projectPath)) };
  }
});
