import { execFile } from "node:child_process";
import { basename } from "node:path";
import { promisify } from "node:util";
import { resolveProjectIdentityAsync } from "./project-identity.js";

const execFileAsync = promisify(execFile);

export interface UnityProjectProcess {
  processId: number;
  processStartedAt: string;
  executablePath: string;
  argv: string[];
  projectPath: string;
}

/** 解析带显式 -projectPath、可正向归因的 interactive Unity.exe 命令行观察。 */
export function parseUnityProjectProcessDiscoveryOutput(stdout: string): UnityProjectProcess[] {
  const text = stdout.trim();
  if (!text) return [];
  const parsed = JSON.parse(text) as unknown;
  const items = Array.isArray(parsed) ? parsed : [parsed];
  return items.flatMap((item): UnityProjectProcess[] => {
    if (!item || typeof item !== "object" || Array.isArray(item)) return [];
    const record = item as Record<string, unknown>;
    if (!Number.isInteger(record.processId) || Number(record.processId) <= 0
      || typeof record.processStartedAt !== "string" || !Number.isFinite(Date.parse(record.processStartedAt))
      || typeof record.executablePath !== "string" || basename(record.executablePath).toLowerCase() !== "unity.exe"
      || !Array.isArray(record.argv) || record.argv.some((value) => typeof value !== "string")) return [];
    const argv = record.argv as string[];
    if (argv.some((value) => value.toLowerCase() === "-batchmode")) return [];
    const projectIndex = argv.findIndex((value) => value.toLowerCase() === "-projectpath");
    if (projectIndex < 0 || !argv[projectIndex + 1]) return [];
    return [{ processId: Number(record.processId), processStartedAt: new Date(Date.parse(record.processStartedAt)).toISOString(), executablePath: record.executablePath, argv, projectPath: argv[projectIndex + 1] }];
  });
}

/** 枚举可提供显式项目参数的 Unity.exe，并以 CanonicalPath v1 精确归因；不覆盖 Registry 正式会话证明。 */
export async function findUnityProjectProcesses(instanceId: string, canonicalProjectPath: string, timeoutMs = 3_000): Promise<UnityProjectProcess[]> {
  if (process.platform !== "win32" || timeoutMs < 1) return [];
  const startedAt = Date.now();
  const { stdout } = await execFileAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", WINDOWS_UNITY_PROCESS_DISCOVERY_SCRIPT], { timeout: timeoutMs, windowsHide: true, maxBuffer: 1024 * 1024 });
  const matches: UnityProjectProcess[] = [];
  for (const candidate of parseUnityProjectProcessDiscoveryOutput(stdout)) {
    const remainingMs = Math.max(0, timeoutMs - (Date.now() - startedAt));
    if (remainingMs < 1) break;
    try {
      if ((await resolveProjectIdentityAsync(candidate.projectPath, { timeoutMs: remainingMs })).instanceId === instanceId) matches.push(candidate);
    } catch {
      // Canonicalization could not prove this candidate; it must not become a project claim.
    }
  }
  return matches.sort((left, right) => left.processId - right.processId);
}

export const WINDOWS_UNITY_PROCESS_DISCOVERY_SCRIPT = `$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class UnityCliProjectProcessArgv { [DllImport("shell32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr CommandLineToArgvW(string commandLine, out int argc); [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr value); public static string[] Parse(string commandLine) { int argc; var memory=CommandLineToArgvW(commandLine,out argc); if(memory==IntPtr.Zero) return null; try { var result=new string[argc]; for(var i=0;i<argc;i++) result[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory,i*IntPtr.Size)); return result; } finally { LocalFree(memory); } } }
"@
$items=@(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop | ForEach-Object { $argv=[UnityCliProjectProcessArgv]::Parse($_.CommandLine); if($argv){ @{ processId=[int]$_.ProcessId; processStartedAt=([DateTime]$_.CreationDate).ToUniversalTime().ToString('O'); executablePath=$_.ExecutablePath; argv=@($argv) } } })
ConvertTo-Json -InputObject $items -Compress -Depth 5`;
