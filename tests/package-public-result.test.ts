import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import test from "node:test";
import { projectRelativePath, publicPackageEffects } from "../src/tools/package/public-result.js";

test("Package public paths are project-relative and external sources use the request alias", () => {
  const project = resolve("D:/package-public-project");
  const source = resolve("D:/package-sources/com.acme.tool");
  assert.equal(projectRelativePath(project, join(project, "Packages", "com.acme.tool"), "targetPath"), "Packages/com.acme.tool");
  assert.throws(() => projectRelativePath(project, project, "projectPath"), /resolves to its root/);
  assert.throws(() => projectRelativePath(project, join(project, "..", "outside"), "targetPath"), /outside the selected project/);

  assert.deepEqual(publicPackageEffects(project, [{
    type: "package.deploy.copy",
    from: source,
    to: join(project, "Packages", "com.acme.tool"),
  }], new Map([[source, "./Packages/my-package"]])), [{
    type: "package.deploy.copy",
    from: "./Packages/my-package",
    to: "Packages/com.acme.tool",
  }]);
});

test("Package roots use production SessionSupervisor transport and DTO-owned path projection", async () => {
  const liveResolve = await readFile("src/tools/package/live-resolve.ts", "utf8");
  const readiness = await readFile("src/tools/editor/readiness.ts", "utf8");
  const deploy = await readFile("src/tools/package/deploy.ts", "utf8");
  const restore = await readFile("src/tools/package/restore.ts", "utf8");
  const coordinator = await readFile("unitypkg/Editor/Infrastructure/UnityCliPackageResolveState.cs", "utf8");

  assert.match(liveResolve, /context\.projectPath = state\.identity\.canonicalProjectPath/);
  assert.doesNotMatch(liveResolve, /if \(!context\.bridgeExecute\).*live bridge executor/);
  assert.match(liveResolve, /executeBridgePayload\([\s\S]*bridgeExecute: context\.bridgeExecute/);
  assert.match(liveResolve, /waitForEditorTransitionEnd\(context, childRequestId\(context\.requestId, "package-state"\), deadline\)/);
  assert.match(readiness, /readEditorState\([\s\S]*bridgeExecute: context\.bridgeExecute/);
  assert.match(coordinator, /PublicFileProof\(proof\)[\s\S]*PublicProjectPath/);
  assert.match(coordinator, /VerifyFileProof[\s\S]*HashDirectory\(target\)/);
  assert.match(coordinator, /resolvedPath[\s\S]*SamePath\(Text\(predicate, "resolvedPath"\), Text\(package, "resolvedPath"\)\)/);
  assert.doesNotMatch(coordinator, /SameJson\(seedFiles, proof\)/);
  assert.match(deploy, /predicates: \[\{ name: plan\.packageName, version: plan\.packageVersion, present: true, resolvedPath: plan\.targetPath \}\]/);
  const restorePredicate = restore.slice(restore.indexOf("const predicate ="), restore.indexOf("return {", restore.indexOf("const predicate =")));
  assert.match(restorePredicate, /present: true, resolvedPath: plan\.targetPath/);
  assert.doesNotMatch(`${deploy}\n${restorePredicate}`, /packageTreeHash/);

  for (const source of [deploy, restore]) {
    assert.match(source, /bindLivePackageProject\(context, "(?:deploy|restore)"\)/);
    const result = /export interface Package(?:Deploy|Restore)Result \{([\s\S]*?)\n\}/.exec(source)?.[1];
    assert.ok(result);
    assert.doesNotMatch(result, /\bprojectPath\b/);
    assert.match(source, /publicResultSeed: public(?:Deploy|Restore)Result/);
    assert.doesNotMatch(source, /publicResultSeed:\s*\{[^\n]*\.\.\.(?:deployed|restored)/);
  }
});
