import { createHash } from "node:crypto";
import { existsSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { spawn, spawnSync } from "node:child_process";

export const CANONICALIZATION_VERSION = 1 as const;
export const INSTANCE_ID_PREFIX = "unity-";

export type ProjectIdentityErrorCode =
  | "E_INSTANCE_REQUIRED"
  | "E_INSTANCE_PATH_UNAVAILABLE"
  | "E_INSTANCE_REMOTE_PATH_UNSUPPORTED"
  | "E_INSTANCE_PATH_CASE_SENSITIVE_UNSUPPORTED"
  | "E_INSTANCE_IDENTITY_MISMATCH";

export interface ProjectIdentity {
  canonicalizationVersion: typeof CANONICALIZATION_VERSION;
  instanceId: string;
  identityHash: string;
  canonicalProjectPath: string;
  displayProjectPath: string;
}

export interface NativeCanonicalProjectPathResult {
  success: boolean;
  canonicalizationVersion: number;
  canonicalProjectPath?: string;
  displayProjectPath?: string;
  identityHash?: string;
  instanceId?: string;
  errorCode?: ProjectIdentityErrorCode;
  errorMessage?: string;
}

export type NativeCanonicalProjectPathResolver = (inputPath: string) => NativeCanonicalProjectPathResult;
export type AsyncNativeCanonicalProjectPathResolver = (inputPath: string, options: CanonicalProjectPathResolveOptions) => Promise<NativeCanonicalProjectPathResult>;

export interface CanonicalProjectPathResolveOptions {
  timeoutMs: number;
  signal?: AbortSignal;
}

/** CanonicalPath v1 的结构化失败。 */
export class ProjectIdentityError extends Error {
  public constructor(
    public readonly code: ProjectIdentityErrorCode,
    message: string,
    public readonly details: Record<string, unknown> = {},
  ) {
    super(message);
    this.name = "ProjectIdentityError";
  }
}

const nativeResultCache = new Map<string, NativeCanonicalProjectPathResult>();

/** 清理进程内 identity cache；测试和显式文件系统变更后使用。 */
export function clearProjectIdentityCache(): void {
  nativeResultCache.clear();
}

/**
 * 解析 CanonicalPath v1 identity。
 * Win32 final-path、volume、case-sensitive flag 和 lowercase 全部由共享 C# helper 完成；TS 只验证 UTF-8 SHA-256。
 */
export function resolveProjectIdentity(
  inputPath: string,
  nativeResolver: NativeCanonicalProjectPathResolver = resolveNativeCanonicalProjectPath,
): ProjectIdentity {
  if (!inputPath?.trim()) {
    throw new ProjectIdentityError("E_INSTANCE_REQUIRED", "A Unity project path is required.");
  }

  const native = nativeResolver(inputPath);
  return projectIdentityFromNative(inputPath, native);
}

/** 从 CWD 或显式路径发现 Unity root；错误必须由调用方转为稳定 envelope。 */
export function resolveProjectIdentityFromCwd(
  cwd = process.cwd(),
  nativeResolver: NativeCanonicalProjectPathResolver = resolveNativeCanonicalProjectPath,
): ProjectIdentity {
  return resolveProjectIdentity(cwd, nativeResolver);
}

/**
 * 在调用方给定的剩余预算内解析 CanonicalPath identity。
 * 超时或取消会终止 helper 子进程，且这种 transient 失败不写入 identity cache。
 */
export async function resolveProjectIdentityAsync(
  inputPath: string,
  options: CanonicalProjectPathResolveOptions,
  nativeResolver: AsyncNativeCanonicalProjectPathResolver = resolveNativeCanonicalProjectPathAsync,
): Promise<ProjectIdentity> {
  if (!inputPath?.trim()) {
    throw new ProjectIdentityError("E_INSTANCE_REQUIRED", "A Unity project path is required.");
  }
  if (!Number.isFinite(options.timeoutMs) || options.timeoutMs <= 0 || options.signal?.aborted) {
    throw new ProjectIdentityError("E_INSTANCE_PATH_UNAVAILABLE", "Canonical path identity resolution exceeded its available observation budget.", { inputPath });
  }
  const native = await resolveNativeWithinBudget(inputPath, options, nativeResolver);
  return projectIdentityFromNative(inputPath, native);
}

/** Injected resolvers and child-process resolvers alike must not outlive the caller deadline. */
async function resolveNativeWithinBudget(
  inputPath: string,
  options: CanonicalProjectPathResolveOptions,
  nativeResolver: AsyncNativeCanonicalProjectPathResolver,
): Promise<NativeCanonicalProjectPathResult> {
  const unavailable = (): NativeCanonicalProjectPathResult => ({
    success: false,
    canonicalizationVersion: CANONICALIZATION_VERSION,
    errorCode: "E_INSTANCE_PATH_UNAVAILABLE",
    errorMessage: "Canonical path identity resolution exceeded its available observation budget.",
  });
  return await new Promise<NativeCanonicalProjectPathResult>((complete) => {
    let settled = false;
    const finish = (result: NativeCanonicalProjectPathResult) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      options.signal?.removeEventListener("abort", abort);
      complete(result);
    };
    const abort = () => finish(unavailable());
    const timeout = setTimeout(abort, Math.max(1, options.timeoutMs));
    options.signal?.addEventListener("abort", abort, { once: true });
    void nativeResolver(inputPath, options).then(finish, () => finish(unavailable()));
  });
}

/** 异步 CWD 入口仅供有绝对 deadline 的 observation selector 使用。 */
export function resolveProjectIdentityFromCwdAsync(
  cwd = process.cwd(),
  options: CanonicalProjectPathResolveOptions,
  nativeResolver: AsyncNativeCanonicalProjectPathResolver = resolveNativeCanonicalProjectPathAsync,
): Promise<ProjectIdentity> {
  return resolveProjectIdentityAsync(cwd, options, nativeResolver);
}

function projectIdentityFromNative(inputPath: string, native: NativeCanonicalProjectPathResult): ProjectIdentity {
  if (!native.success) {
    throw new ProjectIdentityError(
      native.errorCode ?? "E_INSTANCE_PATH_UNAVAILABLE",
      native.errorMessage ?? "Unity project path identity could not be resolved.",
      { inputPath },
    );
  }

  if (native.canonicalizationVersion !== CANONICALIZATION_VERSION
    || !native.canonicalProjectPath
    || !native.displayProjectPath) {
    throw new ProjectIdentityError("E_INSTANCE_IDENTITY_MISMATCH", "Canonical path helper returned an incompatible identity version or incomplete result.", {
      inputPath,
      canonicalizationVersion: native.canonicalizationVersion,
    });
  }

  const identityHash = createHash("sha256").update(Buffer.from(native.canonicalProjectPath, "utf8")).digest("hex");
  const instanceId = `${INSTANCE_ID_PREFIX}${identityHash}`;
  if (native.identityHash !== identityHash || native.instanceId !== instanceId) {
    throw new ProjectIdentityError("E_INSTANCE_IDENTITY_MISMATCH", "TypeScript and Win32 canonical identity hashes differ.", {
      inputPath,
      canonicalProjectPath: native.canonicalProjectPath,
      expectedInstanceId: instanceId,
      nativeInstanceId: native.instanceId ?? null,
    });
  }

  return {
    canonicalizationVersion: CANONICALIZATION_VERSION,
    instanceId,
    identityHash,
    canonicalProjectPath: native.canonicalProjectPath,
    displayProjectPath: native.displayProjectPath,
  };
}

/** 调用与 Unity Bridge 共用的纯 Win32 C# helper。 */
function resolveNativeCanonicalProjectPath(inputPath: string): NativeCanonicalProjectPathResult {
  const cacheKey = resolve(inputPath);
  const cached = nativeResultCache.get(cacheKey);
  if (cached) return cached;

  const helperSource = canonicalHelperSourcePath();
  const command = [
    "$ErrorActionPreference = 'Stop'",
    "$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)",
    "Add-Type -Path $env:UNITY_CLI_CANONICAL_HELPER_SOURCE",
    "$result = [UnityCli.UnityCliCanonicalProjectPath]::Resolve($env:UNITY_CLI_CANONICAL_PATH_INPUT)",
    "$result | ConvertTo-Json -Compress -Depth 4",
  ].join("\n");
  const encodedCommand = Buffer.from(command, "utf16le").toString("base64");
  const powershell = process.env.SystemRoot
    ? resolve(process.env.SystemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
    : "powershell.exe";
  const execution = spawnSync(powershell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encodedCommand], {
    encoding: "utf8",
    windowsHide: true,
    timeout: 15_000,
    maxBuffer: 4 * 1024 * 1024,
    env: {
      ...process.env,
      UNITY_CLI_CANONICAL_HELPER_SOURCE: helperSource,
      UNITY_CLI_CANONICAL_PATH_INPUT: inputPath,
    },
  });

  if (execution.error || execution.status !== 0) {
    const result: NativeCanonicalProjectPathResult = {
      success: false,
      canonicalizationVersion: CANONICALIZATION_VERSION,
      errorCode: "E_INSTANCE_PATH_UNAVAILABLE",
      errorMessage: execution.error?.message ?? (execution.stderr.trim() || "Canonical path helper failed."),
    };
    nativeResultCache.set(cacheKey, result);
    return result;
  }

  try {
    const line = execution.stdout.split(/\r?\n/).map((value) => value.trim()).filter(Boolean).at(-1);
    const raw = JSON.parse(line ?? "{}") as Record<string, unknown>;
    const result: NativeCanonicalProjectPathResult = {
      success: raw.Success === true,
      canonicalizationVersion: Number(raw.CanonicalizationVersion),
      canonicalProjectPath: typeof raw.CanonicalProjectPath === "string" ? raw.CanonicalProjectPath : undefined,
      displayProjectPath: typeof raw.DisplayProjectPath === "string" ? raw.DisplayProjectPath : undefined,
      identityHash: typeof raw.IdentityHash === "string" ? raw.IdentityHash : undefined,
      instanceId: typeof raw.InstanceId === "string" ? raw.InstanceId : undefined,
      errorCode: typeof raw.ErrorCode === "string" ? raw.ErrorCode as ProjectIdentityErrorCode : undefined,
      errorMessage: typeof raw.ErrorMessage === "string" ? raw.ErrorMessage : undefined,
    };
    nativeResultCache.set(cacheKey, result);
    return result;
  } catch (error) {
    const result: NativeCanonicalProjectPathResult = {
      success: false,
      canonicalizationVersion: CANONICALIZATION_VERSION,
      errorCode: "E_INSTANCE_PATH_UNAVAILABLE",
      errorMessage: `Canonical path helper returned invalid JSON: ${error instanceof Error ? error.message : String(error)}`,
    };
    nativeResultCache.set(cacheKey, result);
    return result;
  }
}

/** 异步 helper 与同步路径复用完全相同的 C# source、PowerShell command 和 identity validation。 */
function resolveNativeCanonicalProjectPathAsync(inputPath: string, options: CanonicalProjectPathResolveOptions): Promise<NativeCanonicalProjectPathResult> {
  const cached = nativeResultCache.get(resolve(inputPath));
  if (cached?.success) return Promise.resolve(cached);
  const helperSource = canonicalHelperSourcePath();
  const command = [
    "$ErrorActionPreference = 'Stop'",
    "$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)",
    "Add-Type -Path $env:UNITY_CLI_CANONICAL_HELPER_SOURCE",
    "$result = [UnityCli.UnityCliCanonicalProjectPath]::Resolve($env:UNITY_CLI_CANONICAL_PATH_INPUT)",
    "$result | ConvertTo-Json -Compress -Depth 4",
  ].join("\n");
  const powershell = process.env.SystemRoot
    ? resolve(process.env.SystemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
    : "powershell.exe";
  return new Promise((complete) => {
    const child = spawn(powershell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Buffer.from(command, "utf16le").toString("base64")], {
      windowsHide: true,
      env: { ...process.env, UNITY_CLI_CANONICAL_HELPER_SOURCE: helperSource, UNITY_CLI_CANONICAL_PATH_INPUT: inputPath },
    });
    let stdout = "";
    let settled = false;
    const finish = (result: NativeCanonicalProjectPathResult) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      options.signal?.removeEventListener("abort", abort);
      complete(result);
    };
    const unavailable = (message: string): NativeCanonicalProjectPathResult => ({ success: false, canonicalizationVersion: CANONICALIZATION_VERSION, errorCode: "E_INSTANCE_PATH_UNAVAILABLE", errorMessage: message });
    const abort = () => {
      child.kill();
      finish(unavailable("Canonical path identity resolution was cancelled or exceeded its observation budget."));
    };
    const timeout = setTimeout(abort, Math.max(1, options.timeoutMs));
    options.signal?.addEventListener("abort", abort, { once: true });
    child.stdout.on("data", (chunk: Buffer | string) => { stdout += chunk.toString(); });
    child.once("error", (error) => finish(unavailable(error.message)));
    child.once("close", (status) => {
      if (status !== 0) return finish(unavailable("Canonical path helper failed."));
      try {
        const line = stdout.split(/\r?\n/).map((value) => value.trim()).filter(Boolean).at(-1);
        const raw = JSON.parse(line ?? "{}") as Record<string, unknown>;
        const result: NativeCanonicalProjectPathResult = {
          success: raw.Success === true, canonicalizationVersion: Number(raw.CanonicalizationVersion),
          canonicalProjectPath: typeof raw.CanonicalProjectPath === "string" ? raw.CanonicalProjectPath : undefined,
          displayProjectPath: typeof raw.DisplayProjectPath === "string" ? raw.DisplayProjectPath : undefined,
          identityHash: typeof raw.IdentityHash === "string" ? raw.IdentityHash : undefined,
          instanceId: typeof raw.InstanceId === "string" ? raw.InstanceId : undefined,
          errorCode: typeof raw.ErrorCode === "string" ? raw.ErrorCode as ProjectIdentityErrorCode : undefined,
          errorMessage: typeof raw.ErrorMessage === "string" ? raw.ErrorMessage : undefined,
        };
        if (result.success) nativeResultCache.set(resolve(inputPath), result);
        finish(result);
      } catch (error) {
        finish(unavailable(`Canonical path helper returned invalid JSON: ${error instanceof Error ? error.message : String(error)}`));
      }
    });
  });
}

/** 定位 npm package 中随 Bridge 一起发布的共享 helper 源码。 */
function canonicalHelperSourcePath(): string {
  const candidates = [
    fileURLToPath(new URL("../unitypkg/Editor/Infrastructure/UnityCliCanonicalProjectPath.cs", import.meta.url)),
    resolve(process.cwd(), "unitypkg", "Editor", "Infrastructure", "UnityCliCanonicalProjectPath.cs"),
  ];
  const source = candidates.find((candidate) => existsSync(candidate));
  if (!source) {
    throw new ProjectIdentityError("E_INSTANCE_PATH_UNAVAILABLE", "Canonical path helper source is missing from the installed CLI package.", { candidates });
  }
  return source;
}
