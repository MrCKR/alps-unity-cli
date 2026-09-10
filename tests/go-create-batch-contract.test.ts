import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { bridgeCommandRegistry } from "../src/bridge/index.js";

const toolPath = "unitypkg/Editor/Tools/game-object/create.cs";
const bridgePath = "src/bridge/domains/game-object.ts";

test("game-object.create exposes one bounded typed batch on the canonical Tool", async () => {
  const source = await readFile(toolPath, "utf8");
  assert.match(source, /private const int MaxBatchCount = 1000/);
  assert.match(source, /public int\? Count \{ get; set; \}/);
  assert.match(source, /public List<List<double>> Positions \{ get; set; \}/);
  assert.match(source, /public List<List<double>> Rotations \{ get; set; \}/);
  assert.match(source, /public List<List<double>> Scales \{ get; set; \}/);
  assert.match(source, /public List<GameObjectDetail> GameObjects \{ get; set; \}/);
  assert.match(source, /Name = "game-object\.create"/);
  assert.doesNotMatch(source, /Name = "go\.create-batch"/);
});

test("game-object.create prevalidates the whole batch before one effect barrier", async () => {
  const source = await readFile(toolPath, "utf8");
  assert.equal((source.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1);
  assert.ok(source.indexOf("var plan = Prepare") < source.indexOf("ctx.CommitEffect()"));
  assert.match(source, /private static CreatePlan Prepare[\s\S]*?ValidateVectorArray\(request\.Positions/);
  assert.match(source, /values\.Count != count/);
  assert.match(source, /values\[index\] == null \|\| values\[index\]\.Count != 3/);
  assert.match(source, /E_GO_BATCH_TRANSFORM_CONFLICT/);
  assert.match(source, /ResolveUniqueTarget\(request\.Parent/);
  assert.match(source, /ParsePrimitiveType\(request\.Primitive\)/);
});

test("game-object.create keeps one Undo group and proves the ordered fresh object set", async () => {
  const source = await readFile(toolPath, "utf8");
  assert.match(source, /UnityCliSceneMutation\.BeginUndoGroup\("game-object\.create"\)/);
  assert.match(source, /finally[\s\S]*?UnityCliSceneMutation\.EndUndoGroup\(undoGroup\)/);
  assert.doesNotMatch(source, /Undo\./);
  assert.match(source, /var instanceIds = new List<int>\(plan\.Count\)/);
  assert.match(source, /EditorUtility\.InstanceIDToObject\(instanceIds\[index\]\) as GameObject/);
  assert.match(source, /new HashSet<int>\(\)/);
  assert.match(source, /observed\.transform\.parent != plan\.Parent/);
  assert.match(source, /TransformMatches\(observed\.transform, expected\)/);
  assert.match(source, /GameObjects = observed/);
  assert.match(source, /new PlannedEffect \{ Type = "game-object.create", Count = observed\.Count/);
});

test("game-object.create bridge forwards the structured batch without flattening vectors", async () => {
  const source = await readFile(bridgePath, "utf8");
  assert.match(source, /count: numberFlag\(flags, "count"\) \?\? numberValue\(structured\.count\)/);
  assert.match(source, /positions: structured\.positions/);
  assert.match(source, /rotations: structured\.rotations/);
  assert.match(source, /scales: structured\.scales/);
  assert.match(source, /x: floatFlag\(flags, "x"\) \?\? numberValue\(structured\.x\) \?\? numberValue\(position\?\.x\)/);
  assert.match(source, /rotationY: floatFlag\(flags, "rotation-y"\) \?\? numberValue\(structured\.rotationY\) \?\? numberValue\(rotation\?\.y\)/);
  assert.match(source, /scaleZ: floatFlag\(flags, "scale-z"\) \?\? numberValue\(structured\.scaleZ\) \?\? numberValue\(scale\?\.z\)/);

  const spec = bridgeCommandRegistry.lookup("game-object", "create");
  assert.ok(spec);
  const input = {
    name: "Probe", count: 2, positions: [[0, 0, 0], [1, 0, 0]],
    x: 4, rotationY: 90, scaleZ: 2
  };
  const built = await spec.buildArgs({ command: "game-object.create", flags: { __structuredInput: JSON.stringify(input) } });
  assert.deepEqual(built, input);
});
