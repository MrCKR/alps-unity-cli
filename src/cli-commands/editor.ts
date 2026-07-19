import { getBoolean, getNumber, getString } from "../args.js";
import { resolve } from "node:path";
import { bridgeCommandRegistry, executeBridgePayload } from "../bridge/index.js";
import { dismissUnityLaunchDialog, DISMISS_BUTTON_LABEL, LAUNCH_DIALOG_TITLE_FRAGMENTS } from "../launch-dialog-dismiss.js";
import { durationMsFrom, failure, success } from "../output.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";
import { runUnityJob, startUnityJob } from "./unity-job.js";
import { ensurePackageBridge } from "../package-manager.js";
import { resolveProjectIdentity } from "../project-identity.js";
import type { BridgeRuntime } from "../bridge/index.js";
import type { JobRunner } from "../job-runner.js";
import { StructuredToolError } from "../tools/errors.js";

export interface AssetRefreshServiceRequest {
  path?: string;
  forceUpdate?: boolean;
  importRecursive?: boolean;
  project?: string;
  unity?: string;
  wait?: boolean;
  waitTimeoutMs?: number;
  instance?: string;
  job?: boolean;
  dryRun?: boolean;
}

export interface AssetRefreshRuntime extends BridgeRuntime {
  dataDir: string;
  runner?: JobRunner;
}

/** 根据 job 选项直接执行 live Bridge 或 batchmode Bridge job。 */
export async function executeAssetRefresh(request: AssetRefreshServiceRequest, runtime: AssetRefreshRuntime, requestId: string): Promise<Record<string, unknown>> {
  const input = compact({ path: request.path, forceUpdate: request.forceUpdate, importRecursive: request.importRecursive, dryRun: request.dryRun });
  if (!request.job) {
    const result = await executeBridgePayload(requestId, "project-tool.run", { name: "asset.refresh", input }, compact({
      instance: request.instance,
      "dry-run": request.dryRun
    }) as Record<string, string | boolean>, runtime, undefined, { mode: "execute", mutation: true });
    if (!result.ok) {
      throw StructuredToolError.from(result.error ?? { code: "ASSET_REFRESH_FAILED", message: "asset.refresh failed." });
    }
    return (result.data ?? {}) as Record<string, unknown>;
  }

  if (!runtime.runner) {
    throw new StructuredToolError("E_TOOL_HANDLER_UNSUPPORTED", "Tool \"asset.refresh\" requires a job runner runtime for batchmode execution.");
  }
  const projectPath = resolve(request.project ?? process.cwd());
  const bridge = await ensurePackageBridge(projectPath);
  const identity = resolveProjectIdentity(projectPath);
  const payload = {
    protocolVersion: 2,
    command: "project-tool.run",
    requestId,
    instanceId: identity.instanceId,
    input: { name: "asset.refresh", input },
    execution: {
      absoluteDeadlineUtc: new Date(Date.now() + 60_000).toISOString(),
      completionContractVersion: 1,
      requestDigestVersion: 1,
      ...(request.dryRun ? { dryRun: true } : {})
    }
  };
  return runUnityJob({
    kind: "bridge",
    requestId,
    project: projectPath,
    unity: request.unity,
    args: ["-executeMethod", "UnityCli.UnityCliBatch.RunBridgeCommand", "-unityCliBridgeRequest", JSON.stringify(payload)],
    result: {
      command: "asset.refresh",
      bridgeCommand: "project-tool.run",
      mode: "bridge-job",
      execution: { channel: "batchmode", exitState: "queued" },
      supportsJob: true,
      args: input,
      dryRun: request.dryRun === true,
      bridge
    },
    wait: request.wait,
    waitTimeoutMs: request.waitTimeoutMs
  }, runtime.runner);
}

/** 删除 undefined 字段，供旧 asset.refresh service 的 Bridge input 使用。 */
function compact(input: Record<string, unknown>): Record<string, unknown> {
  return Object.fromEntries(Object.entries(input).filter(([, value]) => value !== undefined));
}

/** editor.refresh batch job 的 Unity executeMethod 参数。 */
function refreshArgs(flags: Record<string, string | boolean>): string[] {
  return ["-executeMethod", getBoolean(flags, "compile") ? "UnityCli.UnityCliBatch.RefreshAndCompile" : "UnityCli.UnityCliBatch.Refresh"];
}
