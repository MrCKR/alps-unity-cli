import { resolve } from "node:path";
import { confirmDestructiveExecution } from "../../security.js";
import { clearRegistry, readSnapshot, setRegistry, validateRegistryUrl } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface PackageRegistryRequest {
  action: "get" | "set" | "clear";
  url?: string;
  project?: string;
  dryRun?: boolean;
}

export interface PackageRegistryResult {
  projectPath: string;
  manifestPath: string;
  registry?: string;
  previousRegistry?: string;
}

// 对 action 型工具的写 manifest 分支执行内部确认；只读 action 不应被 --yes 阻挡。
async function requirePackageRegistryConfirmation(context: ToolContext, action: "set" | "clear"): Promise<void> {
  const confirmation = await confirmDestructiveExecution(context.flags ?? {}, {
    command: "package.registry",
    summary: action === "set" ? "Package Registry Set" : "Package Registry Clear",
    destructiveHint: true,
    supportsDryRun: false,
    requestId: context.requestId
  });
  if (confirmation) {
    throw new StructuredToolError(
      confirmation.error?.code ?? "E_DESTRUCTIVE_CONFIRMATION_REQUIRED",
      confirmation.error?.message ?? "package.registry requires confirmation for write actions.",
      confirmation.error?.nextActions ?? ["Rerun the write action with --yes."],
      confirmation.error?.details as Record<string, unknown> | undefined
    );
  }
}

// 读取或修改 manifest registry 字段；set/clear 写入前保持旧 action 级确认语义。
async function runPackageRegistry(request: PackageRegistryRequest, context: ToolContext): Promise<PackageRegistryResult> {
  const projectPath = resolve(request.project ?? context.projectPath);
  if (request.action === "get") {
    const snapshot = await readSnapshot(projectPath);
    return { projectPath, manifestPath: snapshot.manifestPath, registry: snapshot.registry };
  }
  if (request.action === "set") {
    await requirePackageRegistryConfirmation(context, "set");
    return { projectPath, ...(await setRegistry(projectPath, validateRegistryUrl(request.url))) };
  }
  if (request.action === "clear") {
    await requirePackageRegistryConfirmation(context, "clear");
    return { projectPath, ...(await clearRegistry(projectPath)) };
  }
  throw new StructuredToolError("UNKNOWN_PACKAGE_REGISTRY_COMMAND", "Expected package registry get, set or clear.", [
    "Run unity_cli tools describe package.registry for supported actions."
  ]);
}

export default defineTool<PackageRegistryRequest, PackageRegistryResult>({
  name: "package.registry",
  summary: "Get, set, or clear the primary registry URL in the project manifest. action=get reads, set writes url, clear removes.",
  description: "Get, set, or clear the primary registry URL in the project manifest. action=get reads, set writes url, clear removes. Offline (manifest edit).",
  group: "package",
  destructiveHint: false,
  sideEffects: ["manifest-change"],
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "Read the primary registry", argv: ["tools", "run", "package.registry", "--input", "{\"action\":\"get\"}"] }
  ],
  async execute(request, context) {
    return runPackageRegistry(request, context);
  }
});
