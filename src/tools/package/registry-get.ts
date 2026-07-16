import { resolve } from "node:path";
import { readSnapshot } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";

export interface PackageRegistryGetRequest {
  project?: string;
}

export interface PackageRegistryGetResult {
  projectPath: string;
  manifestPath: string;
  registry?: string;
}

export default defineTool<PackageRegistryGetRequest, PackageRegistryGetResult>({
  name: "package.registry.get",
  summary: "Read the primary registry URL from the project manifest.",
  description: "Read the primary registry URL from Packages/manifest.json. Offline and read-only.",
  group: "package",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "Read the primary registry", argv: ["tools", "run", "package.registry.get", "--input", "{}"] }
  ],
  async execute(request, context) {
    const projectPath = resolve(request.project ?? context.projectPath);
    const snapshot = await readSnapshot(projectPath);
    return { projectPath, manifestPath: snapshot.manifestPath, registry: snapshot.registry };
  }
});
