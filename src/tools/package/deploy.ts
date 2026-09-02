import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { deployPackageSource, hashPackageTree, inspectPackageDeploymentFiles, type PackageDeployResult as PackageDeployFileResult } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";
import { runPackageFileMutationWorker } from "./file-mutation-worker.js";
import { bindLivePackageProject, runLivePackageResolve, type LiveResolveResult, type PackageFileMutationBundle } from "./live-resolve.js";
import { projectRelativePath, publicPackageEffects } from "./public-result.js";

export interface PackageDeployRequest { source: string; packageName?: string; }
export interface PackageDeployPlannedEffect { type?: string; package?: string; from?: string; to?: string; targetPath?: string; packageVersion?: string | null; fileCount?: number; reason?: string; }
export interface PackageDeployFilesProof { proven: true; targetPath: string; sourceHash: string; targetHash: string; copiedFileCount: number; metadataPath: string; metadataHash: string; backupPath?: string; backupHash?: string; backupPackageVersion?: string; }
export interface PackageDeployResult {
  action: string; status: string; manifestPath: string; packageName: string; packageVersion: string;
  sourcePath: string; sourceHash: string; targetPath: string; backupPath?: string; backupPackageVersion?: string;
  backupCreated: boolean; repeatedDeployment: boolean; copiedFileCount: number; dryRun: boolean;
  plannedEffects: PackageDeployPlannedEffect[]; metadataPath: string; files?: PackageDeployFilesProof; resolve?: LiveResolveResult;
}
interface DeployPlan extends PackageDeployFileResult {
  requestedSource: string;
  requestedPackageName: string | null;
  previousTargetExists: boolean;
  previousTargetHash?: string;
  previousRecordIdentity: string;
  previousBackupExists: boolean;
  previousBackupHash?: string;
}

/** deploy 的 backup/copy、Resolve 与 PackageInfo readback 共用一个 durable root。 */
async function runPackageDeploy(request: PackageDeployRequest, context: ToolContext): Promise<PackageDeployResult> {
  if (context.execution.dryRun) {
    await bindLivePackageProject(context, "deploy");
    const projectPath = resolve(context.projectPath);
    const prepared = await deployPackageSource(projectPath, request.source, { packageName: request.packageName, dryRun: true });
    return publicDeployResult(projectPath, request.source, "dry_run", prepared);
  }
  return runLivePackageResolve<PackageDeployResult>(context, "deploy", { source: request.source, packageName: request.packageName }, {
    async prepare(signal) {
      const projectPath = resolve(context.projectPath);
      signal.throwIfAborted();
      const prepared = await deployPackageSource(projectPath, request.source, { packageName: request.packageName, dryRun: true });
      const observed = await inspectPackageDeploymentFiles(projectPath, prepared.packageName, prepared.targetPath, prepared.backupPath);
      signal.throwIfAborted();
      return {
        ...prepared,
        requestedSource: request.source,
        requestedPackageName: request.packageName ?? null,
        previousTargetExists: observed.targetExists,
        previousTargetHash: observed.targetHash,
        previousRecordIdentity: observed.recordIdentity,
        previousBackupExists: observed.backupExists,
        previousBackupHash: observed.backupHash,
      } as unknown as Record<string, unknown>;
    },
    async apply(plan, signal, stopAtMs) {
      const projectPath = resolve(context.projectPath);
      const expected = deployPlan(plan);
      const deployed = await runPackageFileMutationWorker("deploy", projectPath, plan, stopAtMs, signal) as unknown as PackageDeployFileResult;
      return deployBundle(projectPath, expected, deployed);
    },
    async recover(plan, signal) {
      const projectPath = resolve(context.projectPath);
      signal.throwIfAborted();
      const expected = deployPlan(plan);
      const bundle = await deployBundle(projectPath, expected, { ...expected, dryRun: false });
      signal.throwIfAborted();
      return bundle;
    },
  });
}

async function deployBundle(projectPath: string, plan: DeployPlan, deployed: PackageDeployFileResult): Promise<PackageFileMutationBundle> {
  if (deployed.packageName !== plan.packageName || deployed.packageVersion !== plan.packageVersion
    || deployed.sourceHash !== plan.sourceHash || deployed.targetPath !== plan.targetPath
    || (deployed.backupPath ?? null) !== (plan.backupPath ?? null)
    || deployed.backupCreated !== plan.backupCreated || deployed.repeatedDeployment !== plan.repeatedDeployment
    || deployed.copiedFileCount !== plan.copiedFileCount) {
    throw new Error("Deployed package does not match its durable file plan.");
  }
  const observed = await inspectPackageDeploymentFiles(projectPath, plan.packageName, plan.targetPath, plan.backupPath);
  const currentSourceHash = await hashPackageTree(plan.sourcePath);
  if (!observed.targetExists || observed.targetHash !== plan.sourceHash || observed.targetFileCount !== plan.copiedFileCount || currentSourceHash !== plan.sourceHash) throw new Error("Deployed package target does not match the committed source tree.");
  if (!observed.record || observed.record.packageName !== plan.packageName || observed.record.packageVersion !== plan.packageVersion
    || observed.record.sourcePath !== plan.sourcePath || observed.record.sourceHash !== plan.sourceHash || observed.record.targetPath !== plan.targetPath
    || (observed.record.backupPath ?? null) !== (plan.backupPath ?? null)
    || (observed.record.backupPackageVersion ?? null) !== (plan.backupPackageVersion ?? null))
    throw new Error("Package deployment metadata does not match the committed plan.");
  const expectedBackupHash = plan.backupCreated ? plan.previousTargetHash : plan.previousBackupHash;
  if (expectedBackupHash && observed.backupHash !== expectedBackupHash) throw new Error("Package deployment backup does not match its committed pre-state.");
  const manifestHash = await hash(plan.manifestPath);
  const files: PackageDeployFilesProof = {
    proven: true, targetPath: plan.targetPath, sourceHash: plan.sourceHash, targetHash: observed.targetHash,
    copiedFileCount: observed.targetFileCount, metadataPath: observed.metadataPath, metadataHash: observed.metadataHash,
    ...(plan.backupPath ? { backupPath: plan.backupPath } : {}), ...(observed.backupHash ? { backupHash: observed.backupHash } : {}),
    ...(plan.backupPackageVersion ? { backupPackageVersion: plan.backupPackageVersion } : {}),
  };
  return {
    expectedManifestSha256: manifestHash,
    predicates: [{ name: plan.packageName, version: plan.packageVersion, present: true, resolvedPath: plan.targetPath }],
    filesProof: files as unknown as Record<string, unknown>,
    publicResultSeed: publicDeployResult(projectPath, plan.requestedSource, "deployed", { ...plan, dryRun: false }, files) as unknown as Record<string, unknown>,
  };
}

/** 从内部 durable 文件结果构造公开 DTO；绝对路径只保留在 journal 与完成证明中。 */
function publicDeployResult(
  projectPath: string,
  requestedSource: string,
  status: string,
  deployed: PackageDeployFileResult,
  files?: PackageDeployFilesProof,
): PackageDeployResult {
  const sourceAliases = new Map([[resolve(deployed.sourcePath), requestedSource]]);
  const publicFiles = files ? {
    ...files,
    targetPath: projectRelativePath(projectPath, files.targetPath, "files.targetPath"),
    metadataPath: projectRelativePath(projectPath, files.metadataPath, "files.metadataPath"),
    ...(files.backupPath ? { backupPath: projectRelativePath(projectPath, files.backupPath, "files.backupPath") } : {}),
  } : undefined;
  return {
    action: "deploy",
    status,
    manifestPath: projectRelativePath(projectPath, deployed.manifestPath, "manifestPath"),
    packageName: deployed.packageName,
    packageVersion: deployed.packageVersion,
    sourcePath: requestedSource,
    sourceHash: deployed.sourceHash,
    targetPath: projectRelativePath(projectPath, deployed.targetPath, "targetPath"),
    ...(deployed.backupPath ? { backupPath: projectRelativePath(projectPath, deployed.backupPath, "backupPath") } : {}),
    ...(deployed.backupPackageVersion ? { backupPackageVersion: deployed.backupPackageVersion } : {}),
    backupCreated: deployed.backupCreated,
    repeatedDeployment: deployed.repeatedDeployment,
    copiedFileCount: deployed.copiedFileCount,
    dryRun: deployed.dryRun,
    plannedEffects: publicPackageEffects(projectPath, deployed.plannedEffects, sourceAliases) as PackageDeployPlannedEffect[],
    metadataPath: projectRelativePath(projectPath, deployed.metadataPath, "metadataPath"),
    ...(publicFiles ? { files: publicFiles } : {}),
  };
}

function deployPlan(value: Record<string, unknown>): DeployPlan {
  for (const key of ["projectPath", "manifestPath", "packageName", "packageVersion", "sourcePath", "sourceHash", "targetPath", "metadataPath", "requestedSource"])
    if (typeof value[key] !== "string") throw new Error(`Durable package.deploy file plan is missing ${key}.`);
  if (value.requestedPackageName !== null && typeof value.requestedPackageName !== "string") throw new Error("Durable package.deploy file plan has an invalid requestedPackageName.");
  if (typeof value.backupCreated !== "boolean" || typeof value.repeatedDeployment !== "boolean" || typeof value.copiedFileCount !== "number"
    || typeof value.previousTargetExists !== "boolean" || typeof value.previousRecordIdentity !== "string"
    || typeof value.previousBackupExists !== "boolean" || !Array.isArray(value.plannedEffects)) throw new Error("Durable package.deploy file plan is corrupt.");
  return value as unknown as DeployPlan;
}
async function hash(path: string): Promise<string> { return createHash("sha256").update(await readFile(path)).digest("hex"); }

export default defineTool<PackageDeployRequest, PackageDeployResult>({
  name: "package.deploy", summary: "Deploy a local Unity package source folder into Packages/<packageName> with an automatic restore backup.",
  description: "EditMode only. Deploy one local folder containing package.json into Packages/<packageName>, preserving one bounded restore backup when a target exists. The file tree is written once by the durable worker, then Unity PackageManager Resolve, PackageInfo, path, and tree-hash readback prove the imported package. dryRun previews backup/copy effects; timeout never authorizes replay.",
  group: "package", destructiveHint: true, supportsDryRun: true, sideEffects: ["project-file-write", "project-file-delete", "asset-import", "session-affecting"], playModeSupport: "editor-only", handler: "hybrid", supportStatus: "live-bridge", timeoutMs: 120_000,
  examples: [{ description: "Preview deploying a local package", argv: ["tools", "run", "package.deploy", "--input", "{\"source\":\"./Packages/my-package\"}", "--dry-run"] }],
  async execute(request, context) { return runPackageDeploy(request, context); }
});
