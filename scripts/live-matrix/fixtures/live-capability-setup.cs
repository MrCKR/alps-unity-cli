public static object Run(string argsJson)
{
    var action = argsJson ?? string.Empty;
    if (action.IndexOf("prefab-create", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        UnityEditor.AssetDatabase.DeleteAsset("Assets/Prefabs/Player.prefab");
        UnityEditor.AssetDatabase.Refresh(UnityEditor.ImportAssetOptions.ForceSynchronousImport);
        return Complete("prefab-create");
    }

    if (action.IndexOf("prefab-unpack", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        var source = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        if (source == null) throw new InvalidOperationException("Player prefab fixture is missing.");
        var existing = GameObject.Find("Player");
        if (existing != null) UnityEngine.Object.DestroyImmediate(existing);
        var instance = UnityEditor.PrefabUtility.InstantiatePrefab(source) as GameObject;
        if (instance == null) throw new InvalidOperationException("Player prefab fixture could not be instantiated.");
        instance.name = "Player";
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(instance.scene);
        return Complete("prefab-unpack");
    }

    if (action.IndexOf("cinemachine-extension-absent", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        var target = GameObject.Find("PlayerCam");
        if (target == null) throw new InvalidOperationException("PlayerCam fixture target is missing.");
        foreach (var component in target.GetComponents<Component>())
        {
            var type = component.GetType();
            if (type.Name == "CinemachineCollider" || type.FullName == "Cinemachine.CinemachineCollider")
                UnityEngine.Object.DestroyImmediate(component);
        }
        return Complete("cinemachine-extension-absent");
    }

    if (action.IndexOf("probuilder-generic", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        EnsureProBuilderGenericFixtures();
        return Complete("probuilder-generic");
    }

    if (action.IndexOf("scene-create", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        UnityEditor.AssetDatabase.DeleteAsset("Assets/Scenes/Main.unity");
        UnityEditor.AssetDatabase.Refresh(UnityEditor.ImportAssetOptions.ForceSynchronousImport);
        return Complete("scene-create");
    }

    if (action.IndexOf("scene-main-open", StringComparison.OrdinalIgnoreCase) >= 0)
        return OpenSceneFixture("Assets/Scenes/Main.unity", "scene-main-open");
    if (action.IndexOf("scene-gameplay-open", StringComparison.OrdinalIgnoreCase) >= 0)
        return OpenSceneFixture("Assets/Scenes/Gameplay.unity", "scene-gameplay-open");
    if (action.IndexOf("scene-level2-open", StringComparison.OrdinalIgnoreCase) >= 0)
        return OpenSceneFixture("Assets/Scenes/Level2.unity", "scene-level2-open");

    if (action.IndexOf("ui-document-create", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        UnityEditor.AssetDatabase.DeleteAsset("Assets/UI/Main.uxml");
        UnityEditor.AssetDatabase.DeleteAsset("Assets/UI/Main.uss");
        UnityEditor.AssetDatabase.Refresh(UnityEditor.ImportAssetOptions.ForceSynchronousImport);
        return Complete("ui-document-create");
    }

    if (action.IndexOf("ui-panel-settings-absent", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        UnityEditor.AssetDatabase.DeleteAsset("Assets/UI/Panel.asset");
        UnityEditor.AssetDatabase.Refresh(UnityEditor.ImportAssetOptions.ForceSynchronousImport);
        return Complete("ui-panel-settings-absent");
    }

    if (action.IndexOf("vfx-parameter", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        EnsureVfxParameterFixture();
        return Complete("vfx-parameter");
    }

    if (action.IndexOf("vfx-line-target", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        foreach (var target in UnityEngine.Object.FindObjectsOfType<GameObject>())
            if (string.Equals(target.name, "AimLine", StringComparison.Ordinal)) UnityEngine.Object.DestroyImmediate(target);
        var lineObject = new GameObject("AimLine");
        var line = lineObject.AddComponent<LineRenderer>();
        line.positionCount = 2;
        line.SetPositions(new[] { Vector3.zero, Vector3.right });
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(lineObject.scene);
        return Complete("vfx-line-target");
    }

    if (action.IndexOf("profiler-snapshots", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        // compare 只读取文件级 metadata；夹具写入两个固定非空输入，不冒充 Memory Profiler capture。
        var projectRoot = System.IO.Directory.GetParent(Application.dataPath).FullName;
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(projectRoot, "snap1.snap"), new byte[] { 0x55, 0x43, 0x4c, 0x49, 0x01 });
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(projectRoot, "snap2.snap"), new byte[] { 0x55, 0x43, 0x4c, 0x49, 0x01, 0x02 });
        return Complete("profiler-snapshots");
    }

    if (action.IndexOf("scriptable-object-create-absent", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        UnityEditor.AssetDatabase.DeleteAsset("Assets/Data/Config.asset");
        UnityEditor.AssetDatabase.Refresh(UnityEditor.ImportAssetOptions.ForceSynchronousImport);
        return Complete("scriptable-object-create-absent");
    }

    if (action.IndexOf("scriptable-object", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        var type = FindType("MyConfig");
        if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type))
            throw new InvalidOperationException("MyConfig ScriptableObject fixture type is unavailable after setup compilation.");
        UnityEditor.AssetDatabase.DeleteAsset("Assets/Data/Config.asset");
        UnityEditor.AssetDatabase.CreateAsset(ScriptableObject.CreateInstance(type), "Assets/Data/Config.asset");
        UnityEditor.AssetDatabase.SaveAssets();
        return Complete("scriptable-object");
    }

    if (action.IndexOf("ui-detach-target", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        EnsureDocument(GameObject.Find("Player") ?? new GameObject("Player"), false);
        return Complete("ui-detach-target");
    }

    if (action.IndexOf("ui-visual-element", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        EnsureDocument(GameObject.Find("UIDocument") ?? new GameObject("UIDocument"), true);
        return Complete("ui-visual-element");
    }

    throw new ArgumentException("Unknown live capability fixture action: " + action);
}

private static object OpenSceneFixture(string path, string action)
{
    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(path);
    if (!scene.IsValid() || !scene.isLoaded)
        scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(path, UnityEditor.SceneManagement.OpenSceneMode.Additive);
    if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("Scene fixture did not open: " + path);
    return Complete(action, path);
}

// 每个控制脚本都通过同一个可读场景状态证明执行完成，fixture 随后恢复该状态。
private static object Complete(string action, string path = null)
{
    var camera = GameObject.Find("Main Camera");
    if (camera == null) throw new InvalidOperationException("Main Camera completion-proof target is missing.");
    camera.transform.position = new Vector3(32000f, 0f, 0f);
    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(camera.scene);
    var result = new Dictionary<string, object> { { "action", action }, { "ready", true } };
    if (!string.IsNullOrEmpty(path)) result.Add("path", path);
    return result;
}

private static Type FindType(string fullName)
{
    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
    {
        var type = assembly.GetType(fullName, false);
        if (type != null) return type;
    }
    return null;
}

private static void EnsureDocument(GameObject target, bool requireLabel)
{
    var document = target.GetComponent<UnityEngine.UIElements.UIDocument>() ?? target.AddComponent<UnityEngine.UIElements.UIDocument>();
    document.panelSettings = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>("Assets/UI/Panel.asset");
    document.visualTreeAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>("Assets/UI/Main.uxml");
    if (requireLabel)
    {
        document.rootVisualElement.Clear();
        document.rootVisualElement.Add(new UnityEngine.UIElements.Label("Matrix") { name = "label" });
    }
    UnityEditor.EditorUtility.SetDirty(document);
}

// 为非 frozen ProBuilder descriptor 重建精确 5.2.4 proof domain，禁止依赖前一个 case 的场景副作用。
private static void EnsureProBuilderGenericFixtures()
{
    var names = new[]
    {
        "MyCube", "SingleTriangle", "TriangleA", "TriangleB", "TwoIndependentTriangles", "SharedEdgeTriangles",
        "TwoTrianglesSharedCorner", "TriangleWithUnusedRaw", "MyTriangle", "MyTwoMaterialMesh", "MyPrimitive"
    };
    foreach (var target in UnityEngine.Object.FindObjectsOfType<GameObject>())
    {
        if (Array.IndexOf(names, target.name) >= 0 || target.name.StartsWith("TwoIndependentTriangles_Detached", StringComparison.Ordinal))
            UnityEngine.Object.DestroyImmediate(target);
    }

    var existing = EnsureFixtureMaterial("Assets/Materials/Existing.mat", Color.cyan);
    var alternate = EnsureFixtureMaterial("Assets/Materials/A.mat", Color.white);
    var triangle = new[] { new Vector3(2f, 0f, 0f), Vector3.zero, new Vector3(1f, 0f, 1f) };
    var triangleB = new[] { new Vector3(5f, 0f, 0f), new Vector3(3f, 0f, 0f), new Vector3(4f, 0f, 1f) };

    CreateProBuilderFixture("MyCube", triangle, new[] { new[] { 0, 1, 2 } }, SingletonGroups(3), new[] { existing }, new[] { 0 }, true);
    var genericTarget = GameObject.Find("MyCube");
    genericTarget.transform.position = new Vector3(5f, 0f, 0f);
    genericTarget.transform.localScale = new Vector3(2f, 1f, 1f);
    CreateProBuilderFixture("SingleTriangle", triangle, new[] { new[] { 0, 1, 2 } }, SingletonGroups(3), new[] { existing }, new[] { 0 }, true);
    CreateProBuilderFixture("TriangleA", triangle, new[] { new[] { 0, 1, 2 } }, SingletonGroups(3), new[] { existing }, new[] { 0 }, true);
    CreateProBuilderFixture("TriangleB", triangleB, new[] { new[] { 0, 1, 2 } }, SingletonGroups(3), new[] { existing }, new[] { 0 }, true);

    var independent = new[] { triangle[0], triangle[1], triangle[2], triangleB[0], triangleB[1], triangleB[2] };
    CreateProBuilderFixture("TwoIndependentTriangles", independent, new[] { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } }, SingletonGroups(6), new[] { existing }, new[] { 0, 0 }, true);

    var sharedEdge = new[]
    {
        new Vector3(2f, 0f, 0f), Vector3.zero, new Vector3(1f, 0f, 1f),
        new Vector3(1f, 0f, 1f), Vector3.zero, new Vector3(-1f, 0f, 1f)
    };
    var sharedEdgeMesh = CreateProBuilderFixture("SharedEdgeTriangles", sharedEdge, new[] { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } }, SingletonGroups(6), new[] { existing }, new[] { 0, 0 }, true);
    UnityEngine.ProBuilder.MeshOperations.VertexEditing.MergeVertices(sharedEdgeMesh, new[] { 1, 4 }, false);
    UnityEngine.ProBuilder.MeshOperations.VertexEditing.MergeVertices(sharedEdgeMesh, new[] { 2, 3 }, false);
    sharedEdgeMesh.ToMesh();
    sharedEdgeMesh.Refresh();

    var sharedCorner = new[]
    {
        new Vector3(2f, 0f, 0f), Vector3.zero, new Vector3(1f, 0f, 1f),
        new Vector3(2f, 0f, 0f), new Vector3(3f, 0f, 1f), new Vector3(4f, 0f, 0f)
    };
    var sharedCornerMesh = CreateProBuilderFixture("TwoTrianglesSharedCorner", sharedCorner, new[] { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } }, SingletonGroups(6), new[] { existing }, new[] { 0, 0 }, true);
    UnityEngine.ProBuilder.MeshOperations.VertexEditing.MergeVertices(sharedCornerMesh, new[] { 0, 3 }, false);
    sharedCornerMesh.ToMesh();
    sharedCornerMesh.Refresh();

    var repairMesh = CreateProBuilderFixture("TriangleWithUnusedRaw", triangle, new[] { new[] { 0, 1, 2 } }, SingletonGroups(3), new[] { existing }, new[] { 0 }, true);
    var repairVertices = repairMesh.GetVertices().ToList();
    var unusedVertex = new UnityEngine.ProBuilder.Vertex(repairVertices[0]) { position = new Vector3(10f, 0f, 10f) };
    repairVertices.Add(unusedVertex);
    repairMesh.SetVertices(repairVertices);
    repairMesh.sharedVertices = SingletonGroups(4).Select(group => new UnityEngine.ProBuilder.SharedVertex(group)).ToArray();
    repairMesh.ToMesh();
    var repairCompiled = repairMesh.GetComponent<MeshFilter>().sharedMesh;
    repairCompiled.normals = repairMesh.normals.ToArray();
    repairCompiled.tangents = repairMesh.tangents.ToArray();
    repairCompiled.colors = repairMesh.colors.ToArray();
    repairCompiled.uv = repairMesh.textures.ToArray();
    repairCompiled.RecalculateBounds();
    UnityEditor.EditorUtility.SetDirty(repairMesh);
    CreateProBuilderFixture("MyTriangle", triangle, new[] { new[] { 0, 1, 2 } }, SingletonGroups(3), new[] { existing }, new[] { 0 }, false);
    var threeMaterialFaces = new[]
    {
        triangle[0], triangle[1], triangle[2], triangleB[0], triangleB[1], triangleB[2],
        new Vector3(8f, 0f, 0f), new Vector3(6f, 0f, 0f), new Vector3(7f, 0f, 1f)
    };
    CreateProBuilderFixture("MyTwoMaterialMesh", threeMaterialFaces, new[] { new[] { 0, 1, 2 }, new[] { 3, 4, 5 }, new[] { 6, 7, 8 } }, SingletonGroups(9), new[] { alternate, existing }, new[] { 0, 1, 0 }, true);
    CreateOrdinaryTriangleFixture("MyPrimitive", triangle, existing);
    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
}

private static Material EnsureFixtureMaterial(string path, Color color)
{
    var material = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(path);
    if (material != null) return material;
    var shader = Shader.Find("Standard") ?? Shader.Find("Hidden/InternalErrorShader");
    if (shader == null) throw new InvalidOperationException("Fixture material shader is unavailable.");
    material = new Material(shader) { color = color };
    UnityEditor.AssetDatabase.CreateAsset(material, path);
    UnityEditor.AssetDatabase.SaveAssetIfDirty(material);
    return material;
}

private static int[][] SingletonGroups(int count)
{
    var groups = new int[count][];
    for (var index = 0; index < count; index++) groups[index] = new[] { index };
    return groups;
}

private static UnityEngine.ProBuilder.ProBuilderMesh CreateProBuilderFixture(string name, Vector3[] positions, int[][] faceStreams, int[][] sharedGroups, Material[] materials, int[] submeshSlots, bool manualUv)
{
    var vertices = new List<UnityEngine.ProBuilder.Vertex>();
    for (var index = 0; index < positions.Length; index++)
    {
        var uvIndex = index % 3;
        vertices.Add(new UnityEngine.ProBuilder.Vertex
        {
            position = positions[index],
            color = Color.white,
            uv0 = uvIndex == 0 ? Vector2.zero : uvIndex == 1 ? Vector2.right : Vector2.up
        });
    }

    var faces = new List<UnityEngine.ProBuilder.Face>();
    var elementField = typeof(UnityEngine.ProBuilder.Face).GetField("elementGroup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    if (elementField == null) throw new InvalidOperationException("ProBuilder Face.elementGroup is unavailable.");
    for (var index = 0; index < faceStreams.Length; index++)
    {
        var face = new UnityEngine.ProBuilder.Face(faceStreams[index])
        {
            submeshIndex = submeshSlots[index],
            smoothingGroup = 0,
            textureGroup = -1,
            manualUV = manualUv,
            uv = UnityEngine.ProBuilder.AutoUnwrapSettings.tile
        };
        elementField.SetValue(face, -1);
        faces.Add(face);
    }

    var shared = new List<UnityEngine.ProBuilder.SharedVertex>();
    foreach (var group in sharedGroups) shared.Add(new UnityEngine.ProBuilder.SharedVertex(group));
    var mesh = UnityEngine.ProBuilder.ProBuilderMesh.Create(vertices, faces, shared, new List<UnityEngine.ProBuilder.SharedVertex>(), materials);
    if (mesh == null || mesh.gameObject == null) throw new InvalidOperationException("ProBuilder fixture creation failed: " + name);
    mesh.gameObject.name = name;
    mesh.transform.position = Vector3.zero;
    mesh.transform.rotation = Quaternion.identity;
    mesh.transform.localScale = Vector3.one;
    mesh.ToMesh();
    mesh.Refresh();
    UnityEditor.EditorUtility.SetDirty(mesh);
    return mesh;
}

// convert-to-probuilder 要求普通 Mesh 具备完整 UV0-UV3、颜色、normal 与 tangent 通道。
private static void CreateOrdinaryTriangleFixture(string name, Vector3[] positions, Material material)
{
    var target = new GameObject(name);
    var filter = target.AddComponent<MeshFilter>();
    var renderer = target.AddComponent<MeshRenderer>();
    var mesh = new Mesh { name = name + "Mesh" };
    var uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
    var normal = Vector3.Cross(positions[1] - positions[0], positions[2] - positions[0]).normalized;
    var sdir = positions[1] - positions[0];
    var tangent3 = sdir.normalized;
    Vector3.OrthoNormalize(ref normal, ref tangent3);
    var tdir = positions[2] - positions[0];
    var tangent = new Vector4(tangent3.x, tangent3.y, tangent3.z, Vector3.Dot(Vector3.Cross(normal, tangent3), tdir) < 0f ? -1f : 1f);
    mesh.vertices = positions;
    mesh.triangles = new[] { 0, 1, 2 };
    mesh.normals = new[] { normal, normal, normal };
    mesh.tangents = new[] { tangent, tangent, tangent };
    mesh.colors = new[] { Color.white, Color.white, Color.white };
    mesh.uv = uv;
    mesh.uv2 = uv;
    mesh.SetUVs(2, new List<Vector4> { uv[0], uv[1], uv[2] });
    mesh.SetUVs(3, new List<Vector4> { uv[0], uv[1], uv[2] });
    mesh.RecalculateBounds();
    filter.sharedMesh = mesh;
    renderer.sharedMaterial = material;
}

// 使用当前 VFX Editor API reflection 创建并编译两个 exposed 参数，然后绑定到固定 emitter。
private static void EnsureVfxParameterFixture()
{
    const string path = "Assets/VFX/ParameterFixture.vfx";
    UnityEditor.AssetDatabase.DeleteAsset(path);
    var utility = FindType("UnityEditor.VisualEffectAssetEditorUtility");
    var create = utility == null ? null : utility.GetMethod("CreateNewAsset", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
    if (create == null) throw new InvalidOperationException("VFX graph factory is unavailable.");
    create.Invoke(null, new object[] { path });
    UnityEditor.AssetDatabase.ImportAsset(path, UnityEditor.ImportAssetOptions.ForceSynchronousImport | UnityEditor.ImportAssetOptions.ForceUpdate);

    var resourceType = FindType("UnityEditor.VFX.VisualEffectResource");
    var resourceMethod = resourceType == null ? null : resourceType.GetMethod("GetResourceAtPath", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
    var resource = resourceMethod == null ? null : resourceMethod.Invoke(null, new object[] { path });
    var extensionsType = FindType("UnityEditor.VFX.VisualEffectResourceExtensions");
    var getGraph = extensionsType == null ? null : extensionsType.GetMethod("GetOrCreateGraph", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
    var graph = getGraph == null || resource == null ? null : getGraph.Invoke(null, new[] { resource });
    if (graph == null) throw new InvalidOperationException("VFX graph model is unavailable.");

    var parameterType = FindType("UnityEditor.VFX.VFXParameter");
    if (parameterType == null) throw new InvalidOperationException("VFXParameter model type is unavailable.");
    AddVfxParameter(graph, parameterType, typeof(float), "SpawnRate", 1f, 0);
    AddVfxParameter(graph, parameterType, typeof(Vector4), "Tint", Vector4.one, 1);
    InvokeOptional(graph, "SetExpressionGraphDirty", new object[] { true });
    InvokeOptional(graph, "RecompileIfNeeded", new object[] { false, false });
    UnityEditor.AssetDatabase.SaveAssets();
    UnityEditor.AssetDatabase.ImportAsset(path, UnityEditor.ImportAssetOptions.ForceSynchronousImport | UnityEditor.ImportAssetOptions.ForceUpdate);

    var assetType = FindType("UnityEngine.VFX.VisualEffectAsset");
    var componentType = FindType("UnityEngine.VFX.VisualEffect");
    var asset = assetType == null ? null : UnityEditor.AssetDatabase.LoadAssetAtPath(path, assetType);
    var emitter = GameObject.Find("Emitter");
    var component = emitter == null || componentType == null ? null : emitter.GetComponent(componentType);
    if (asset == null || component == null) throw new InvalidOperationException("VFX parameter fixture asset or emitter component is missing.");
    var assetProperty = componentType.GetProperty("visualEffectAsset", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
    if (assetProperty == null) throw new InvalidOperationException("VisualEffect.visualEffectAsset is unavailable.");
    assetProperty.SetValue(component, asset, null);
    UnityEditor.EditorUtility.SetDirty(component);
    var hasFloat = componentType.GetMethod("HasFloat", new[] { typeof(string) });
    var hasVector4 = componentType.GetMethod("HasVector4", new[] { typeof(string) });
    if (hasFloat == null || hasVector4 == null || !(bool)hasFloat.Invoke(component, new object[] { "SpawnRate" }) || !(bool)hasVector4.Invoke(component, new object[] { "Tint" }))
        throw new InvalidOperationException("Compiled VFX graph did not expose SpawnRate and Tint.");
}

private static void AddVfxParameter(object graph, Type parameterType, Type valueType, string name, object value, int order)
{
    var parameter = ScriptableObject.CreateInstance(parameterType);
    parameterType.GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public).Invoke(parameter, new object[] { valueType });
    parameterType.GetProperty("value", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public).SetValue(parameter, value, null);
    SetField(parameter, "m_ExposedName", name);
    SetField(parameter, "m_Exposed", true);
    SetField(parameter, "m_Order", order);
    var addChild = graph.GetType().GetMethod("AddChild", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
    if (addChild == null) throw new InvalidOperationException("VFXGraph.AddChild is unavailable.");
    addChild.Invoke(graph, new object[] { parameter, -1, true });
}

private static void SetField(object target, string name, object value)
{
    for (var type = target.GetType(); type != null; type = type.BaseType)
    {
        var field = type.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (field == null) continue;
        field.SetValue(target, value);
        return;
    }
    throw new InvalidOperationException("Field is unavailable: " + name);
}

private static void InvokeOptional(object target, string name, object[] args)
{
    var method = target.GetType().GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
        .FirstOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length);
    if (method != null) method.Invoke(target, args);
}
