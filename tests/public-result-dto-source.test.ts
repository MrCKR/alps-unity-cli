import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

const resultRoots = [
  "unitypkg/Editor/Tools",
  "unitypkg/Editor/Contexts",
];

// 递归读取受公开 Result DTO 约束的 C# 源文件。
async function readCSharpFiles(directory: string): Promise<Array<{ path: string; source: string }>> {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = await Promise.all(entries.map(async (entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) return readCSharpFiles(path);
    if (!entry.isFile() || !entry.name.endsWith(".cs")) return [];
    return [{ path, source: await readFile(path, "utf8") }];
  }));
  return files.flat();
}

function resultTypes(source: string): Array<{ name: string; body: string }> {
  const types: Array<{ name: string; body: string }> = [];
  const declaration = /public\s+(?:(?:sealed|readonly)\s+)?(?:class|struct)\s+(\w*Result)\b/g;
  for (const match of source.matchAll(declaration)) {
    const name = match[1];
    const bodyStart = source.indexOf("{", match.index!);
    let depth = 0;
    for (let index = bodyStart; index < source.length; index += 1) {
      if (source[index] === "{") depth += 1;
      if (source[index] === "}") depth -= 1;
      if (depth === 0) {
        types.push({ name, body: source.slice(bodyStart + 1, index) });
        break;
      }
    }
  }
  return types;
}

test("public Tool and Context Result DTOs exclude generic execution and diagnostic fields", async () => {
  const extensions = (await readdir("extensions", { withFileTypes: true }))
    .filter((entry) => entry.isDirectory())
    .map((entry) => join("extensions", entry.name, "unitypkg"));
  const files = (await Promise.all([...resultRoots, ...extensions].map(readCSharpFiles))).flat();
  const genericField = /public\s+[^;{]+\s+(Command|LogPath|Execution|Logs|Audit|DescriptorFingerprint|HandlerFingerprint)\s*\{\s*get;/g;
  const declared = files.flatMap(({ path, source }) => resultTypes(source).flatMap(({ name, body }) =>
    [...body.matchAll(genericField)].map((match) => `${path.replaceAll("\\", "/")}:${name}.${match[1]}`),
  ));
  assert.deepEqual(declared, []);

  for (const { path, source } of files) {
    for (const { name, body } of resultTypes(source)) {
      for (const field of ["Command", "LogPath", "Execution", "Logs", "Audit", "DescriptorFingerprint", "HandlerFingerprint"]) {
        assert.doesNotMatch(body, new RegExp(`\\b${field}\\s*=`), `${path} must not assign ${name}.${field}`);
      }
    }
  }
});

test("public descriptors do not mention removed generic success wrappers", async () => {
  const extensions = (await readdir("extensions", { withFileTypes: true }))
    .filter((entry) => entry.isDirectory())
    .map((entry) => join("extensions", entry.name, "unitypkg"));
  const files = (await Promise.all([...resultRoots, ...extensions].map(readCSharpFiles))).flat();
  const violations = files
    .filter(({ source }) => /common envelope audit|effects\.playModeChanged/i.test(source))
    .map(({ path }) => path.replaceAll("\\", "/"));
  assert.deepEqual(violations, []);
});
