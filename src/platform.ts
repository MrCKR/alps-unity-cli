import { join } from "node:path";

export function isWindows(platform = process.platform): boolean {
  return platform === "win32";
}

export function defaultDataDir(env = process.env, platform = process.platform): string {
  if (env.UNITYCLI_DATA_DIR) {
    return env.UNITYCLI_DATA_DIR;
  }
  if (isWindows(platform)) {
    return env.LOCALAPPDATA ? join(env.LOCALAPPDATA, "UnityCli") : join(process.cwd(), ".unitycli");
  }

  return join(process.cwd(), ".unitycli");
}

export function ensureWindows(platform = process.platform): void {
  if (!isWindows(platform) && process.env.UNITY_CLI_ALLOW_NON_WINDOWS !== "1") {
    throw new Error("UnityCLI runtime is currently Windows-only. Set UNITY_CLI_ALLOW_NON_WINDOWS=1 only for automated tests.");
  }
}
