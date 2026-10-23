import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";

/** 读取本 CLI 自身版本；`--version`、`--help` 与安装结果共用同一读取 owner。 */
export async function cliVersion(): Promise<string> {
  const packagePath = fileURLToPath(new URL("../package.json", import.meta.url));
  const packageJson = JSON.parse(await readFile(packagePath, "utf8")) as { version?: string };
  return packageJson.version ?? "0.0.0";
}
