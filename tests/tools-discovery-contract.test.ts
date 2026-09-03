import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";
import { execute } from "../src/cli.js";
import { findToolByName, filterToolsByGroup } from "../src/tools/metadata/index.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";

const REMOVED_IDENTITIES = ["tools.describe", "tools.group", "tools.group-list", "tools.search", "extension.status"];

test("discovery controls are not public Tool identities", () => {
  for (const name of REMOVED_IDENTITIES) assert.equal(findToolByName(name), undefined, name);
  assert.deepEqual(filterToolsByGroup("tools"), []);
});

test("frozen top-level tools controls remain usable while tools run rejects their former identities", async () => {
  const runtime = {
    projectTools: [makeLiveTool({ name: "fixture.read", group: "fixture", summary: "Read one fixture." })],
    runtimeTools: [],
  };
  const groups = await execute(["tools", "group"], runtime as never);
  const listed = await execute(["tools", "group", "list", "fixture"], runtime as never);
  const described = await execute(["tools", "describe", "fixture.read"], runtime as never);
  assert.equal(groups.ok, true, JSON.stringify(groups));
  assert.equal(listed.ok, true, JSON.stringify(listed));
  assert.equal(described.ok, true, JSON.stringify(described));

  for (const name of REMOVED_IDENTITIES.slice(0, 4)) {
    const result = await execute(["tools", "run", name, "--input", "{}"], runtime as never);
    assert.equal(result.ok, false, name);
    if (!result.ok) assert.equal(result.error.code, "E_TOOL_NOT_FOUND_FOR_TARGET", name);
  }
});

test("canonical and generated source has no removed Tool declaration or Bridge status route", async () => {
  const sourceFiles = [
    ...await recursiveFiles("src", ".ts"),
    ...await recursiveFiles(join("unitypkg", "Editor"), ".cs"),
  ];
  const source = (await Promise.all(sourceFiles.map((path) => readFile(path, "utf8")))).join("\n");
  for (const name of REMOVED_IDENTITIES) {
    assert.doesNotMatch(source, new RegExp(`(?:name\\s*:\\s*|Name\\s*=\\s*|toolName\\s*:\\s*)["']${name.replaceAll(".", "\\.")}["']`), name);
  }
  assert.doesNotMatch(source, /request\.Command\s*==\s*"extension\.status"/);
});

async function recursiveFiles(root: string, suffix: string): Promise<string[]> {
  const result: string[] = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const path = join(root, entry.name);
    if (entry.isDirectory()) result.push(...await recursiveFiles(path, suffix));
    else if (entry.isFile() && entry.name.endsWith(suffix)) result.push(path);
  }
  return result;
}
