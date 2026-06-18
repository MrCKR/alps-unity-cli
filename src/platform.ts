import { existsSync, readdirSync } from "node:fs";
import { join } from "node:path";

export function isWindows(platform = process.platform): boolean {
  return platform === "win32";
}

export function defaultDataDir(env = process.env, platform = process.platform): string {
  if (env.UNITY_CLI_DATA_DIR) {
    return env.UNITY_CLI_DATA_DIR;
  }
  if (isWindows(platform)) {
    return env.LOCALAPPDATA ? join(env.LOCALAPPDATA, "UnityCli") : join(process.cwd(), ".unity_cli");
  }

  return join(process.cwd(), ".unity_cli");
}

export function ensureWindows(platform = process.platform): void {
  if (!isWindows(platform) && process.env.UNITY_CLI_ALLOW_NON_WINDOWS !== "1") {
    throw new Error("Unity CLI runtime is currently Windows-only. Set UNITY_CLI_ALLOW_NON_WINDOWS=1 only for automated tests.");
  }
}

/** Return the latest Unity Editor executable found under the Unity Hub install directory.
 * Scans Hub/Editor/<version>/Editor/ for the Unity binary, preferring the newest version.
 */
function discoverFromHub(hubDir: string, platform: string): string | undefined {
  if (!existsSync(hubDir)) return undefined;

  const binaryName = platform === "win32" ? "Unity.exe" : "Unity";

  try {
    const versions = readdirSync(hubDir)
      .filter((entry) => /^\d+\.\d+\.\d+[a-z]f\d$/i.test(entry))
      .sort()
      .reverse(); // newest first

    for (const version of versions) {
      const candidate = join(hubDir, version, "Editor", binaryName);
      if (existsSync(candidate)) return candidate;
    }
  } catch {
    // Permission or I/O error — skip Hub scan.
  }

  return undefined;
}

export function discoverUnity(env = process.env, platform = process.platform): string | undefined {
  // 1. Explicit UNITY_EXE override (skip existsSync to let spawn surface the real error)
  if (env.UNITY_EXE) {
    return env.UNITY_EXE;
  }

  // 2. Dynamic Hub directory scan (newest version first)
  const hubDirs = platform === "win32"
    ? ["C:\\Program Files\\Unity\\Hub\\Editor"]
    : platform === "darwin"
    ? ["/Applications/Unity/Hub/Editor"]
    : ["/opt/Unity/Hub/Editor", join(process.env.HOME ?? "/root", "Unity", "Hub", "Editor")];

  for (const hubDir of hubDirs) {
    const found = discoverFromHub(hubDir, platform);
    if (found) return found;
  }

  // 3. Fallback: well-known non-Hub install locations
  const fallbacks = platform === "win32"
    ? ["C:\\Program Files\\Unity\\Editor\\Unity.exe"]
    : platform === "darwin"
    ? ["/Applications/Unity/Unity.app/Contents/MacOS/Unity"]
    : ["/opt/Unity/Editor/Unity", "/usr/local/bin/Unity"];

  return fallbacks.find((candidate) => existsSync(candidate));
}
