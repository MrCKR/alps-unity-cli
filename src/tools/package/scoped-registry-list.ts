import { resolve } from "node:path";
import { readSnapshot } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";

export interface PackageScopedRegistryListRequest {
  project?: string;
}

export interface PackageScopedRegistryListResult {
  projectPath: string;
  manifestPath: string;
  scopedRegistries: Array<{ name: string; url: string; scopes: string[] }>;
}

export default defineTool<PackageScopedRegistryListRequest, PackageScopedRegistryListResult>({
  name: "package.scoped-registry.list",
  summary: "List scoped registries from the project manifest.",
  description: "Read scoped registries from Packages/manifest.json. Offline and read-only.",
  group: "package",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "List scoped registries", argv: ["tools", "run", "package.scoped-registry.list", "--input", "{}"] }
  ],
  async execute(request, context) {
    const projectPath = resolve(request.project ?? context.projectPath);
    const snapshot = await readSnapshot(projectPath);
    return { projectPath, manifestPath: snapshot.manifestPath, scopedRegistries: snapshot.scopedRegistries };
  }
});
