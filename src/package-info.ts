import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { getString } from "./args.js";
import { success } from "./output.js";
import type { CliResult } from "./types.js";

interface NpmPackageJson {
  name?: string;
  version?: string;
  description?: string;
  bin?: Record<string, string>;
  engines?: Record<string, string>;
}

interface UnityPackageJson {
  name?: string;
  version?: string;
  displayName?: string;
  description?: string;
  unity?: string;
}

export async function handlePackageInfoCommand(
  flags: Record<string, string | boolean>,
  requestId: string
): Promise<CliResult> {
  const npmPackagePath = fileURLToPath(new URL("../package.json", import.meta.url));
  const unityPackagePath = fileURLToPath(new URL("../unitypkg/package.json", import.meta.url));
  const npmPackage = JSON.parse(await readFile(npmPackagePath, "utf8")) as NpmPackageJson;
  const unityPackage = JSON.parse(await readFile(unityPackagePath, "utf8")) as UnityPackageJson;
  const format = getString(flags, "format");

  const data: Record<string, unknown> = {
    command: "package",
    npm: {
      name: npmPackage.name ?? "unity_cli",
      version: npmPackage.version ?? "0.0.0",
      description: npmPackage.description,
      bin: npmPackage.bin,
      engines: npmPackage.engines
    },
    unity: {
      name: unityPackage.name ?? "com.unity_cli",
      version: unityPackage.version ?? "0.0.0",
      displayName: unityPackage.displayName,
      description: unityPackage.description,
      unity: unityPackage.unity
    },
    docs: {
      builtin: "unity_cli docs / unity_cli docs topic <topic>",
      offlineUnityApi: "unity_cli docs get/search --docs-dir <path>",
      reflect: "unity_cli reflect type/member/search --reflect-dir <path>"
    },
    discovery: {
      builtinTools: "unity_cli tools list",
      unifiedTools: "unity_cli tools list --include-custom",
      projectTools: "unity_cli tools list --include-custom"
    }
  };

  if (format === "text") {
    data.text = [
      `${(data.npm as { name: string }).name}@${(data.npm as { version: string }).version}`,
      `Unity package: ${(data.unity as { name: string }).name}@${(data.unity as { version: string }).version}`,
      "Docs: unity_cli docs / docs topic <topic> / docs get/search",
      "Discovery: tools list [--include-custom], tools groups/search/describe"
    ].join("\n");
  }

  return success(requestId, data);
}
