public static object Run(string argsJson)
{
    const string snapshotPath = "Library/UnityCliMatrixPipelineSnapshot.txt";
    var restore = argsJson != null && argsJson.IndexOf("restore", StringComparison.OrdinalIgnoreCase) >= 0;
    if (restore)
    {
        RestorePipeline(snapshotPath);
        SetProofPosition();
        return "restored";
    }

    SnapshotPipeline(snapshotPath);
    var useHdrp = argsJson != null && argsJson.IndexOf("hdrp", StringComparison.OrdinalIgnoreCase) >= 0;
    const string root = "Assets/UnityCLIValidation";
    if (!UnityEditor.AssetDatabase.IsValidFolder(root)) UnityEditor.AssetDatabase.CreateFolder("Assets", "UnityCLIValidation");
    if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/Settings")) UnityEditor.AssetDatabase.CreateFolder("Assets", "Settings");

    UnityEngine.Rendering.RenderPipelineAsset pipeline;
    string pipelinePath;
    if (useHdrp)
    {
        var pipelineType = RequireType("UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset");
        pipelinePath = root + "/MatrixHDRP.asset";
        pipeline = CreatePipelineAsset(pipelineType, pipelinePath);
        EnsureGlobalVolume(root + "/MatrixHDRPVolumeProfile.asset", true);
    }
    else
    {
        var rendererType = RequireType("UnityEngine.Rendering.Universal.UniversalRendererData");
        var pipelineType = RequireType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset");
        const string rendererPath = "Assets/Settings/URPRenderer.asset";
        CreateAsset(rendererType, rendererPath);
        var renderer = UnityEditor.AssetDatabase.LoadMainAssetAtPath(rendererPath);
        if (renderer == null) throw new InvalidOperationException("URP renderer data could not be reloaded after asset creation.");
        pipelinePath = "Assets/Settings/URP.asset";
        pipeline = CreatePipelineAsset(pipelineType, pipelinePath);
        using (var serialized = new UnityEditor.SerializedObject(pipeline))
        {
            var rendererList = serialized.FindProperty("m_RendererDataList");
            if (rendererList != null)
            {
                rendererList.arraySize = 1;
                rendererList.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
            }
            var rendererData = serialized.FindProperty("m_RendererData");
            if (rendererData != null) rendererData.objectReferenceValue = renderer;
            var defaultIndex = serialized.FindProperty("m_DefaultRendererIndex");
            if (defaultIndex != null) defaultIndex.intValue = 0;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
        UnityEditor.EditorUtility.SetDirty(pipeline);
        UnityEditor.AssetDatabase.SaveAssets();
        EnsureGlobalVolume(root + "/MatrixURPVolumeProfile.asset", false);
        renderer = UnityEditor.AssetDatabase.LoadMainAssetAtPath(rendererPath);
        if (renderer == null) throw new InvalidOperationException("URP renderer data became unavailable during pipeline setup.");
        EnsureUrpRendererFeature(renderer, root + "/MatrixURPFeature.asset");
    }

    pipeline = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.RenderPipelineAsset>(pipelinePath);
    if (pipeline == null) throw new InvalidOperationException("Render pipeline asset became unavailable during setup: " + pipelinePath);
    if (!useHdrp)
    {
        using (var verify = new UnityEditor.SerializedObject(pipeline))
        {
            var rendererList = verify.FindProperty("m_RendererDataList");
            if (rendererList == null || rendererList.arraySize < 1 || rendererList.GetArrayElementAtIndex(0).objectReferenceValue == null)
                throw new InvalidOperationException("URP pipeline renderer data index 0 was not persisted.");
        }
    }
    UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline = pipeline;
    var qualityPipeline = typeof(QualitySettings).GetProperty("renderPipeline", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
    if (qualityPipeline != null && qualityPipeline.CanWrite) qualityPipeline.SetValue(null, pipeline, null);
    UnityEditor.EditorUtility.SetDirty(pipeline);
    UnityEditor.AssetDatabase.SaveAssets();
    UnityEditor.AssetDatabase.Refresh(UnityEditor.ImportAssetOptions.ForceSynchronousImport);
    SetProofPosition();
    return useHdrp ? "hdrp" : "urp";
}

// 首次 pipeline case 保存当前项目设置；后续 case 不得覆盖原始快照。
private static void SnapshotPipeline(string snapshotPath)
{
    if (File.Exists(snapshotPath)) return;
    var qualityProperty = typeof(QualitySettings).GetProperty("renderPipeline", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
    var qualityPipeline = qualityProperty == null ? null : qualityProperty.GetValue(null, null) as UnityEngine.Rendering.RenderPipelineAsset;
    File.WriteAllLines(snapshotPath, new[]
    {
        UnityEditor.AssetDatabase.GetAssetPath(UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline) ?? string.Empty,
        UnityEditor.AssetDatabase.GetAssetPath(qualityPipeline) ?? string.Empty
    });
}

// 结束矩阵时恢复原始 default/quality pipeline，并删除 durable 快照。
private static void RestorePipeline(string snapshotPath)
{
    if (!File.Exists(snapshotPath)) return;
    var lines = File.ReadAllLines(snapshotPath);
    if (lines.Length != 2) throw new InvalidOperationException("Pipeline snapshot is malformed: " + snapshotPath);
    var defaultPipeline = string.IsNullOrEmpty(lines[0]) ? null : UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.RenderPipelineAsset>(lines[0]);
    var qualityPipeline = string.IsNullOrEmpty(lines[1]) ? null : UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.RenderPipelineAsset>(lines[1]);
    UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline = defaultPipeline;
    var qualityProperty = typeof(QualitySettings).GetProperty("renderPipeline", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
    if (qualityProperty != null && qualityProperty.CanWrite) qualityProperty.SetValue(null, qualityPipeline, null);
    UnityEditor.AssetDatabase.SaveAssets();
    File.Delete(snapshotPath);
}

// completion probe 只观察公开场景 Context；调用方在验证后把相机恢复到基线。
private static void SetProofPosition()
{
    var camera = GameObject.Find("Main Camera");
    if (camera == null) throw new InvalidOperationException("Main Camera completion-proof target is missing.");
    camera.transform.position = new Vector3(33000f, 0f, 0f);
    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(camera.scene);
}

private static void EnsureUrpRendererFeature(UnityEngine.Object renderer, string featurePath)
{
    var featureType = RequireOneType(
        "UnityEngine.Rendering.Universal.RenderObjects",
        "UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion");
    UnityEditor.AssetDatabase.DeleteAsset(featurePath);
    var feature = ScriptableObject.CreateInstance(featureType);
    if (feature == null) throw new InvalidOperationException("URP renderer feature could not be created: " + featureType.FullName);
    feature.name = Path.GetFileNameWithoutExtension(featurePath);
    UnityEditor.AssetDatabase.AddObjectToAsset(feature, renderer);
    using (var serialized = new UnityEditor.SerializedObject(renderer))
    {
        var features = serialized.FindProperty("m_RendererFeatures");
        if (features == null || !features.isArray)
            throw new InvalidOperationException("URP renderer feature list is unavailable.");
        features.arraySize = 1;
        features.GetArrayElementAtIndex(0).objectReferenceValue = feature;
        var map = serialized.FindProperty("m_RendererFeatureMap");
        if (map != null)
        {
            string guid;
            long localId;
            UnityEditor.AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out guid, out localId);
            map.arraySize = 1;
            map.GetArrayElementAtIndex(0).longValue = localId;
        }
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }
    UnityEditor.EditorUtility.SetDirty(renderer);
}

private static void EnsureGlobalVolume(string profilePath, bool hdrp)
{
    var profileType = RequireType("UnityEngine.Rendering.VolumeProfile");
    var volumeType = RequireType("UnityEngine.Rendering.Volume");
    var profile = CreateAsset(profileType, profilePath);
    var bloomType = RequireType(hdrp
        ? "UnityEngine.Rendering.HighDefinition.Bloom"
        : "UnityEngine.Rendering.Universal.Bloom");
    var addEffect = profileType.GetMethod("Add", new[] { typeof(Type), typeof(bool) });
    if (addEffect == null) throw new InvalidOperationException("VolumeProfile.Add(Type,bool) is unavailable.");
    var effect = addEffect.Invoke(profile, new object[] { bloomType, true }) as UnityEngine.Object;
    if (effect == null) throw new InvalidOperationException("VolumeProfile.Add did not create a Bloom override.");
    // VolumeProfile.Add 只更新内存列表；显式持久化 subasset，Scene Reload 后引用才不会变成 destroyed object。
    if (!UnityEditor.AssetDatabase.Contains(effect)) UnityEditor.AssetDatabase.AddObjectToAsset(effect, profile);
    UnityEditor.EditorUtility.SetDirty(effect);
    UnityEditor.EditorUtility.SetDirty(profile);
    UnityEditor.AssetDatabase.SaveAssets();
    profile = UnityEditor.AssetDatabase.LoadAssetAtPath<ScriptableObject>(profilePath);
    if (profile == null || !UnityEditor.AssetDatabase.LoadAllAssetsAtPath(profilePath).Any(asset => asset != null && bloomType.IsInstanceOfType(asset)))
        throw new InvalidOperationException("Bloom override was not persisted in the VolumeProfile asset.");
    var target = GameObject.Find("GlobalVolume") ?? new GameObject("GlobalVolume");
    var volume = target.GetComponent(volumeType) ?? target.AddComponent(volumeType);
    var isGlobal = volumeType.GetProperty("isGlobal");
    if (isGlobal != null && isGlobal.CanWrite) isGlobal.SetValue(volume, true, null);
    var sharedProfile = volumeType.GetProperty("sharedProfile");
    if (sharedProfile != null && sharedProfile.CanWrite) sharedProfile.SetValue(volume, profile, null);
    else
    {
        var sharedProfileField = volumeType.GetField("sharedProfile", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (sharedProfileField == null) throw new InvalidOperationException("Volume.sharedProfile is unavailable.");
        sharedProfileField.SetValue(volume, profile);
    }
    // HDRP Tool 修改 scene-session profile；显式触发 shared template clone，并验证 14.0.12 internal owner。
    var profileProperty = volumeType.GetProperty("profile", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
    var sessionProfile = profileProperty == null ? null : profileProperty.GetValue(volume, null) as UnityEngine.Object;
    var internalProfileField = volumeType.GetField("m_InternalProfile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    if (sessionProfile == null || internalProfileField == null || !ReferenceEquals(internalProfileField.GetValue(volume), sessionProfile))
        throw new InvalidOperationException("Volume.profile did not create the required scene-session internal profile.");
}

private static Type RequireOneType(params string[] names)
{
    foreach (var name in names)
    {
        try { return RequireType(name); }
        catch (InvalidOperationException) { }
    }
    throw new InvalidOperationException("None of the required types are available: " + string.Join(", ", names));
}

private static Type RequireType(string fullName)
{
    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
    {
        var type = assembly.GetType(fullName, false);
        if (type != null) return type;
    }
    throw new InvalidOperationException("Pipeline type not found: " + fullName);
}

private static ScriptableObject CreateAsset(Type type, string path)
{
    if (UnityEditor.AssetDatabase.LoadMainAssetAtPath(path) != null) UnityEditor.AssetDatabase.DeleteAsset(path);
    var asset = ScriptableObject.CreateInstance(type);
    if (asset == null) throw new InvalidOperationException("Could not create pipeline object: " + type.FullName);
    UnityEditor.AssetDatabase.CreateAsset(asset, path);
    return asset;
}

private static UnityEngine.Rendering.RenderPipelineAsset CreatePipelineAsset(Type type, string path)
{
    var asset = CreateAsset(type, path) as UnityEngine.Rendering.RenderPipelineAsset;
    if (asset == null) throw new InvalidOperationException("Type is not a RenderPipelineAsset: " + type.FullName);
    return asset;
}
