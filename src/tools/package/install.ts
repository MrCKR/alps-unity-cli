import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { installPackage, parsePackageSpec, readUnityManifestDependencies, validatePackageName } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";
import { runPackageFileMutationWorker } from "./file-mutation-worker.js";
import { runLivePackageResolve, type LiveResolveResult, type PackageFileMutationBundle } from "./live-resolve.js";
import { projectRelativePath } from "./public-result.js";

export interface PackageInstallRequest { name: string; version?: string; }
export interface PackageInstallResult { action: "install"; package: string; manifestPath: string; manifestHash: string; dependency: { name: string; version: string; present: true }; files: { proven: true; manifestPath: string; manifestHash: string }; resolve: LiveResolveResult; }
interface InstallPlan { name: string; version: string; manifestPath: string; }

/** manifest 写入、Resolve 与 PackageInfo readback 共用公开 requestId root。 */
async function runPackageInstall(request: PackageInstallRequest, context: ToolContext): Promise<PackageInstallResult> {
  const spec = parsePackageSpec(validatePackageName(request.name), request.version);
  if (context.execution.dryRun) {
    await installPackage(context.projectPath, spec, { dryRun: true });
    throw new StructuredToolError("E_TOOL_COMPLETION_PROOF_INVALID", "package.install dry-run does not have a live PackageInfo terminal.");
  }
  return runLivePackageResolve<PackageInstallResult>(context, "install", { name: spec.name, version: spec.version }, {
    async prepare(signal) {
      signal.throwIfAborted();
      const prepared = await installPackage(context.projectPath, spec, { dryRun: true });
      signal.throwIfAborted();
      return { name: spec.name, version: prepared.installedVersion, manifestPath: prepared.manifestPath } satisfies InstallPlan as unknown as Record<string, unknown>;
    },
    async apply(plan, signal, stopAtMs) {
      const expected = installPlan(plan);
      await runPackageFileMutationWorker("install", context.projectPath, plan, stopAtMs, signal);
      return installBundle(context.projectPath, expected, expected.manifestPath);
    },
    async recover(plan, signal) {
      signal.throwIfAborted();
      const expected = installPlan(plan);
      const bundle = await installBundle(context.projectPath, expected, expected.manifestPath);
      signal.throwIfAborted();
      return bundle;
    },
  });
}

async function installBundle(projectPath: string, plan: InstallPlan, manifestPath: string): Promise<PackageFileMutationBundle> {
  const dependencies = await readUnityManifestDependencies(projectPath);
  if (dependencies[plan.name] !== plan.version) throw new Error(`Manifest dependency ${plan.name} does not match the committed install plan.`);
  const manifestHash = await hash(manifestPath);
  const files = { proven: true as const, manifestPath, manifestHash };
  const publicManifestPath = projectRelativePath(projectPath, manifestPath, "manifestPath");
  const publicFiles = { ...files, manifestPath: publicManifestPath };
  return {
    expectedManifestSha256: manifestHash,
    predicates: [{ name: plan.name, requestedVersion: plan.version, present: true }],
    filesProof: files,
    publicResultSeed: { action: "install", package: plan.name, manifestPath: publicManifestPath, manifestHash, dependency: { name: plan.name, version: plan.version, present: true }, files: publicFiles },
  };
}

function installPlan(value: Record<string, unknown>): InstallPlan {
  if (typeof value.name !== "string" || typeof value.version !== "string" || typeof value.manifestPath !== "string") throw new Error("Durable package.install file plan is corrupt.");
  return { name: value.name, version: value.version, manifestPath: value.manifestPath };
}
async function hash(path: string): Promise<string> { return createHash("sha256").update(await readFile(path)).digest("hex"); }

export default defineTool<PackageInstallRequest, PackageInstallResult>({
  name: "package.install", summary: "Install one exact package version in EditMode and return durable Unity resolve proof.",
  description: "EditMode only. Prevalidates one exact package name/version, writes Packages/manifest.json once under the durable root, then dispatches Unity PackageManager Resolve and returns only after fresh manifest, lock, and PackageInfo readback prove the installed version. Timeout never implies rollback or replay.",
  group: "package", destructiveHint: true, sideEffects: ["manifest-change", "asset-import", "session-affecting"], playModeSupport: "editor-only", handler: "hybrid", supportStatus: "live-bridge", timeoutMs: 120_000,
  examples: [{ description: "Install a package.", argv: ["tools", "run", "package.install", "--input", "{\"name\":\"com.unity.textmeshpro\",\"version\":\"3.0.7\"}"] }],
  async execute(request, context) { return runPackageInstall(request, context); }
});
