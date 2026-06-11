import fs from "node:fs";
import path from "node:path";
import { normalizeFsPath } from "./paths.js";
import { pathExists, readJsonFile } from "./json-file.js";

export async function readConfig(configFile) {
  return (await readJsonFile(configFile)) ?? {};
}

export async function discoverProject(options) {
  const candidates = [
    options.explicitProject,
    await findProjectRoot(options.cwd),
    options.config.defaultProject,
    ...(options.config.recentProjects ?? [])
  ].filter(Boolean);

  for (const candidate of candidates) {
    const resolved = path.resolve(candidate);
    if (await isUnityProject(resolved)) {
      return readProjectInfo(resolved);
    }
  }

  return undefined;
}

export async function findProjectRoot(startDirectory) {
  let current = path.resolve(startDirectory);
  while (true) {
    if (await isUnityProject(current)) {
      return current;
    }
    const parent = path.dirname(current);
    if (parent === current) {
      return undefined;
    }
    current = parent;
  }
}

export async function isUnityProject(projectPath) {
  const projectSettings = path.join(projectPath, "ProjectSettings", "ProjectVersion.txt");
  const assets = path.join(projectPath, "Assets");
  const packages = path.join(projectPath, "Packages");
  return (await pathExists(projectSettings)) && (await pathExists(assets)) && (await pathExists(packages));
}

export async function readProjectInfo(projectPath) {
  const projectVersion = await readUnityVersion(projectPath);
  const manifestPath = path.join(projectPath, "Packages", "manifest.json");
  const manifest = await readJsonFile(manifestPath);
  return {
    path: projectPath,
    name: path.basename(projectPath),
    unityVersion: projectVersion,
    manifestPath: (await pathExists(manifestPath)) ? manifestPath : undefined,
    packages: manifest?.dependencies ? Object.keys(manifest.dependencies).sort() : []
  };
}

export async function readUnityVersion(projectPath) {
  try {
    const raw = await fs.promises.readFile(path.join(projectPath, "ProjectSettings", "ProjectVersion.txt"), "utf8");
    const match = raw.match(/^m_EditorVersion:\s*(.+)$/m);
    return match?.[1]?.trim();
  } catch {
    return undefined;
  }
}

export function sameProject(left, right) {
  return normalizeFsPath(left) === normalizeFsPath(right);
}
