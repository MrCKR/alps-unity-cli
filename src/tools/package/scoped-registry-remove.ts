import { resolve } from "node:path";
import { PackageManagerError, removeScopedRegistry, validatePackageName } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";

export interface PackageScopedRegistryRemoveRequest {
  name: string;
  force?: boolean;
  project?: string;
}

export interface PackageScopedRegistryRemoveResult {
  projectPath: string;
  manifestPath: string;
  registryRemoved?: boolean;
  removed?: { name: string; url: string; scopes: string[] };
  matchedPackages?: string[];
  forced?: boolean;
  dryRun?: boolean;
  blocked?: boolean;
  plannedEffects?: Array<{ type?: string; name?: string; scopes?: string[]; matchedPackages?: string[]; forced?: boolean; blocked?: boolean }>;
}

export default defineTool<PackageScopedRegistryRemoveRequest, PackageScopedRegistryRemoveResult>({
  name: "package.scoped-registry.remove",
  summary: "Remove a scoped registry from the project manifest.",
  description: "Remove a scoped registry from Packages/manifest.json. Requires --yes; matching manifest dependencies require force=true. Supports --dry-run.",
  group: "package",
  destructiveHint: true,
  supportsDryRun: true,
  sideEffects: ["manifest-change"],
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "Preview removing a scoped registry", argv: ["tools", "run", "package.scoped-registry.remove", "--input", "{\"name\":\"Acme\"}", "--dry-run"] }
  ],
  async execute(request, context) {
    const projectPath = resolve(request.project ?? context.projectPath);
    const name = validatePackageName(request.name);
    const prepared = await removeScopedRegistry(projectPath, name, { force: request.force === true, dryRun: true });
    if (context.execution.dryRun) return { projectPath, ...prepared };
    if (prepared.blocked) {
      throw new PackageManagerError("SCOPED_REGISTRY_IN_USE", `Scoped registry ${name} still matches manifest package(s): ${prepared.matchedPackages.join(", ")}.`, ["Remove or move matching packages first, or pass force=true only when intentional."]);
    }
    await context.commitEffect?.();
    return { projectPath, ...(await removeScopedRegistry(projectPath, name, { force: request.force === true })) };
  }
});
