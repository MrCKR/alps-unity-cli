import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const editor = new URL("../unitypkg/Editor/", import.meta.url);
const shared = new URL("../unitypkg/Shared/", import.meta.url);

async function source(path: string): Promise<string> {
  return readFile(new URL(path, editor), "utf8");
}

test("request budget owns all six counters and injectable monotonic time", async () => {
  const budget = await readFile(new URL("UnityCliRequestBudget.cs", shared), "utf8");
  for (const dimension of ["scannedRecords", "nodes", "getters", "reflectionCalls", "mainThreadMilliseconds", "serializedUtf8Bytes"]) {
    assert.match(budget, new RegExp(dimension, "i"));
  }
  assert.match(budget, /IUnityCliMonotonicClock/);
  assert.match(budget, /CancellationToken/);
  assert.doesNotMatch(budget, /Thread\.Sleep/);
});

test("console scene and GameObject discovery page raw positions without full result materialization", async () => {
  const [consoleGet, editorSupport, hierarchy, goFind] = await Promise.all([
    source("Tools/console/get.cs"), source("Tools/editor/EditorToolSupport.cs"),
    source("Tools/scene/hierarchy.cs"), source("Tools/go/find.cs"),
  ]);
  assert.match(editorSupport, /rawIndex/);
  assert.match(consoleGet, /NextCursor/);
  assert.doesNotMatch(consoleGet, /Skip\(|Take\(|ToList\(/);
  assert.doesNotMatch(hierarchy, /allItems|CollectHierarchy|Skip\(|Take\(|OrderBy/);
  assert.doesNotMatch(goFind, /FindObjectsOfType|GetComponentsInChildren|Concat\(|Skip\(|Take\(|ToList\(/);
  assert.match(hierarchy + goFind, /FindGameObjectByExactPath/);
});

test("serializer judges cycle and depth before collections or member expansion", async () => {
  const serializer = await source("Infrastructure/UnityCliObjectSerializer.cs");
  const depth = serializer.indexOf("depth >= options.MaxDepth");
  const cycle = serializer.indexOf("!active.Add(obj)");
  const list = serializer.indexOf("obj is IList list");
  const members = serializer.indexOf("ExpandMembers(obj, type");
  assert.ok(depth >= 0 && cycle > depth && list > cycle && members > cycle);
  assert.match(serializer, /enumerator\.MoveNext\(\)/);
  assert.doesNotMatch(serializer, /enumerable[^\n]*\.ToList\(/);
});

test("capture planning precedes effect and surround does not retain a tile list", async () => {
  const [support, orbit, surround] = await Promise.all([
    source("Tools/screenshot/ScreenshotToolSupport.cs"),
    source("Tools/screenshot/orbit.cs"),
    source("Tools/screenshot/surround.cs"),
  ]);
  for (const value of ["MaxImageDimension", "MaxPixelsPerImage", "MaxCaptureCount", "DefaultMaxTotalPixels", "DefaultMaxTemporaryBytes", "DefaultMaxArtifactBytes"]) {
    assert.match(support, new RegExp(value));
  }
  assert.ok(orbit.indexOf("PrepareCaptureBudget") < orbit.indexOf("ctx.CommitEffect()"));
  assert.ok(surround.indexOf("PrepareCaptureBudget") < surround.indexOf("ctx.CommitEffect()"));
  assert.doesNotMatch(surround, /List<Texture2D>/);
  assert.match(surround, /DestroyImmediate\(tile\)/);
});

test("profiler and snapshot paths remain raw-paged and governed", async () => {
  const [hierarchy, events, snapshots, take, list] = await Promise.all([
    source("Tools/profiler/hierarchy.cs"), source("Tools/profiler/frame-debugger/events.cs"),
    source("Tools/profiler/ProfilerMemorySnapshotSupport.cs"),
    source("Tools/profiler/memory-snapshot/take.cs"), source("Tools/profiler/memory-snapshot/list.cs"),
  ]);
  assert.match(hierarchy, /GetSampleChildAt/);
  assert.doesNotMatch(events, /GetFrameEvents|CopyTo\(/);
  assert.match(snapshots, /Library.*UnityCli.*memory-snapshots/);
  assert.match(snapshots, /RetainedSnapshotCount = 100/);
  assert.match(snapshots, /PublicationLock/);
  assert.match(take, /MemorySnapshotTerminal/);
  assert.match(list, /NextCursor/);
  assert.doesNotMatch(take + list, /temporaryCachePath|MemoryCaptures|Directory\.GetFiles/);
});

test("reflection pages bind assembly generation and budgeted member definitions", async () => {
  const [reflection, typeTool, methodTool] = await Promise.all([
    source("Infrastructure/UnityCliReflection.cs"),
    source("Tools/reflection/type/schema.cs"), source("Tools/reflection/method/find.cs"),
  ]);
  assert.match(reflection, /ResolvedTypeCache/);
  assert.match(reflection, /UnityCliReadGeneration\.Reflection/);
  assert.match(typeTool, /MaxMembers/);
  assert.match(typeTool, /MaxDefinitions/);
  assert.match(methodTool, /NextCursor/);
  assert.match(typeTool + methodTool, /UnityCliReadBudgetInput/);
});
