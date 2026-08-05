#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.SceneManagement;

public static class ProBuilderShapeTopologyRecordCapture
{
    private static readonly string[] Shapes = { "Cube", "Stair", "CurvedStair", "Prism", "Cylinder", "Plane", "Door", "Pipe", "Cone", "Sprite", "Arch", "Sphere", "Torus" };

    public static string Capture(string outputPath)
    {
        var scene = EditorSceneManager.NewPreviewScene();
        var rows = new List<string>();
        try
        {
            foreach (var name in Shapes)
            {
                var pb = ShapeGenerator.CreateShape((ShapeType)Enum.Parse(typeof(ShapeType), name), PivotLocation.Center);
                if (pb == null) throw new InvalidOperationException("Could not create " + name + ".");
                SceneManager.MoveGameObjectToScene(pb.gameObject, scene);
                var positions = pb.positions;
                if (!BoundsFromPositions(positions, out var bounds) || bounds.center != Vector3.zero) throw new InvalidOperationException("Center pivot capture failed for " + name + ".");
                rows.Add(Emit(name, bounds, pb));
                UnityEngine.Object.DestroyImmediate(pb.gameObject);
            }
        }
        finally { EditorSceneManager.ClosePreviewScene(scene); }
        if (rows.Count != Shapes.Length) throw new InvalidOperationException("Expected 13 rows.");
        var source = (Header() + string.Join("\n", rows) + Footer()).Replace("\r\n", "\n").Replace("\r", "\n");
        if (source.Any(character => character > 127) || Count(source, "new Record(") != Shapes.Length) throw new InvalidOperationException("Generated source is incomplete.");
        var old = File.Exists(outputPath) ? File.ReadAllText(outputPath, Encoding.ASCII) : null;
        var changed = !string.Equals(old, source, StringComparison.Ordinal);
        if (changed) WriteAtomically(outputPath, source);
        return "{\"shapeCount\":13,\"changed\":" + changed.ToString().ToLowerInvariant() + ",\"bytes\":" + Encoding.ASCII.GetByteCount(source).ToString(CultureInfo.InvariantCulture) + "}";
    }

    /// <summary>仅输出 default bounds、raw count、有向面索引和共享顶点分区。</summary>
    private static string Emit(string name, Bounds bounds, ProBuilderMesh pb)
    {
        if (pb.positions == null || pb.faces == null || pb.sharedVertices == null) throw new InvalidOperationException("Topology capture failed for " + name + ".");
        var faces = pb.faces.Select(face => face == null ? null : face.indexes).ToArray();
        if (faces.Any(indexes => indexes == null)) throw new InvalidOperationException("Face indexes are unavailable for " + name + ".");
        return "        private static Record Create" + name + "()=>new Record(\"" + name + "\"," + B(bounds) + "," + pb.positions.Count + ",new int[][]{" + string.Join(",", faces.Select(I)) + "},new int[][]{" + string.Join(",", pb.sharedVertices.Select(group => I(group))) + "});";
    }

    /// <summary>由 ProBuilder local positions 计算默认边界。</summary>
    private static bool BoundsFromPositions(IList<Vector3> positions, out Bounds bounds)
    {
        bounds = default;
        if (positions == null || positions.Count == 0 || !Finite(positions[0])) return false;
        bounds = new Bounds(positions[0], Vector3.zero);
        for (var index = 1; index < positions.Count; index++)
        {
            if (!Finite(positions[index])) return false;
            bounds.Encapsulate(positions[index]);
        }
        return Finite(bounds.center) && Finite(bounds.size);
    }

    private static string I(IEnumerable<int> values) => "new int[]{" + string.Join(",", values) + "}";
    private static string B(Bounds value) => "new Bounds(" + V3(value.center) + "," + V3(value.size) + ")";
    private static string V3(Vector3 value) => "new Vector3(" + F(value.x) + "," + F(value.y) + "," + F(value.z) + ")";
    private static string F(float value) => "F(" + BitConverter.SingleToInt32Bits(value).ToString(CultureInfo.InvariantCulture) + ")";
    private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static int Count(string value, string token) { var count = 0; for (var index = 0; (index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0; index += token.Length) count++; return count; }

    /// <summary>在目标目录完成原子替换。</summary>
    private static void WriteAtomically(string outputPath, string source)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("Generated output directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporaryPath, source, new UTF8Encoding(false));
            if (File.Exists(fullPath)) File.Replace(temporaryPath, fullPath, null);
            else File.Move(temporaryPath, fullPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string Header() => @"// <auto-generated />
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.ProBuilder;
namespace UnityCli.ProBuilder
{
    internal static class ProBuilderShapeTopologyRecords
    {
        internal const string PackageVersion = ""5.2.4"";

        internal sealed class Record
        {
            internal readonly string Shape;
            internal readonly Bounds DefaultLocalBounds;
            internal readonly int RawVertexCount;
            private readonly int[][] faceIndexes;
            private readonly int[][] sharedVertices;

            internal Record(string shape, Bounds defaultLocalBounds, int rawVertexCount, IEnumerable<int[]> faces, IEnumerable<int[]> shared)
            {
                Shape = shape;
                DefaultLocalBounds = defaultLocalBounds;
                RawVertexCount = rawVertexCount;
                faceIndexes = Copy(faces);
                sharedVertices = Copy(shared);
            }

            internal string Mismatch(object mesh)
            {
                var pb = mesh as ProBuilderMesh;
                if (pb == null) return ""topology.probuilder-component"";
                if (pb.positions == null || pb.positions.Count != RawVertexCount) return ""topology.raw-vertex-count"";
                if (pb.faces == null || pb.faces.Count != faceIndexes.Length) return ""topology.face-count"";
                for (var i = 0; i < faceIndexes.Length; i++)
                {
                    var actual = pb.faces[i] == null ? null : pb.faces[i].indexes;
                    if (!Same(actual, faceIndexes[i])) return ""topology.face["" + i + ""]"";
                }
                if (pb.sharedVertices == null || pb.sharedVertices.Count != sharedVertices.Length) return ""topology.shared-count"";
                for (var i = 0; i < sharedVertices.Length; i++)
                    if (!Same(pb.sharedVertices[i].ToArray(), sharedVertices[i])) return ""topology.shared["" + i + ""]"";
                return null;
            }
        }

";

    private static string Footer() => @"
        private static readonly Record[] Records = new Record[] { CreateCube(), CreateStair(), CreateCurvedStair(), CreatePrism(), CreateCylinder(), CreatePlane(), CreateDoor(), CreatePipe(), CreateCone(), CreateSprite(), CreateArch(), CreateSphere(), CreateTorus() };

        internal static bool TryGet(string shape, out Record record)
        {
            for (var i = 0; i < Records.Length; i++)
                if (Records[i].Shape == shape) { record = Records[i]; return true; }
            record = null;
            return false;
        }

        private static int[][] Copy(IEnumerable<int[]> values) => values.Select(value => (int[])value.Clone()).ToArray();
        private static bool Same(IList<int> left, IList<int> right)
        {
            if (left == null || right == null || left.Count != right.Count) return false;
            for (var i = 0; i < left.Count; i++) if (left[i] != right[i]) return false;
            return true;
        }
        private static float F(int bits) => BitConverter.Int32BitsToSingle(bits);
    }
}
#endif
";
}
#endif
