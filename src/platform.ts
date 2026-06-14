import { existsSync } from "node:fs";
import { join } from "node:path";

export function isWindows(platform = process.platform): boolean {
  return platform === "win32";
}

export function defaultDataDir(env = process.env, platform = process.platform): string {
  if (env.ALPS_UNITY_DATA_DIR) {
    return env.ALPS_UNITY_DATA_DIR;
  }
  if (isWindows(platform)) {
    return env.LOCALAPPDATA ? join(env.LOCALAPPDATA, "AlpsUnityCli") : join(process.cwd(), ".unity_cli");
  }

  return join(process.cwd(), ".unity_cli");
}

export function ensureWindows(platform = process.platform): void {
  if (!isWindows(platform) && process.env.ALPS_UNITY_ALLOW_NON_WINDOWS !== "1") {
    throw new Error("Alps Unity CLI runtime is currently Windows-only. Set ALPS_UNITY_ALLOW_NON_WINDOWS=1 only for automated tests.");
  }
}

export function discoverUnity(env = process.env): string | undefined {
  if (env.UNITY_EXE && existsSync(env.UNITY_EXE)) {
    return env.UNITY_EXE;
  }

  const candidates = [
    "C:\\Program Files\\Unity\\Hub\\Editor\\2023.2.20f1\\Editor\\Unity.exe",
    "C:\\Program Files\\Unity\\Hub\\Editor\\2022.3.45f1\\Editor\\Unity.exe",
    "C:\\Program Files\\Unity\\Editor\\Unity.exe"
  ];

  return candidates.find((candidate) => existsSync(candidate));
}
