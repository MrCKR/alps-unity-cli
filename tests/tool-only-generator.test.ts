import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { access, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { promisify } from "node:util";
import test from "node:test";

const execFileAsync = promisify(execFile);

test("Tool registry generation is idempotent and emits no second capability registry", async () => {
  const path = "src/tools/.generated-registry.ts";
  const before = await readFile(path, "utf8");
  await execFileAsync(process.execPath, ["scripts/gen-ts-registries.mjs"]);
  const after = await readFile(path, "utf8");
  assert.equal(after, before);
  assert.match(after, /\.\/editor\/dialogs\.js/);
  assert.match(after, /\.\/editor\/state\.js/);
  await assert.rejects(access("src/contexts"), { code: "ENOENT" });
});

test("Tool registry generator rejects removed read-authoring input", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-cli-tool-only-generator-"));
  try {
    await mkdir(join(root, "src", "tools"), { recursive: true });
    await mkdir(join(root, "src", "contexts"), { recursive: true });
    await writeFile(join(root, "src", "tools", "sample.ts"), 'export default defineTool({ name: "sample.tool" });\n', "utf8");
    const removedFactory = "define" + "Context";
    await writeFile(join(root, "src", "contexts", "removed.ts"), `export default ${removedFactory}({ name: "removed.read" });\n`, "utf8");
    await assert.rejects(
      execFileAsync(process.execPath, ["scripts/gen-ts-registries.mjs", root]),
      /Context authoring directory is forbidden/,
    );
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
