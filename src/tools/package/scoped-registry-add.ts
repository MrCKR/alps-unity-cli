import { resolve } from "node:path";
import { addScopedRegistry, parseScopes, readSnapshot, validatePackageName, validateRegistryUrl } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";

export interface PackageScopedRegistryAddRequest {
  name: string;
  url: string;
  scope?: string;
  scopes?: string;
  project?: string;
}

export interface PackageScopedRegistryAddResult {
  projectPath: string;
  manifestPath: string;
  registry?: { name: string; url: string; scopes: string[] };
  dryRun?: boolean;
  plannedEffects?: Array<{ type: string; name: string; url: string; scopes: string[] }>;
}

export default defineTool<PackageScopedRegistryAddRequest, PackageScopedRegistryAddResult>({
  name: "package.scoped-registry.add",
  summary: "Add or replace a scoped registry in the project manifest.",
  description: "Add or replace a scoped registry in Packages/manifest.json. Requires --yes and supports --dry-run.",
  group: "package",
  destructiveHint: true,
  supportsDryRun: true,
  sideEffects: ["manifest-change"],
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "Add a scoped registry", argv: ["tools", "run", "package.scoped-registry.add", "--input", "{\"name\":\"Acme\",\"url\":\"https://registry.acme.test\",\"scope\":\"com.acme\"}", "--yes"] }
  ],
  async execute(request, context) {
    const projectPath = resolve(request.project ?? context.projectPath);
    const name = validatePackageName(request.name);
    const url = validateRegistryUrl(request.url);
    const scopes = parseScopes(request.scope ?? request.scopes);
    const prepared = await addScopedRegistry(projectPath, { name, url, scopes }, { dryRun: true });
    if (context.execution.dryRun) return { projectPath, ...prepared } as PackageScopedRegistryAddResult;
    await context.commitEffect?.();
    return { projectPath, ...(await addScopedRegistry(projectPath, { name, url, scopes })) } as PackageScopedRegistryAddResult;
  }
});
