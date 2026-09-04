import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import test from "node:test";

const domains = ["animation", "asset", "build", "camera", "component", "controller", "editor", "go", "graphics", "material"];
const toolsRoot = new URL("../unitypkg/Editor/Tools/", import.meta.url);

async function sourceFiles(directory: URL): Promise<URL[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  const results = await Promise.all(entries.map(async entry => {
    const url = new URL(entry.name + (entry.isDirectory() ? "/" : ""), directory);
    return entry.isDirectory() ? sourceFiles(url) : entry.name.endsWith(".cs") ? [url] : [];
  }));
  return results.flat();
}

function executeBody(source: string): string {
  const start = source.indexOf("ExecuteAsync");
  return start < 0 ? "" : source.slice(start);
}

test("core A mutation Tools own a commit-effect boundary", async () => {
  const files = (await Promise.all(domains.map(domain => sourceFiles(new URL(`${domain}/`, toolsRoot))))).flat();
  const sources = await Promise.all(files.map(async file => ({ file, source: await readFile(file, "utf8") })));
  const mutations = sources.filter(({ source }) => /DestructiveHint\s*=\s*true|SideEffects\s*=\s*new\[\]\s*\{\s*"[^"]+"/.test(source));
  const readOnly = sources.filter(({ source }) => /DestructiveHint\s*=\s*false/.test(source) && !/SideEffects\s*=\s*new\[\]\s*\{\s*"[^"]+"/.test(source));
  const sharedBuildScene = await readFile(new URL("build/BuildSceneStateTool.cs", toolsRoot), "utf8");

  assert.ok(mutations.length >= 80, "the ten core domains must enumerate every mutation Tool");
  for (const { file, source } of mutations) {
    const body = executeBody(source);
    const usesSharedBuildScene = /BuildSceneStateTool\.Execute/.test(body);
    const usesCompletionProbe = /ctx\.RunCompletionProbe\(/.test(body);
    assert.ok(/ctx\.CommitEffect\(\)/.test(body) || usesSharedBuildScene || usesCompletionProbe, `${file.pathname} has no commit-effect boundary`);
    if (/SupportsDryRun\s*=\s*true/.test(source) && !usesSharedBuildScene && !usesCompletionProbe) {
      const dryRun = body.indexOf("ctx.DryRun");
      const commit = body.indexOf("ctx.CommitEffect()");
      assert.ok(dryRun < 0 || commit > dryRun, `${file.pathname} commits before its dry-run guard`);
    }
  }
  assert.match(sharedBuildScene, /if \(!ctx\.DryRun && changed\)\s*\{\s*ctx\.CommitEffect\(\)/);
  for (const { file, source } of readOnly) {
    assert.doesNotMatch(executeBody(source), /ctx\.CommitEffect\(\)/, `${file.pathname} is read-only but commits an effect`);
  }
});
