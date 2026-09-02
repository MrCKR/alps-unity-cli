import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { PackageManagerError, readUnityManifestDependencies, removePackage, validatePackageName } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";
import { runPackageFileMutationWorker } from "./file-mutation-worker.js";
import { bindLivePackageProject, runLivePackageResolve, type LiveResolveResult, type PackageFileMutationBundle } from "./live-resolve.js";
import { projectRelativePath } from "./public-result.js";

export interface PackageRemoveRequest { name: string; force?: boolean; }
export interface PackageRemoveResult { action: "remove"; package: string; manifestPath: string; manifestHash: string; dependency: { name: string; present: false }; files: { proven: true; manifestPath: string; manifestHash: string }; resolve: LiveResolveResult; }
export interface PackageRemovePlannedEffect { type: string; package: string; removedVersion?: string; dependents: Array<{ name: string; requiredVersion?: string }>; forced: boolean; blocked: boolean; }
export interface PackageRemoveDryRunResult { action: "remove"; package: string; status: "dry_run" | "blocked"; dryRun: true; blocked: boolean; dependents: Array<{ name: string; requiredVersion?: string }>; forced: boolean; plannedEffects: PackageRemovePlannedEffect[]; }
interface RemovePlan { name: string; force: boolean; manifestPath: string; }

/** manifest 删除与 UPM 终态共享一个 root，redelivery 只读验证删除结果。 */
async function runPackageRemove(request: PackageRemoveRequest, context: ToolContext): Promise<PackageRemoveResult | PackageRemoveDryRunResult> {
  const name = validatePackageName(request.name);
  if (context.execution.dryRun) {
    await bindLivePackageProject(context, "remove");
    const plan = await removePackage(context.projectPath, name, { force: request.force === true, dryRun: true });
    return { action: "remove", package: name, status: plan.blocked ? "blocked" : "dry_run", dryRun: true, blocked: plan.blocked, dependents: plan.dependents, forced: plan.forced, plannedEffects: (plan.plannedEffects ?? []) as unknown as PackageRemovePlannedEffect[] };
  }
  return runLivePackageResolve<PackageRemoveResult>(context, "remove", { name, force: request.force === true }, {
    async prepare(signal) {
      signal.throwIfAborted();
      const prepared = await removePackage(context.projectPath, name, { force: request.force === true, dryRun: true });
      signal.throwIfAborted();
      if (prepared.blocked) throw new PackageManagerError("PACKAGE_DEPENDENTS_BLOCKED", `Package ${name} is required by dependent packages.`, ["Remove dependents first or explicitly pass force=true."]);
      return { name, force: request.force === true, manifestPath: prepared.manifestPath } as unknown as Record<string, unknown>;
    },
    async apply(plan, signal, stopAtMs) {
      const expected = removePlan(plan);
      await runPackageFileMutationWorker("remove", context.projectPath, plan, stopAtMs, signal);
      return removeBundle(context.projectPath, expected, expected.manifestPath);
    },
    async recover(plan, signal) {
      signal.throwIfAborted();
      const expected = removePlan(plan);
      const bundle = await removeBundle(context.projectPath, expected, expected.manifestPath);
      signal.throwIfAborted();
      return bundle;
    },
  });
}

async function removeBundle(projectPath: string, plan: RemovePlan, manifestPath: string): Promise<PackageFileMutationBundle> {
  const dependencies = await readUnityManifestDependencies(projectPath);
  if (Object.prototype.hasOwnProperty.call(dependencies, plan.name)) throw new Error(`Manifest dependency ${plan.name} remains after the committed remove plan.`);
  const manifestHash = await hash(manifestPath);
  const files = { proven: true as const, manifestPath, manifestHash };
  const publicManifestPath = projectRelativePath(projectPath, manifestPath, "manifestPath");
  const publicFiles = { ...files, manifestPath: publicManifestPath };
  return {
    expectedManifestSha256: manifestHash,
    predicates: [{ name: plan.name, present: false }],
    filesProof: files,
    publicResultSeed: { action: "remove", package: plan.name, manifestPath: publicManifestPath, manifestHash, dependency: { name: plan.name, present: false }, files: publicFiles },
  };
}

function removePlan(value: Record<string, unknown>): RemovePlan {
  if (typeof value.name !== "string" || typeof value.force !== "boolean" || typeof value.manifestPath !== "string") throw new Error("Durable package.remove file plan is corrupt.");
  return { name: value.name, force: value.force, manifestPath: value.manifestPath };
}
async function hash(path: string): Promise<string> { return createHash("sha256").update(await readFile(path)).digest("hex"); }

export default defineTool<PackageRemoveRequest, PackageRemoveResult | PackageRemoveDryRunResult>({
  name: "package.remove", summary: "Remove one manifest package in EditMode and return durable Unity absence proof.",
  description: "EditMode only. Prevalidates dependents, optionally previews with dryRun, removes the exact manifest dependency once, then dispatches Unity PackageManager Resolve and returns only after fresh manifest, lock, and PackageInfo readback prove absence. Timeout never implies rollback or replay.",
  group: "package", destructiveHint: true, supportsDryRun: true, sideEffects: ["manifest-change", "asset-import", "session-affecting"], playModeSupport: "editor-only", handler: "hybrid", supportStatus: "live-bridge", timeoutMs: 120_000,
  examples: [{ description: "Remove a package.", argv: ["tools", "run", "package.remove", "--input", "{\"name\":\"com.unity.textmeshpro\"}"] }],
  async execute(request, context) { return runPackageRemove(request, context); }
});
