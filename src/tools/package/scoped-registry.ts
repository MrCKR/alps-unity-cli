import { resolve } from "node:path";
import { confirmDestructiveExecution } from "../../security.js";
import { addScopedRegistry, parseScopes, readSnapshot, removeScopedRegistry, validatePackageName, validateRegistryUrl } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface PackageScopedRegistryRequest {
  action: "list" | "add" | "remove";
  name?: string;
  url?: string;
  scope?: string;
  scopes?: string;
  force?: boolean;
  project?: string;
  dryRun?: boolean;
}

export interface PackageScopedRegistryEntry {
  name: string;
  url: string;
  scopes: string[];
}

export interface PackageScopedRegistryPlannedEffect {
  type?: string;
  name?: string;
  scopes?: string[];
  matchedPackages?: string[];
  forced?: boolean;
  blocked?: boolean;
}

export interface PackageScopedRegistryResult {
  projectPath: string;
  manifestPath: string;
  scopedRegistries?: PackageScopedRegistryEntry[];
  registry?: PackageScopedRegistryEntry;
  replaced?: boolean;
  removed?: PackageScopedRegistryEntry;
  matchedPackages?: string[];
  forced?: boolean;
  dryRun?: boolean;
  blocked?: boolean;
  plannedEffects?: PackageScopedRegistryPlannedEffect[];
}

// 对 action 型工具的写 manifest 分支执行内部确认；remove 允许 dry-run 预览阻断包。
async function requireScopedRegistryConfirmation(context: ToolContext, action: "add" | "remove"): Promise<void> {
  const confirmation = await confirmDestructiveExecution(context.flags ?? {}, {
    command: "package.scoped-registry",
    summary: action === "add" ? "Package Scoped Registry Add" : "Package Scoped Registry Remove",
    destructiveHint: true,
    supportsDryRun: action === "remove",
    requestId: context.requestId
  });
  if (confirmation) {
    throw new StructuredToolError(
      confirmation.error?.code ?? "E_DESTRUCTIVE_CONFIRMATION_REQUIRED",
      confirmation.error?.message ?? "package.scoped-registry requires confirmation for write actions.",
      confirmation.error?.nextActions ?? ["Rerun the write action with --yes, or use --dry-run for remove."],
      confirmation.error?.details as Record<string, unknown> | undefined
    );
  }
}

// 读取或修改 manifest scopedRegistries；list 只读，add/remove 保持旧 action 级确认语义。
async function runPackageScopedRegistry(request: PackageScopedRegistryRequest, context: ToolContext): Promise<PackageScopedRegistryResult> {
  const projectPath = resolve(request.project ?? context.projectPath);
  if (request.action === "list") {
    const snapshot = await readSnapshot(projectPath);
    return { projectPath, manifestPath: snapshot.manifestPath, scopedRegistries: snapshot.scopedRegistries };
  }
  if (request.action === "add") {
    await requireScopedRegistryConfirmation(context, "add");
    const name = validatePackageName(request.name);
    const url = validateRegistryUrl(request.url);
    const scopes = parseScopes(request.scope ?? request.scopes);
    return { projectPath, ...(await addScopedRegistry(projectPath, { name, url, scopes })) };
  }
  if (request.action === "remove") {
    await requireScopedRegistryConfirmation(context, "remove");
    const name = validatePackageName(request.name);
    return { projectPath, ...(await removeScopedRegistry(projectPath, name, { force: request.force === true, dryRun: request.dryRun === true })) };
  }
  throw new StructuredToolError("UNKNOWN_SCOPED_REGISTRY_COMMAND", "Expected package scoped-registry list, add or remove.", [
    "Run unity_cli tools describe package.scoped-registry for supported actions."
  ]);
}

export default defineTool<PackageScopedRegistryRequest, PackageScopedRegistryResult>({
  name: "package.scoped-registry",
  summary: "List, add, or remove scoped registries in the project manifest. action=list reads all; add requires name/url/(scope|scopes); remove requires name.",
  description: "List, add, or remove scoped registries in the project manifest. action=list reads all; add requires name/url/(scope|scopes); remove requires name. Write actions edit manifest.json and require --yes; remove blocks when manifest dependencies still match the registry scopes unless force=true. remove supports --dry-run to preview matched packages without writing.",
  group: "package",
  destructiveHint: false,
  supportsDryRun: true,
  sideEffects: ["manifest-change"],
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "List scoped registries", argv: ["tools", "run", "package.scoped-registry", "--input", "{\"action\":\"list\"}"] },
    { description: "Preview removing a scoped registry", argv: ["tools", "run", "package.scoped-registry", "--input", "{\"action\":\"remove\",\"name\":\"Acme\"}", "--dry-run"] }
  ],
  async execute(request, context) {
    return runPackageScopedRegistry(request, context);
  }
});
