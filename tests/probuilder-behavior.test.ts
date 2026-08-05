/**
 * Real-behavior tests for ProBuilder C# handlers.
 *
 * These tests validate that the ProBuilder C# tool handlers use the REAL
 * ProBuilder API via reflection, rather than approximating the operation
 * with cube fallbacks, full-refresh stubs, or fake return counts.
 *
 * Each tool is implemented in a self-contained IUnityCliTool<,> file.
 * These source assertions verify the required ProBuilder API calls.
 *
 * Verified behaviors:
 * - create-poly-shape uses CreateShapeFromPolygon
 * - extrude-edges actually selects edges and extrudes via ExtrudeElements
 * - bridge-edges calls AppendElements.Bridge (not just RefreshMesh)
 * - connect-elements calls typed ConnectElements.Connect with a frozen plan
 * - bevel-edges calls typed Bevel.BevelEdges with a frozen plan
 * - duplicate-and-flip calls typed AppendElements.DuplicateAndFlip with a frozen plan
 * - create-polygon calls AppendElements.CreatePolygon
 * - append-vertices-to-edge calls AppendElements.AppendVerticesToEdge
 * - insert-vertex supports edge pairs and face index (not null edge)
 * - move-vertices offsets positions (not set-all-to-same)
 * - merge/weld/split-vertices use VertexEditing API with IList<int>
 * - set-face-color uses SetFaceColor (not the non-existent ProBuilder.Color)
 * - set-face-uvs modifies Face.uv fields (not just RefreshMesh)
 * - select-faces supports direction-based selection (not just flood)
 * - convert-to-probuilder uses MeshImporter.Import
 * - combine-meshes / merge-objects use CombineMeshes.Combine
 * - ProBuilderReflection exposes the new reflection helpers
 */

import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const __dirname = dirname(fileURLToPath(import.meta.url));
const PB_DIR = join(__dirname, "..", "extensions", "probuilder", "unitypkg", "Editor", "ProBuilder");

function readPb(name: string): string {
  return readFileSync(join(PB_DIR, name), "utf8");
}

const REFLECTION_SRC = readPb("ProBuilderReflection.cs");
const INPUT_SRC = readPb("ProBuilderInput.cs");
const ASMDEF = JSON.parse(readFileSync(join(PB_DIR, "..", "..", "UnityCli.Extensions.ProBuilder.Editor.asmdef"), "utf8"));

// 每个变量读取对应的单工具文件。
const CREATE_SHAPE_SRC = readPb("create-shape.cs");
const CREATE_POLY_SHAPE_SRC = readPb("create-poly-shape.cs");
const EXTRUDE_EDGES_SRC = readPb("extrude-edges.cs");
const BRIDGE_EDGES_SRC = readPb("bridge-edges.cs");
const CONNECT_ELEMENTS_SRC = readPb("connect-elements.cs");
const BEVEL_EDGES_SRC = readPb("bevel-edges.cs");
const DUPLICATE_AND_FLIP_SRC = readPb("duplicate-and-flip.cs");
const COMBINE_MESHES_SRC = readPb("combine-meshes.cs");
const MERGE_OBJECTS_SRC = readPb("merge-objects.cs");
const CREATE_POLYGON_SRC = readPb("create-polygon.cs");
const APPEND_VERTICES_SRC = readPb("append-vertices-to-edge.cs");
const INSERT_VERTEX_SRC = readPb("insert-vertex.cs");
const MOVE_VERTICES_SRC = readPb("move-vertices.cs");
const MERGE_VERTICES_SRC = readPb("merge-vertices.cs");
const WELD_VERTICES_SRC = readPb("weld-vertices.cs");
const SPLIT_VERTICES_SRC = readPb("split-vertices.cs");
const SET_FACE_COLOR_SRC = readPb("set-face-color.cs");
const SET_FACE_UVS_SRC = readPb("set-face-uvs.cs");
const SELECT_FACES_SRC = readPb("select-faces.cs");
const CONVERT_SRC = readPb("convert-to-probuilder.cs");
const FREEZE_TRANSFORM_SRC = readPb("freeze-transform.cs");
const MESH_INFO_SRC = readPb("mesh-info.cs");
const REPAIR_MESH_SRC = readPb("repair-mesh.cs");

test("ProBuilder production C# contains no compatibility or semantic degradation path", () => {
  const sources = readdirSync(PB_DIR)
    .filter((name) => name.endsWith(".cs"))
    .map((name) => ({ name, source: readPb(name) }));
  const forbidden = [
    /\bfallback\b/i,
    /回退|近似/,
    /\blegacy(?:Method|Fallback|Adapter|Path|Implementation)\b/i,
    /Mesh\.CombineMeshes|CreatePrimitive|InsertVertexOnEdge\([^\r\n]*null/,
    /sharedMaterials\s*=(?!=)/
  ];

  for (const { name, source } of sources) {
    for (const pattern of forbidden) {
      assert.doesNotMatch(source, pattern, `${name}: must fail closed on the current ProBuilder API`);
    }
  }
});

// ─── Reflection helper coverage ────────────────────────────────────────

test("ProBuilder extension fails closed outside the exact 5.2.4 package profile", () => {
  assert.match(REFLECTION_SRC, /SupportedPackageVersion = "5\.2\.4"/);
  assert.match(REFLECTION_SRC, /#if UNITY_CLI_PROBUILDER_5_2_4[\s\S]*return SupportedPackageVersion;[\s\S]*#else[\s\S]*return null;/);
  assert.doesNotMatch(REFLECTION_SRC, /PackageInfo|FindForAssembly|installedPackageVersion|PackageVersionGate/);
  assert.match(REFLECTION_SRC, /ProBuilderMeshType\.Value != null && string\.Equals\(InstalledPackageVersion, SupportedPackageVersion, StringComparison\.Ordinal\)/);
  assert.deepEqual(ASMDEF.versionDefines, [{
    name: "com.unity.probuilder",
    expression: "[5.2.4,5.2.5)",
    define: "UNITY_CLI_PROBUILDER_5_2_4"
  }]);
});

test("ProBuilderReflection resolves all MeshOperations types used by reference project", () => {
  // 这些类型是实际 ProBuilder 操作的基础，必须通过可重试 resolver 调用真实 API。
  const requiredTypes = [
    "ExtrudeElementsType",
    "DeleteElementsType",
    "AppendElementsType",
    "CombineMeshesType",
    "VertexEditingType",
    "MeshImporterType",
    "MeshImportSettingsType",
    "ElementSelectionType",
    "PivotLocationType"
  ];

  for (const typeName of requiredTypes) {
    // optional assembly 晚加载时不得永久缓存 null。
    assert.ok(
      REFLECTION_SRC.includes(`RetryingType ${typeName} =`),
      `ProBuilderReflection must declare retryable ${typeName} field`
    );
  }
});

test("ProBuilderReflection exposes edge resolution helpers", () => {
  assert.ok(REFLECTION_SRC.includes("CreateEdge(int a, int b)"), "must expose CreateEdge");
  assert.ok(REFLECTION_SRC.includes("GetEdgeVertexA(object edge)"), "must expose GetEdgeVertexA");
  assert.ok(REFLECTION_SRC.includes("GetEdgeVertexB(object edge)"), "must expose GetEdgeVertexB");
  assert.ok(REFLECTION_SRC.includes("CollectUniqueEdges(object mesh)"), "must expose CollectUniqueEdges");
  assert.ok(REFLECTION_SRC.includes("ResolveEdges(object mesh, int[] edgeIndices, ProBuilderInput.EdgePair[] edgePairs)"), "must expose ResolveEdges");
  assert.ok(REFLECTION_SRC.includes("ToTypedFaceList(Array faces)"), "must expose ToTypedFaceList");
  assert.ok(REFLECTION_SRC.includes("ToTypedEdgeList(Array edges)"), "must expose ToTypedEdgeList");
});

test("ProBuilderReflection exposes real ProBuilder API call wrappers", () => {
  // These wrappers call the real ProBuilder API methods, not stubs.
  const requiredWrappers = [
    "TryExtrudeFrozenEdges",
    "TryBridgeFrozenEdges",
    "CreatePolygon",
    "AppendVerticesToEdge",
    "InsertVertexOnEdgeByPair",
    "InsertVertexInFace",
    "MergeVerticesWithCollapse",
    "WeldVerticesWithRadius",
    "SplitVerticesWithList",
    "MoveVerticesByOffset",
    "RemoveDegenerateTriangles",
    "RemoveUnusedVertices",
    "CountDegenerateFaces",
    "CountUnusedVertices",
    "ConvertToProBuilderMesh",
    "CombineMeshesViaReflection",
    "DeleteFaces",
    "SelectFaces",
    "SetFaceColor",
    "SetFaceMaterial",
    "ComputeFaceNormal",
    "GetPositions"
  ];

  for (const wrapper of requiredWrappers) {
    assert.ok(
      REFLECTION_SRC.includes(`public static`) && REFLECTION_SRC.includes(wrapper),
      `ProBuilderReflection must expose ${wrapper} wrapper`
    );
  }
  for (const removed of ["MergeFaces", "FaceUvMatches", "HasPolygonNormalOrientation", "CaptureMeshState", "HasObservedMutation", "CaptureObjectState", "HasObservedObjectMutation"]) {
    assert.doesNotMatch(REFLECTION_SRC, new RegExp(removed), `${removed} is an obsolete weak or unused wrapper`);
  }
});

test("ProBuilderReflection edge helpers use shared vertex lookup for deduplication", () => {
  // Reference project builds a shared vertex lookup so edges on different faces
  // with the same spatial position are correctly deduplicated.
  assert.ok(
    REFLECTION_SRC.includes("BuildSharedVertexLookup"),
    "must build shared vertex lookup for correct edge deduplication"
  );
  assert.ok(
    REFLECTION_SRC.includes("sharedVertices"),
    "must read sharedVertices property from ProBuilderMesh"
  );
});

// ─── Input parsing helpers ─────────────────────────────────────────────

test("ProBuilder edge and point inputs use typed request DTOs", () => {
  assert.match(INPUT_SRC, /struct EdgePair[\s\S]*public int a;[\s\S]*public int b;/);
  assert.match(EXTRUDE_EDGES_SRC, /public List<EdgeRef> Edges \{ get; set; \}/);
  assert.match(EXTRUDE_EDGES_SRC, /request\.Edges\?\.Select\(e => new ProBuilderInput\.EdgePair \{ a = ProBuilderOperationProof\.CheckedInt\(e\.A, "edges\.a"\), b = ProBuilderOperationProof\.CheckedInt\(e\.B, "edges\.b"\) \}\)/);
  assert.match(CREATE_POLY_SHAPE_SRC, /\[UnityCliRequired\] public List<Point3D> Points \{ get; set; \}/);
  assert.doesNotMatch(INPUT_SRC, /EdgePairArrayArg|EdgeArg|Vector3ListArg|RawJsonArg/);
});

// ─── create-shape: descriptor enum must match runtime validation ───────

test("create-shape rejects unsupported shapeType without semantic degradation", () => {
  // 公开 descriptor 暴露 shapeType enum，Unity 侧必须先拒绝非 enum 值。
  assert.ok(CREATE_SHAPE_SRC.includes("IsSupportedShape"), "create-shape must validate shapeType against the public enum");
  assert.ok(CREATE_SHAPE_SRC.includes("ProBuilderErrors.SchemaInvalid"), "unsupported shapeType must return E_SCHEMA_INVALID");
  assert.ok(
    CREATE_SHAPE_SRC.indexOf("IsSupportedShape") < CREATE_SHAPE_SRC.indexOf("ProBuilderReflection.CreateShape"),
    "create-shape must validate shapeType before calling ProBuilder API"
  );
  assert.doesNotMatch(CREATE_SHAPE_SRC, /CreatePrimitive|CreateUnityPrimitive|NormalizeShape/);
  assert.match(CREATE_SHAPE_SRC, /ProBuilder 5\.2\.4 ShapeGenerator\.CreateShape failed/);
});

// ─── create-poly-shape: must use CreateShapeFromPolygon, not cube ──────

test("create-poly-shape uses typed 5.2.4 polygon creation with atomic business readback", () => {
  assert.match(CREATE_POLY_SHAPE_SRC, /AppendElements\.CreateShapeFromPolygon\(mesh, points, height, flipNormals\)/);
  assert.match(CREATE_POLY_SHAPE_SRC, /ProBuilderAtomicReadback\.PolyShapeMatchesRequest\(fresh, freshMesh, points, height, flipNormals\)/);
  assert.match(CREATE_POLY_SHAPE_SRC, /request\.Points == null \|\| request\.Points\.Any\(point => point == null\)/);
  assert.match(CREATE_POLY_SHAPE_SRC, /request\.ExtrudeHeight \?\? 1d/);
  assert.doesNotMatch(CREATE_POLY_SHAPE_SRC, /ProBuilderComplexTopologyCompletionProof|CreatePrimitive|ProBuilderReflection\.CreateShapeFromPolygon/);
});

// ─── extrude-edges: must select edges and extrude via real API ─────────

test("extrude-edges selects edges and calls ExtrudeElements.Extrude", () => {
  // OLD: counted faces*3/2 as fake edge count, only called RefreshMesh.
  // NEW: must resolve edges from input and call the real Extrude API.
  assert.ok(
    EXTRUDE_EDGES_SRC.includes("TryExtrudeFrozenEdges"),
    "extrude-edges must call TryExtrudeFrozenEdges with the frozen typed selection"
  );
  // typed request.Edges 转为 ProBuilderInput.EdgePair。
  assert.ok(
    EXTRUDE_EDGES_SRC.includes("EdgePair"),
    "extrude-edges must accept edge vertex pairs"
  );

  // Must NOT use the old fake edge count calculation
  assert.ok(
    !EXTRUDE_EDGES_SRC.includes("allEdges.Length * 3 / 2"),
    "extrude-edges must NOT use fake edge count (faces*3/2)"
  );
  assert.ok(
    !EXTRUDE_EDGES_SRC.includes("Extrude all edges"),
    "extrude-edges must NOT have the old 'extrude all edges' stub comment"
  );
});

// ─── bridge-edges: must call AppendElements.Bridge ──────────────────────

test("bridge-edges calls AppendElements.Bridge instead of stub", () => {
  // OLD: just called RefreshMesh and returned newFaceCount: 1.
  // effect 必须消费 pre-effect 冻结的 typed edge，不能在 barrier 后重新解析 request。
  assert.ok(
    BRIDGE_EDGES_SRC.includes("ProBuilderReflection.TryBridgeFrozenEdges"),
    "bridge-edges must call TryBridgeFrozenEdges"
  );
  assert.ok(BRIDGE_EDGES_SRC.includes("frozenEdgeA") && BRIDGE_EDGES_SRC.includes("frozenEdgeB"));
  // 两条边来自 typed request.EdgeA/EdgeB。
  assert.ok(
    BRIDGE_EDGES_SRC.includes("request.EdgeA"),
    "bridge-edges must parse edgeA from request"
  );
  assert.ok(
    BRIDGE_EDGES_SRC.includes("request.EdgeB"),
    "bridge-edges must parse edgeB from request"
  );
});

// ─── connect-elements: must call ConnectElements.Connect ───────────────

test("connect-elements consumes the frozen plan with typed ConnectElements.Connect", () => {
  assert.match(CONNECT_ELEMENTS_SRC, /ConnectElements\.Connect\(typedMesh, \(UnityEngine\.ProBuilder\.(?:Face|Edge)\[\]\)plan\.Frozen(?:Faces|Edges)\)/);
  assert.ok(CONNECT_ELEMENTS_SRC.includes("ProBuilderConnectCompletionProof.Capture"));
  assert.ok(CONNECT_ELEMENTS_SRC.indexOf("ProBuilderConnectCompletionProof.Capture") < CONNECT_ELEMENTS_SRC.indexOf("ctx.CommitEffect()"));
  assert.equal((CONNECT_ELEMENTS_SRC.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1);
  assert.match(CONNECT_ELEMENTS_SRC, /ProBuilderConnectCompletionProof\.Matches\(plan, after\)/);
  assert.match(CONNECT_ELEMENTS_SRC, /ProBuilderErrors\.OutcomeUnknown\(/);
  assert.doesNotMatch(CONNECT_ELEMENTS_SRC, /ProBuilderReflection\.ConnectElements/);
});

// ─── bevel-edges: must call Bevel.BevelEdges ────────────────────────────

test("bevel-edges consumes the frozen plan with typed Bevel.BevelEdges", () => {
  assert.ok(BEVEL_EDGES_SRC.includes("Bevel.BevelEdges(typedMesh, plan.FrozenEdges, amount)"));
  assert.ok(BEVEL_EDGES_SRC.includes("ProBuilderBevelCompletionProof.Capture"));
  assert.doesNotMatch(BEVEL_EDGES_SRC, /ProBuilderReflection\.BevelEdges/);
  // Must NOT return hardcoded "modifiedEdgeCount", 1
  assert.ok(
    !BEVEL_EDGES_SRC.includes(".Prop(\"modifiedEdgeCount\", 1)"),
    "bevel-edges must NOT return hardcoded modifiedEdgeCount: 1"
  );
});

// ─── duplicate-and-flip: must call DuplicateAndFlip ────────────────────

test("duplicate-and-flip consumes the frozen plan with typed AppendElements API", () => {
  assert.ok(DUPLICATE_AND_FLIP_SRC.includes("AppendElements.DuplicateAndFlip(typedMesh, plan.FrozenFaces)"));
  assert.ok(DUPLICATE_AND_FLIP_SRC.includes("ProBuilderFlipCompletionProof.CaptureDuplicateAndFlip"));
  assert.doesNotMatch(DUPLICATE_AND_FLIP_SRC, /ProBuilderReflection\.DuplicateAndFlip/);
});

// ─── combine-meshes / merge-objects: must use CombineMeshes.Combine ────

test("combine-meshes uses ProBuilder Combine and removes every absorbed source through Undo", () => {
  assert.match(COMBINE_MESHES_SRC, /CombineMeshesViaReflection\(meshes, meshes\[0\]\)/);
  assert.match(COMBINE_MESHES_SRC, /targets\.Skip\(1\)[\s\S]*Undo\.DestroyObjectImmediate\(source\)/);
  assert.match(COMBINE_MESHES_SRC, /ProBuilderAtomicReadback\.SourcesWereRemovedAndGeometryIncluded/);
  assert.match(COMBINE_MESHES_SRC, /totalVertices >= ProBuilderMesh\.maxVertexCount/);
  assert.doesNotMatch(COMBINE_MESHES_SRC, /ProBuilderComplexTopologyCompletionProof/);
});

test("merge-objects uses ProBuilder CombineMeshes with exact existing-ProBuilder proof", () => {
  assert.ok(
    MERGE_OBJECTS_SRC.includes("CombineMeshesViaReflection"),
    "merge-objects must call CombineMeshesViaReflection"
  );
  assert.ok(
    MERGE_OBJECTS_SRC.includes("CaptureMergeObjects"),
    "merge-objects must freeze an exact merge plan before its effect"
  );
  assert.ok(
    !MERGE_OBJECTS_SRC.includes("ConvertToProBuilderMesh"),
    "merge-objects must reject regular meshes rather than auto-convert them"
  );
});

// ─── create-polygon: must call AppendElements.CreatePolygon ─────────────

test("create-polygon calls AppendElements.CreatePolygon and verifies the distinct vertex set", () => {
  assert.ok(
    CREATE_POLYGON_SRC.includes("ProBuilderReflection.CreatePolygon"),
    "create-polygon must call ProBuilderReflection.CreatePolygon"
  );
  assert.match(CREATE_POLYGON_SRC, /ProBuilderAtomicReadback\.PolygonWasCreatedFromRequestedVertices/);
});

// ─── append-vertices-to-edge: must call AppendElements.AppendVerticesToEdge ──

test("append-vertices-to-edge calls AppendElements.AppendVerticesToEdge", () => {
  assert.ok(
    APPEND_VERTICES_SRC.includes("ProBuilderReflection.AppendVerticesToEdge"),
    "append-vertices-to-edge must call ProBuilderReflection.AppendVerticesToEdge"
  );
  assert.ok(
    APPEND_VERTICES_SRC.includes("ProBuilderOperationProof.ValidateEdges"),
    "append-vertices-to-edge must validate requested edges and capture fresh operation proof"
  );
  assert.ok(APPEND_VERTICES_SRC.includes("ProBuilderAppendCompletionProof.TryCreate"), "append-vertices-to-edge must freeze an exact plan");
  assert.ok(APPEND_VERTICES_SRC.includes("ProBuilderAppendCompletionProof.Matches"), "append-vertices-to-edge must prove fresh terminal state");
});

// ─── insert-vertex: must support edge pairs and face index ─────────────

test("insert-vertex requires exactly one edge or face seed", () => {
  // 边来自 typed request.Edge，面来自 faceIndex。
  assert.ok(
    INSERT_VERTEX_SRC.includes("request.Edge"),
    "insert-vertex must parse edge from request"
  );
  assert.ok(
    INSERT_VERTEX_SRC.includes("InsertVertexOnEdgeByPair"),
    "insert-vertex must call InsertVertexOnEdgeByPair when edge is provided"
  );
  assert.ok(
    INSERT_VERTEX_SRC.includes("InsertVertexInFace"),
    "insert-vertex must call InsertVertexInFace when faceIndex is provided"
  );
  assert.match(INSERT_VERTEX_SRC, /Pass exactly one of edge or faceIndex/);
  assert.doesNotMatch(INSERT_VERTEX_SRC, /InsertVertexOnEdge\(mesh, null/);
  assert.match(INSERT_VERTEX_SRC, /Inserted = true/);
  assert.doesNotMatch(INSERT_VERTEX_SRC, /vertexCountAfter <= vertexCountBefore/);
});

// ─── move-vertices: must offset positions, not set-all-to-same ─────────

test("move-vertices offsets positions via MoveVerticesByOffset", () => {
  // OLD: created an array of identical offset vectors and called SetVertexPositions,
  // which would move ALL vertices to the same absolute position.
  // NEW: must call MoveVerticesByOffset which adds the offset to each vertex.
  assert.ok(
    MOVE_VERTICES_SRC.includes("ProBuilderReflection.MoveVerticesByOffset"),
    "move-vertices must call ProBuilderReflection.MoveVerticesByOffset"
  );

  // Must NOT use the old SetVertexPositions with identical positions
  assert.ok(
    !MOVE_VERTICES_SRC.includes("positions[i] = offset"),
    "move-vertices must NOT create array of identical offset positions"
  );
});

// ─── merge/weld/split-vertices: must use VertexEditing API ──────────────

test("merge-vertices uses VertexEditing.MergeVertices with collapseToFirst", () => {
  assert.ok(
    MERGE_VERTICES_SRC.includes("MergeVerticesWithCollapse"),
    "merge-vertices must call MergeVerticesWithCollapse"
  );
  // 合并策略由 request.CollapseToFirst 指定。
  assert.ok(
    MERGE_VERTICES_SRC.includes("CollapseToFirst"),
    "merge-vertices must support collapseToFirst parameter"
  );
});

test("weld-vertices uses VertexEditing.WeldVertices with radius", () => {
  assert.ok(
    WELD_VERTICES_SRC.includes("WeldVerticesWithRadius"),
    "weld-vertices must call WeldVerticesWithRadius"
  );
});

test("split-vertices uses VertexEditing.SplitVertices with a typed partition claim", () => {
  assert.ok(
    SPLIT_VERTICES_SRC.includes("SplitVerticesWithList"),
    "split-vertices must call SplitVerticesWithList"
  );
  assert.match(SPLIT_VERTICES_SRC, /CaptureSplitVertexClaim\(mesh, indices\)/);
  assert.match(SPLIT_VERTICES_SRC, /SplitVerticesMatch\(fresh, plan\)/);
});

// ─── set-face-color: must use SetFaceColor (not ProBuilder.Color) ───────

test("set-face-color uses ProBuilderMesh.SetFaceColor and nullable white defaults", () => {
  assert.ok(
    SET_FACE_COLOR_SRC.includes("ProBuilderReflection.SetFaceColor"),
    "set-face-color must call ProBuilderReflection.SetFaceColor"
  );
  assert.match(SET_FACE_COLOR_SRC, /public double\? R/);
  assert.match(SET_FACE_COLOR_SRC, /request\.R \?\? 1d/);
  assert.match(SET_FACE_COLOR_SRC, /request\.G \?\? 1d/);
  assert.match(SET_FACE_COLOR_SRC, /request\.B \?\? 1d/);
  assert.match(SET_FACE_COLOR_SRC, /request\.A \?\? 1d/);
  assert.ok(
    !SET_FACE_COLOR_SRC.includes("UnityEngine.ProBuilder.Color"),
    "set-face-color must NOT reference the non-existent ProBuilder.Color type"
  );
});

// ─── set-face-uvs: must modify Face.uv fields ──────────────────────────

test("set-face-uvs modifies Face.uv fields and replays RefreshUV", () => {
  assert.match(SET_FACE_UVS_SRC, /GetProperty\("uv", BindingFlags\.Instance \| BindingFlags\.Public\)/);
  assert.match(SET_FACE_UVS_SRC, /\(AutoUnwrapSettings\)uv\.GetValue\(face\)/);
  assert.match(SET_FACE_UVS_SRC, /uv\.SetValue\(face, value\)/);
  assert.match(SET_FACE_UVS_SRC, /GetMethod\("RefreshUV"/);
  assert.match(SET_FACE_UVS_SRC, /ProBuilderAtomicReadback\.RequestedFacesHaveUv/);
  assert.doesNotMatch(SET_FACE_UVS_SRC, /ProBuilderAttributeTransformCompletionProof/);
});

// ─── select-faces: must support direction-based selection ──────────────

test("select-faces supports direction-based selection (not just flood)", () => {
  // OLD: only flood=true returned all faces; everything else returned empty.
  // NEW: must compute face normals and select by direction.
  assert.ok(
    SELECT_FACES_SRC.includes("ComputeFaceNormal"),
    "select-faces must compute face normals for direction-based selection"
  );
  assert.ok(
    SELECT_FACES_SRC.includes("Vector3.Dot"),
    "select-faces must use Vector3.Dot to compare normals with target direction"
  );
  assert.ok(
    SELECT_FACES_SRC.includes("north"),
    "select-faces must support 'north' direction"
  );
  assert.ok(
    SELECT_FACES_SRC.includes("forward"),
    "select-faces must support 'forward' direction alias"
  );
  assert.match(SELECT_FACES_SRC, /SeedFaceIndices/);
  assert.match(SELECT_FACES_SRC, /ProBuilderReflection\.SelectFaces/);
  assert.match(REFLECTION_SRC, /current = UnionFaceArrays\(current, neighboring\)/);
});

// ─── convert-to-probuilder: must use MeshImporter ───────────────────────

test("convert-to-probuilder uses the exact MeshImporter path", () => {
  assert.ok(
    CONVERT_SRC.includes("ConvertToProBuilderMesh"),
    "convert-to-probuilder must call ConvertToProBuilderMesh"
  );
  assert.match(REFLECTION_SRC, /GetConstructor\(new\[\] \{ typeof\(GameObject\) \}\)/);
  assert.match(REFLECTION_SRC, /GetMethod\("Import", new\[\] \{ MeshImportSettingsType\.Value \}\)/);
});

// ─── freeze-transform: must bake positions into vertices ───────────────

test("freeze-transform uses the exact 5.2.4 scale-only operation and typed bake claim", () => {
  assert.match(FREEZE_TRANSFORM_SRC, /ProBuilderAtomicReadback\.CaptureScaleBake\(go, mesh\)/);
  assert.match(FREEZE_TRANSFORM_SRC, /mesh\.FreezeScaleTransform\(\)/);
  assert.match(FREEZE_TRANSFORM_SRC, /ProBuilderAtomicReadback\.ScaleWasBaked\(freshGo, fresh, plan\)/);
  assert.doesNotMatch(FREEZE_TRANSFORM_SRC, /ProBuilderAttributeTransformCompletionProof|position = Vector3\.zero|rotation = Quaternion\.identity/);
});

// ─── mesh-info: must use real face/vertex count properties ─────────────

test("mesh-info reports real vertex and face counts", () => {
  assert.ok(
    MESH_INFO_SRC.includes("GetVertexCount"),
    "mesh-info must report real vertex count"
  );
  assert.ok(
    MESH_INFO_SRC.includes("GetFaceCount"),
    "mesh-info must report real face count"
  );
  // bounds 使用嵌套 DTO 表示。
  assert.ok(
    MESH_INFO_SRC.includes("bounds.center"),
    "mesh-info must report real bounds from renderer"
  );
});

// ─── repair-mesh: must call RemoveDegenerateTriangles ───────────────────

test("repair-mesh calls RemoveDegenerateTriangles and RemoveUnusedVertices", () => {
  assert.ok(
    REPAIR_MESH_SRC.includes("RemoveDegenerateTriangles"),
    "repair-mesh must call RemoveDegenerateTriangles"
  );
  assert.ok(
    REPAIR_MESH_SRC.includes("RemoveUnusedVertices"),
    "repair-mesh must call RemoveUnusedVertices"
  );
});
