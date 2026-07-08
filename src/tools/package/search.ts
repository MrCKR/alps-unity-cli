import { resolve } from "node:path";
import { PackageManagerError, readSnapshot, searchRegistry, validatePackageName, validateRegistryUrl } from "../../package-manager.js";
import { defineTool } from "../define.js";

export interface PackageSearchRequest {
  query: string;
  registry?: string;
  project?: string;
}

export interface PackageSearchEntry {
  name: string;
  version?: string;
  description?: string;
}

export interface PackageSearchResult {
  registry: string;
  query: string;
  packages: PackageSearchEntry[];
}

const DEFAULT_REGISTRY = "https://package.openupm.com";

/** 解析 registry 来源：显式 registry 优先；显式 project 必须有效；无 project 的非 Unity CWD 回退公开 UPM registry。 */
async function resolveRegistrySource(request: PackageSearchRequest, projectPath: string): Promise<string> {
  if (request.registry) {
    return request.registry;
  }
  try {
    return (await readSnapshot(resolve(request.project ?? projectPath))).registry ?? DEFAULT_REGISTRY;
  } catch (error) {
    if (!request.project && error instanceof PackageManagerError && error.code === "MANIFEST_NOT_FOUND") {
      return DEFAULT_REGISTRY;
    }
    throw error;
  }
}

export default defineTool<PackageSearchRequest, PackageSearchResult>({
  name: "package.search",
  summary: "Search a Unity package registry by name and return candidate packages with versions/descriptions.",
  description: "Search a Unity package registry by name and return candidate packages with versions/descriptions. Offline (HTTP to registry, no Unity needed). Explicit registry avoids reading a project manifest; otherwise the tool reads the project registry and falls back to https://package.openupm.com when no project manifest is available. Use this before package.install.",
  group: "package",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "Search a registry", argv: ["tools", "run", "package.search", "--input", "{\"query\":\"textmeshpro\",\"registry\":\"https://package.openupm.com\"}"] }
  ],
  /** 查询 Unity package registry；不触发 Unity Editor，也不修改 manifest。 */
  async execute(request, context) {
    const query = validatePackageName(request.query);
    const registry = validateRegistryUrl(await resolveRegistrySource(request, context.projectPath));
    return { registry, query, packages: await searchRegistry(registry, query) };
  }
});
