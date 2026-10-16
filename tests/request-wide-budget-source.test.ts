import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
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
  assert.match(budget, /TryReserveSerializedUtf8Bytes/);
  assert.match(budget, /EstimatedSerializedUtf8Bytes/);
  assert.match(budget, /ActualSerializedUtf8Bytes/);
  assert.doesNotMatch(budget, /Thread\.Sleep/);
});

test("console is bounded non-resumable while scene and GameObject discovery page raw positions", async () => {
  const [consoleGet, editorSupport, hierarchy, goFind] = await Promise.all([
    source("Tools/debug/console/read.cs"), source("Tools/editor/EditorToolSupport.cs"),
    source("Tools/scene/hierarchy.cs"), source("Tools/game-object/find.cs"),
  ]);
  assert.match(editorSupport, /rawIndex/);
  assert.match(editorSupport, /intentionally non-resumable/);
  assert.doesNotMatch(editorSupport + consoleGet, /CreateConsoleCursor|NextCursor/);
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
  assert.match(hierarchy, /GetSampleChildrenCountRecursive/);
  assert.doesNotMatch(hierarchy, /GetSampleChildAt/);
  assert.doesNotMatch(events, /GetFrameEvents|CopyTo\(/);
  assert.match(events, /pageInfo\.nextCursor|PageInfo|NextCursor/);
  assert.match(events, /UnityCliContinuationToken\.Create\("profiler\.frame-debugger\.events"/);
  assert.match(events, /UnityCliContinuationToken\.TryParse/);
  assert.match(events, /index\.ToString\(CultureInfo\.InvariantCulture\)/);
  assert.match(snapshots, /Library.*UnityCli.*memory-snapshots/);
  assert.match(snapshots, /RetainedSnapshotCount = 100/);
  assert.match(snapshots, /RetainedSnapshotBytes = 8L \* 1024L \* 1024L \* 1024L/);
  assert.match(snapshots, /PublicationLock/);
  assert.ok(snapshots.indexOf("ValidateSnapshotLength(length, requestedMaxBytes)") < snapshots.indexOf("File.Move(source, finalPath)"));
  assert.doesNotMatch(take, /MemorySnapshotTerminal/);
  assert.match(take, /ctx\.Wait\.ForCallback/);
  assert.match(take, /complete\(\(\) =>/);
  assert.match(list, /NextCursor/);
  assert.match(list, /index == offset && index < files\.Count/);
  assert.match(list, /UnityCliRequestBudgetExceededException/);
  assert.doesNotMatch(take + list, /temporaryCachePath|MemoryCaptures|Directory\.GetFiles/);
});

test("reflection pages bind assembly generation and budgeted member definitions", async () => {
  const [reflection, typeTool, methodTool] = await Promise.all([
    source("Infrastructure/UnityCliReflection.cs"),
    source("Tools/debug/type/schema.cs"), source("Tools/debug/method/find.cs"),
  ]);
  assert.match(reflection, /ResolvedTypeCache/);
  assert.match(reflection, /UnityCliReadGeneration\.Reflection/);
  assert.match(reflection, /raw ordinal/);
  assert.doesNotMatch(reflection, /type\.GetMethods\(bindings\)[\s\S]{0,240}\.Where/);
  assert.match(typeTool, /MaxMembers/);
  assert.match(typeTool, /MaxDefinitions/);
  assert.match(methodTool, /NextCursor/);
  assert.match(reflection, /index == pageStartIndex && index < methods\.Length/);
  assert.match(reflection, /UnityCliRequestBudgetExceededException\(budget\.ExhaustedDimension \?\? "scannedRecords"\)/);
  assert.match(typeTool + methodTool, /UnityCliReadBudgetInput/);
});

test("continuations bind listener generation and callback waits use atomic publication ownership", async () => {
  const [generation, waiter] = await Promise.all([
    source("Infrastructure/UnityCliReadGeneration.cs"),
    source("Infrastructure/UnityCliWaiter.cs"),
  ]);
  assert.match(generation, /ListenerId/);
  assert.match(generation, /CurrentListenerGeneration/);
  assert.match(waiter, /Action onAbandoned/);
  assert.match(waiter, /UnityCliCallbackPublication<T>/);
  assert.match(waiter, /TryAbandon/);
  assert.match(waiter, /NotifyAbandoned/);
});

test("screenshot artifact IO is chunked and temporary allocations are guarded", async () => {
  const [support, camera, gameView, sceneView] = await Promise.all([
    source("Tools/screenshot/ScreenshotToolSupport.cs"),
    source("Tools/camera/CameraToolSupport.cs"),
    source("Tools/screenshot/game-view.cs"),
    source("Tools/screenshot/scene-view.cs"),
  ]);
  assert.match(support, /chunkSize = 64 \* 1024/);
  assert.match(support, /TransformBlock/);
  assert.match(support, /DeleteArtifactNoThrow/);
  for (const value of [camera, gameView, sceneView]) {
    assert.match(value, /RenderTexture [a-zA-Z]+ = null/);
    assert.match(value, /finally/);
    assert.match(value, /Release\(\)/);
    assert.match(value, /DestroyImmediate/);
  }
});

test("EditMode regressions call production paging serialization ownership and handler cores", async () => {
  const tests = await readFile(new URL("../unitypkg/Tests/Editor/UnityCliRequestBudgetTests.cs", import.meta.url), "utf8");
  for (const productionCall of [
    "ObjectRefResolver.BoundedSceneRoots",
    "SceneGameObjectComponentsTool",
    "ObjectSerializer.Serialize",
    "TypeSchemaGenerator.Generate",
    "EditorToolSupport.ReadConsolePage",
    "ReflectionInvoker.PrepareCall",
    "SerializeTerminalSuccess",
    "ProfilerMemorySnapshotSupport.ApplyRetention",
    "ProfilerMemorySnapshotSupport.CleanupOrphanPass",
    "PendingCapturePublication",
    "CreateTextureWithOwnership",
    "InitializeOwnedView",
  ]) assert.ok(tests.includes(productionCall), `missing production regression call: ${productionCall}`);
  assert.doesNotMatch(tests, /MemorySnapshotTerminal|Thread\.Sleep/);
});

test("memory and reflection integration fixtures exercise exact terminal and resource matrices", async () => {
  const tests = await readFile(new URL("../unitypkg/Tests/Editor/UnityCliMemoryReflectionIntegrationTests.cs", import.meta.url), "utf8");
  for (const productionCall of [
    "ProfilerMemorySnapshotTakeTool",
    "ProfilerMemorySnapshotListTool",
    "TypeSchemaGenerator.Generate",
    "ReflectionInvoker.FindMethods",
    "UnityCliReadGeneration.InvalidateReflection",
    "AssertInventory",
  ]) assert.ok(tests.includes(productionCall), `missing integration regression call: ${productionCall}`);
  for (const outcome of ["available", "missing", "unsupported", "write-failure", "timeout", "cancel", "reload"]) {
    assert.ok(tests.includes(`\"${outcome}\"`), `missing memory terminal fixture: ${outcome}`);
  }
  assert.match(tests, /for \(var index = 0; index < 105; index\+\+\)/);
  assert.match(tests, /Assert\.That\(names\.Count, Is\.EqualTo\(100\)\)/);
  assert.match(tests, /File\.WriteAllBytes\(FakeMemoryProfiler\.PendingPath/);
  assert.doesNotMatch(tests, /Thread\.Sleep/);
});

test("command projections preserve opaque cursors and nested budgets", async () => {
  const roots = "src/bridge/domains";
  const files = ["debug.ts", "game-object.ts", "scene.ts", "profiler.ts", "screenshot.ts"];
  const text = (await Promise.all(files.map((file) => readFile(`${roots}/${file}`, "utf8")))).join("\n");
  assert.ok((text.match(/cursor: stringFlag\(flags, "cursor"\)/g) ?? []).length >= 5);
  assert.match(text, /budget: structured\.budget/);
  assert.match(text, /limits: structured\.limits/);
});

test("obsolete aggregate readers are absent from canonical source and non-generated routes", async () => {
  await assert.rejects(access(new URL("Tools/game-object/inspect.cs", editor)));
  await assert.rejects(access(new URL("Tools/scene/data.cs", editor)));
  const [goRoute, sceneRoute] = await Promise.all([
    readFile("src/bridge/domains/game-object.ts", "utf8"),
    readFile("src/bridge/domains/scene.ts", "utf8"),
  ]);
  assert.doesNotMatch(goRoute, /go\.inspect/);
  assert.doesNotMatch(sceneRoute, /scene\.data/);
});
