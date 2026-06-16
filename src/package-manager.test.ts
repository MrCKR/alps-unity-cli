import assert from "node:assert/strict";
import test from "node:test";
import { fileDependencySpec, resolveFileDependency } from "./package-manager.js";

test("file dependency spec uses absolute Windows path for cross-drive package installs", () => {
  const projectPath = String.raw`D:\workspace\alps-unity-cli\Dev\UnityProject`;
  const packagePath = String.raw`C:\workspace\alps-unity-cli\unitypkg`;

  const spec = fileDependencySpec(projectPath, packagePath);

  assert.equal(spec, "file:C:/workspace/alps-unity-cli/unitypkg");
  assert.equal(resolveFileDependency(projectPath, spec), String.raw`C:\workspace\alps-unity-cli\unitypkg`);
});
