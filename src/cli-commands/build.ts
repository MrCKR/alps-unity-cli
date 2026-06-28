import { resolve } from "node:path";
import { getString } from "../args.js";
import { failure, success } from "../output.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";
import { startUnityJob, summarizeJob } from "./unity-job.js";

export async function handleBuild(command: string | undefined, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult | undefined> {
  if (command === "run") {
    const targets = buildTargets(flags);
    if (targets.length <= 1) {
      return startBuildJob(targets[0] ?? "StandaloneWindows64", flags, runtime, requestId, getString(flags, "output"));
    }

    const jobs: Array<Record<string, unknown>> = [];
    for (const target of targets) {
      const result = await startBuildJob(target, flags, runtime, requestId, outputForTarget(getString(flags, "output"), target));
      if (!result.ok) return result;
      jobs.push(result.data as Record<string, unknown>);
    }
    return success(requestId, {
      command: "build.run",
      mode: "batch",
      targetCount: targets.length,
      targets,
      summary: { queued: jobs.length, failedToQueue: 0 },
      jobs
    });
  }

  if (command === "status") {
    const jobId = requiredBuildJobId(flags, requestId);
    if (!jobId.ok) return jobId.error;
    const job = await runtime.store.get(jobId.id);
    if (!job) return failure(requestId, "BUILD_JOB_NOT_FOUND", `Build job not found: ${jobId.id}.`, ["Pass a build jobId returned by tools run build.run."]);
    if (job.kind !== "build") return failure(requestId, "BUILD_JOB_KIND_MISMATCH", `Job is not a build job: ${job.kind}.`, ["Pass a jobId returned by tools run build.run."]);
    return success(requestId, buildJobToolSummary("build.status", await summarizeJob(job)));
  }

  if (command === "cancel") {
    const jobId = requiredBuildJobId(flags, requestId);
    if (!jobId.ok) return jobId.error;
    const existing = await runtime.store.get(jobId.id);
    if (!existing) return failure(requestId, "BUILD_JOB_NOT_FOUND", `Build job not found: ${jobId.id}.`, ["Pass a build jobId returned by tools run build.run."]);
    if (existing.kind !== "build") return failure(requestId, "BUILD_JOB_KIND_MISMATCH", `Job is not a build job: ${existing.kind}.`, ["Pass a jobId returned by tools run build.run."]);
    try {
      const job = await runtime.runner.cancel(jobId.id);
      return success(requestId, buildJobToolSummary("build.cancel", await summarizeJob(job)));
    } catch (error) {
      return failure(requestId, "BUILD_CANCEL_FAILED", error instanceof Error ? error.message : String(error), ["Check build.status for the current job state."]);
    }
  }

  return undefined;
}

function startBuildJob(target: string, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string, output?: string): Promise<CliResult> {
  const outputPath = resolve(output ?? "Builds/Windows/UnityCliBuild.exe");
  return startUnityJob("build", flags, runtime, requestId, ["-buildTarget", target, "-executeMethod", "UnityCli.UnityCliBatch.BuildProject", "-unityCliBuildOutput", outputPath], {
    action: "build",
    target,
    outputPath,
    resultPath: outputPath
  });
}

function buildTargets(flags: Record<string, string | boolean>): string[] {
  const structured = structuredInput(flags);
  const rawTargets = structured.targets;
  if (Array.isArray(rawTargets)) return rawTargets.filter((item): item is string => typeof item === "string" && item.length > 0);
  const targets = getString(flags, "targets") ?? (typeof rawTargets === "string" ? rawTargets : undefined);
  if (targets) return targets.split(",").map((target) => target.trim()).filter(Boolean);
  return [getString(flags, "target") ?? "StandaloneWindows64"];
}

function outputForTarget(output: string | undefined, target: string): string | undefined {
  if (!output) return undefined;
  return output.includes("{target}") ? output.replace(/\{target\}/g, target) : `${output.replace(/[\\/]$/, "")}/${target}`;
}

function structuredInput(flags: Record<string, string | boolean>): Record<string, unknown> {
  const raw = typeof flags.__structuredInput === "string" ? flags.__structuredInput : undefined;
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw);
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed as Record<string, unknown> : {};
  } catch {
    return {};
  }
}

function requiredBuildJobId(flags: Record<string, string | boolean>, requestId: string): { ok: true; id: string } | { ok: false; error: CliResult } {
  const id = getString(flags, "job-id") ?? getString(flags, "job") ?? getString(flags, "id");
  return id ? { ok: true, id } : { ok: false, error: failure(requestId, "BUILD_JOB_ID_REQUIRED", "Pass a build jobId.", ["Run tools run build.run first, then pass the returned jobId."]) };
}

function buildJobToolSummary(command: "build.status" | "build.cancel", summary: Record<string, unknown>): Record<string, unknown> {
  const { command: jobCommand, ...rest } = summary;
  return { command, ...rest, jobCommand };
}
