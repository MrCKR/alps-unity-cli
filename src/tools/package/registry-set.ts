import { resolve } from "node:path";
import { readSnapshot, setRegistry, validateRegistryUrl } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";

export interface PackageRegistrySetRequest {
  url: string;
  project?: string;
}

export interface PackageRegistrySetResult {
  projectPath: string;
  manifestPath: string;
  registry?: string;
  previousRegistry?: string;
  dryRun?: boolean;
  plannedEffects?: Array<{ type: string; registry: string }>;
}

export default defineTool<PackageRegistrySetRequest, PackageRegistrySetResult>({
  name: "package.registry.set",
  summary: "Set the primary registry URL in the project manifest.",
  description: "Set the primary registry URL in Packages/manifest.json. Supports --dry-run.",
  group: "package",
  destructiveHint: true,
  supportsDryRun: true,
  sideEffects: ["manifest-change"],
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "Preview setting the primary registry", argv: ["tools", "run", "package.registry.set", "--input", "{\"url\":\"https://npm.example.com\"}", "--dry-run"] }
  ],
  async execute(request, context) {
    const projectPath = resolve(request.project ?? context.projectPath);
    const registry = validateRegistryUrl(request.url);
    const prepared = await setRegistry(projectPath, registry, { dryRun: true });
    if (context.execution.dryRun) return { projectPath, ...prepared } as PackageRegistrySetResult;
    await context.commitEffect?.();
    return { projectPath, ...(await setRegistry(projectPath, registry)) } as PackageRegistrySetResult;
  }
});
