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
 * - connect-elements calls ConnectElements.Connect
 * - bevel-edges calls Bevel.BevelEdges
 * - duplicate-and-flip calls AppendElements.DuplicateAndFlip
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
    /hasColors|sharedMaterials\s*=/
  ];

  for (const { name, source } of sources) {
    for (const pattern of forbidden) {
      assert.doesNotMatch(source, pattern, `${name}: must fail closed on the current ProBuilder API`);
    }
  }
});

// ─── Reflection helper coverage ────────────────────────────────────────

test("ProBuilderReflection resolves all MeshOperations types used by reference project", () => {
  // 这些类型是实际 ProBuilder 操作的基础，必须通过可重试 resolver 调用真实 API。
  const requiredTypes = [
    "ExtrudeElementsType",
    "BevelType",
    "DeleteElementsType",
    "AppendElementsType",
    "ConnectElementsType",
    "MergeElementsType",
    "SubdivisionType",
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
    "ExtrudeEdgesBySelection",
    "BevelEdges",
    "BridgeEdges",
    "ConnectElements",
    "CreateShapeFromPolygon",
    "DuplicateAndFlip",
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
    "MergeFaces",
    "SubdivideFaces",
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
  assert.match(EXTRUDE_EDGES_SRC, /request\.Edges\?\.Select\(e => new ProBuilderInput\.EdgePair \{ a = \(int\)e\.A, b = \(int\)e\.B \}\)/);
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
  assert.match(CREATE_SHAPE_SRC, /ProBuilder 5\.x ShapeGenerator\.CreateShape failed/);
});

// ─── create-poly-shape: must use CreateShapeFromPolygon, not cube ──────

test("create-poly-shape uses CreateShapeFromPolygon instead of cube fallback", () => {
  // The OLD implementation created a Unity cube primitive and scaled it.
  // The NEW implementation must use AppendElements.CreateShapeFromPolygon.
  assert.ok(
    CREATE_POLY_SHAPE_SRC.includes("CreateShapeFromPolygon"),
    "create-poly-shape must call CreateShapeFromPolygon via reflection"
  );
  // create-poly-shape 必须不使用 CreatePrimitive(cube 回退)
  assert.ok(
    !CREATE_POLY_SHAPE_SRC.includes("CreatePrimitive"),
    "create-poly-shape must NOT use CreatePrimitive (cube fallback)"
  );
  // 必须校验 points >= 3
  assert.ok(
    CREATE_POLY_SHAPE_SRC.includes("points.Count < 3"),
    "create-poly-shape must validate at least 3 points"
  );
  // 点集来自 typed request.Points。
  assert.ok(
    CREATE_POLY_SHAPE_SRC.includes("request.Points"),
    "create-poly-shape must parse points from request"
  );
});

// ─── extrude-edges: must select edges and extrude via real API ─────────

test("extrude-edges selects edges and calls ExtrudeElements.Extrude", () => {
  // OLD: counted faces*3/2 as fake edge count, only called RefreshMesh.
  // NEW: must resolve edges from input and call the real Extrude API.
  assert.ok(
    EXTRUDE_EDGES_SRC.includes("ExtrudeEdgesBySelection"),
    "extrude-edges must call ExtrudeEdgesBySelection"
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
  // NEW: must call BridgeEdges with edgeA and edgeB vertex pairs.
  assert.ok(
    BRIDGE_EDGES_SRC.includes("ProBuilderReflection.BridgeEdges"),
    "bridge-edges must call ProBuilderReflection.BridgeEdges"
  );
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

test("connect-elements calls ConnectElements.Connect instead of stub", () => {
  assert.ok(
    CONNECT_ELEMENTS_SRC.includes("ProBuilderReflection.ConnectElements"),
    "connect-elements must call ProBuilderReflection.ConnectElements"
  );
});

// ─── bevel-edges: must call Bevel.BevelEdges ────────────────────────────

test("bevel-edges calls Bevel.BevelEdges instead of stub", () => {
  assert.ok(
    BEVEL_EDGES_SRC.includes("ProBuilderReflection.BevelEdges"),
    "bevel-edges must call ProBuilderReflection.BevelEdges"
  );
  // Must NOT return hardcoded "modifiedEdgeCount", 1
  assert.ok(
    !BEVEL_EDGES_SRC.includes(".Prop(\"modifiedEdgeCount\", 1)"),
    "bevel-edges must NOT return hardcoded modifiedEdgeCount: 1"
  );
});

// ─── duplicate-and-flip: must call DuplicateAndFlip ────────────────────

test("duplicate-and-flip calls AppendElements.DuplicateAndFlip", () => {
  // OLD: only flipped normals (no duplication).
  // NEW: must call DuplicateAndFlip.
  assert.ok(
    DUPLICATE_AND_FLIP_SRC.includes("ProBuilderReflection.DuplicateAndFlip"),
    "duplicate-and-flip must call ProBuilderReflection.DuplicateAndFlip"
  );
});

// ─── combine-meshes / merge-objects: must use CombineMeshes.Combine ────

test("combine-meshes uses ProBuilder CombineMeshes.Combine", () => {
  assert.ok(
    COMBINE_MESHES_SRC.includes("CombineMeshesViaReflection"),
    "combine-meshes must call CombineMeshesViaReflection"
  );
});

test("merge-objects uses ProBuilder CombineMeshes with auto-convert", () => {
  assert.ok(
    MERGE_OBJECTS_SRC.includes("CombineMeshesViaReflection"),
    "merge-objects must call CombineMeshesViaReflection"
  );
  assert.ok(
    MERGE_OBJECTS_SRC.includes("ConvertToProBuilderMesh"),
    "merge-objects must auto-convert non-ProBuilder objects"
  );
});

// ─── create-polygon: must call AppendElements.CreatePolygon ─────────────

test("create-polygon calls AppendElements.CreatePolygon", () => {
  assert.ok(
    CREATE_POLYGON_SRC.includes("ProBuilderReflection.CreatePolygon"),
    "create-polygon must call ProBuilderReflection.CreatePolygon"
  );
});

// ─── append-vertices-to-edge: must call AppendElements.AppendVerticesToEdge ──

test("append-vertices-to-edge calls AppendElements.AppendVerticesToEdge", () => {
  assert.ok(
    APPEND_VERTICES_SRC.includes("ProBuilderReflection.AppendVerticesToEdge"),
    "append-vertices-to-edge must call ProBuilderReflection.AppendVerticesToEdge"
  );
  assert.ok(
    APPEND_VERTICES_SRC.includes("ProBuilderReflection.ResolveEdges"),
    "append-vertices-to-edge must resolve edges from input"
  );
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

test("split-vertices uses VertexEditing.SplitVertices with IList overload", () => {
  assert.ok(
    SPLIT_VERTICES_SRC.includes("SplitVerticesWithList"),
    "split-vertices must call SplitVerticesWithList"
  );
});

// ─── set-face-color: must use SetFaceColor (not ProBuilder.Color) ───────

test("set-face-color uses ProBuilderMesh.SetFaceColor", () => {
  // OLD: looked for the non-existent UnityEngine.ProBuilder.Color type and did nothing.
  // NEW: must call SetFaceColor with a UnityEngine.Color.
  assert.ok(
    SET_FACE_COLOR_SRC.includes("ProBuilderReflection.SetFaceColor"),
    "set-face-color must call ProBuilderReflection.SetFaceColor"
  );

  // Must NOT reference the non-existent ProBuilder.Color type
  assert.ok(
    !SET_FACE_COLOR_SRC.includes("UnityEngine.ProBuilder.Color"),
    "set-face-color must NOT reference the non-existent UnityEngine.ProBuilder.Color type"
  );
});

// ─── set-face-uvs: must modify Face.uv fields ──────────────────────────

test("set-face-uvs modifies Face.uv fields (not just RefreshMesh)", () => {
  assert.ok(
    SET_FACE_UVS_SRC.includes("uvProperty"),
    "set-face-uvs must access the Face.uv property"
  );
  assert.ok(
    SET_FACE_UVS_SRC.includes("autoUnwrapType"),
    "set-face-uvs must resolve the AutoUnwrapSettings type"
  );
  assert.ok(
    SET_FACE_UVS_SRC.includes("RefreshUV"),
    "set-face-uvs must call RefreshUV after modifying UV settings"
  );
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

test("freeze-transform bakes transform into vertex positions", () => {
  // OLD: just reset transform to identity without baking positions.
  // NEW: must read positions, transform to world space, reset, write back.
  assert.ok(
    FREEZE_TRANSFORM_SRC.includes("TransformPoint"),
    "freeze-transform must use TransformPoint to bake positions into world space"
  );
  assert.ok(
    FREEZE_TRANSFORM_SRC.includes("GetPositions"),
    "freeze-transform must read positions via GetPositions"
  );
  assert.ok(
    FREEZE_TRANSFORM_SRC.includes("posList[i] = go.transform.TransformPoint(posList[i])"),
    "freeze-transform must transform each vertex to world space before resetting transform"
  );
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
