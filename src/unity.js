import fs from "node:fs";
import path from "node:path";
import { pathExists } from "./json-file.js";

const UNITY_HUB_EDITOR_ROOTS = [
  "C:/Program Files/Unity/Hub/Editor",
  "C:/Program Files (x86)/Unity/Hub/Editor"
];

export async function discoverUnityExecutable(options) {
  if (options.explicitUnity) {
    return { path: path.resolve(options.explicitUnity), source: "argument" };
  }

  if (options.config.unityExecutable) {
    return { path: path.resolve(options.config.unityExecutable), source: "config" };
  }

  for (const root of UNITY_HUB_EDITOR_ROOTS) {
    if (!(await pathExists(root))) {
      continue;
    }
    const versions = await fs.promises.readdir(root, { withFileTypes: true });
    const versionNames = versions.filter((entry) => entry.isDirectory()).map((entry) => entry.name).sort().reverse();
    for (const versionName of versionNames) {
      const executable = path.join(root, versionName, "Editor", "Unity.exe");
      if (await pathExists(executable)) {
        return { path: executable, source: "unityHub" };
      }
    }
  }

  return { path: "", source: "notFound" };
}
