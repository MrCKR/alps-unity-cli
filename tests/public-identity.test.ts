import assert from "node:assert/strict";
import { access, readFile, readdir } from "node:fs/promises";
import { extname, join, resolve } from "node:path";
import test from "node:test";

const textExtensions = new Set([".cjs", ".cs", ".json", ".md", ".mjs", ".ps1", ".ts", ".yaml", ".yml"]);
const publicRoots = ["src", "unitypkg", "extensions", "docs", "schemas", "contracts", "scripts", "skills/unitycli", "skill-factory", ".pi"];
const publicRootFiles = ["AGENTS.md", "README.md", "package.json", "package-lock.json"];

/** 递归收集会进入产品、公开契约或仓库治理投影的文本文件。 */
async function collectTextFiles(root: string): Promise<string[]> {
  const result: string[] = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const path = join(root, entry.name);
    if (entry.isDirectory()) result.push(...await collectTextFiles(path));
    else if (entry.isFile() && textExtensions.has(extname(entry.name).toLowerCase())) result.push(path);
  }
  return result;
}

test("public package, executable, UPM, and Skill identities are unified", async () => {
  const npmManifest = JSON.parse(await readFile("package.json", "utf8")) as { name: string; bin: Record<string, string> };
  const upmManifest = JSON.parse(await readFile("unitypkg/package.json", "utf8")) as { name: string; displayName: string };
  const skillManifest = JSON.parse(await readFile("skill-factory/unitycli/manifest.json", "utf8")) as { name: string };
  const skill = await readFile("skills/unitycli/SKILL.md", "utf8");
  const agent = await readFile("skill-factory/unitycli/agents/interface.yaml", "utf8");

  assert.equal(npmManifest.name, "@alpsckr/unitycli");
  assert.deepEqual(npmManifest.bin, { unitycli: "dist/cli.js" });
  assert.equal(upmManifest.name, "com.alpsckr.unitycli");
  assert.equal(upmManifest.displayName, "UnityCLI");
  assert.equal(skillManifest.name, "unitycli");
  assert.match(skill, /^---\r?\nname: unitycli\r?$/m);
  assert.match(agent, /display_name: "UnityCLI"/);
  assert.match(agent, /Use \$unitycli for every Unity task/);
  await assert.rejects(access(resolve("skills", "unity" + "_cli")));
  await assert.rejects(access(resolve("skills", "unity-cli")));
});

test("public product text contains no superseded identity spelling", async () => {
  const files = [...publicRootFiles];
  for (const root of publicRoots) files.push(...await collectTextFiles(root));
  const forbidden = [
    "unity" + "_cli",
    "Unity" + " CLI",
    "com.unity" + "_cli",
    "@alpsckr/unity" + "_cli",
    "UNITY" + "_CLI_DATA_DIR",
  ];

  for (const file of files) {
    const text = await readFile(file, "utf8");
    for (const spelling of forbidden) assert.equal(text.includes(spelling), false, `${file} contains ${spelling}`);
  }
});

test("public schema identifiers use the UnityCLI domain", async () => {
  const schema = JSON.parse(await readFile("schemas/tool-metadata.schema.json", "utf8")) as { $id: string; title: string };
  assert.match(schema.$id, /^https:\/\/unitycli\.local\//);
  assert.match(schema.title, /^UnityCLI\b/);
});
