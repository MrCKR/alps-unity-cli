public static object Run(string argsJson)
{
    EnsureFolders("Assets/UnityCLIValidation");
    const string fixtureScenePath = "Assets/UnityCLIValidation/MatrixScene.unity";
    // 调用方先以 scene.load 重载固定基线；这里原位重建，保持 completionProbe 的对象与 scene identity。
    var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
    if (!activeScene.IsValid() || !string.Equals(activeScene.path, fixtureScenePath, StringComparison.Ordinal))
        throw new InvalidOperationException("Live matrix setup requires the fixed MatrixScene baseline.");
    // 截图能力要求输出不存在；每个正向 case 必须清除上一次生成的 artifact。
    if (UnityEditor.AssetDatabase.IsValidFolder("Assets/Screenshots"))
        UnityEditor.AssetDatabase.DeleteAsset("Assets/Screenshots");

    EnsureFolders(
        "Assets/Animations", "Assets/Characters", "Assets/Data", "Assets/Generated", "Assets/Icons",
        "Assets/Imported", "Assets/Materials", "Assets/Matrix Folder", "Assets/Old", "Assets/Physics",
        "Assets/Prefabs", "Assets/Reflections", "Assets/Scenes", "Assets/Scripts", "Assets/Screenshots", "Assets/Screenshots/Surround",
        "Assets/Shaders", "Assets/Shots", "Assets/Skyboxes", "Assets/Textures", "Assets/UI",
        "Assets/UnityCLIValidation", "Assets/VFX");
    // ReflectionProbe bake 必须写入 fresh artifact，避免把前一 case 的相同 digest 误归因给当前请求。
    if (UnityEditor.AssetDatabase.LoadMainAssetAtPath("Assets/Reflections/ReflectionProbe.exr") != null ||
        System.IO.File.Exists(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Assets/Reflections/ReflectionProbe.exr")))
        UnityEditor.AssetDatabase.DeleteAsset("Assets/Reflections/ReflectionProbe.exr");

    var player = EnsurePrimitive("Player", PrimitiveType.Cube);
    EnsureComponent<Rigidbody>(player);
    var animator = EnsureComponent<Animator>(player);
    EnsureComponent<Animation>(player);
    EnsureComponent<FixedJoint>(player);
    var enemy = EnsurePrimitive("Enemy", PrimitiveType.Capsule);
    EnsureComponent<Rigidbody>(enemy);
    EnsureComponent<Animator>(enemy);
    EnsurePrimitive("Cube", PrimitiveType.Cube);
    EnsurePrimitive("Sphere", PrimitiveType.Sphere);
    EnsurePrimitive("Probe", PrimitiveType.Sphere);
    EnsurePrimitive("MyPrimitive", PrimitiveType.Cube);

    var parent = EnsureGameObject("Parent");
    EnsureChild(parent, "Child");
    var root = EnsureGameObject("Root");
    EnsureChild(root, "Child");

    var avatar = EnsureChild(player, "Avatar");
    avatar.transform.localPosition = new Vector3(0f, 0f, 10f);
    EnsureChild(player, "CameraRig");
    EnsureChild(player, "RightHand");
    EnsureChild(player, "Sword");
    EnsureChild(player, "Rig");
    var cameraRig = EnsureRootGameObject("CameraRig");
    var hierarchyCamera = EnsureChild(cameraRig, "MainCamera");
    EnsureComponent<Camera>(hierarchyCamera);
    hierarchyCamera.transform.position = new Vector3(0f, 0f, -10f);
    var generated = EnsureGameObject("Generated");
    EnsureChild(generated, "A");
    EnsureChild(generated, "B");
    EnsureChild(EnsureGameObject("TempRoot"), "GeneratedChild");
    var componentTarget = EnsureGameObject("ComponentTarget");
    EnsureComponent<BoxCollider>(componentTarget);
    EnsureComponent<Rigidbody>(componentTarget);

    var mainCamera = EnsureGameObject("Main Camera");
    EnsureComponent<Camera>(mainCamera);
    mainCamera.tag = "MainCamera";
    EnsureReflectedComponent(mainCamera, "Cinemachine.CinemachineBrain", "Unity.Cinemachine.CinemachineBrain");
    EnsureComponent<Camera>(EnsureGameObject("Camera"));
    var playerCamera = EnsureGameObject("PlayerCam");
    var playerCinemachineCamera = EnsureReflectedComponent(playerCamera, "Cinemachine.CinemachineVirtualCamera", "Unity.Cinemachine.CinemachineCamera");
    EnsureCinemachinePipelineComponent(playerCinemachineCamera, "Cinemachine.CinemachineBasicMultiChannelPerlin", "Unity.Cinemachine.CinemachineBasicMultiChannelPerlin");
    EnsureReflectedComponent(playerCamera, "Cinemachine.CinemachineCollider", "Unity.Cinemachine.CinemachineDecollider");
    var closeupCamera = EnsureGameObject("CloseupCam");
    EnsureReflectedComponent(closeupCamera, "Cinemachine.CinemachineVirtualCamera", "Unity.Cinemachine.CinemachineCamera");
    var alignmentCamera = EnsureGameObject("UnityCliAlignmentCamera");
    EnsureComponent<Camera>(alignmentCamera);
    alignmentCamera.transform.position = new Vector3(0f, 0f, -10f);
    var alignmentProbe = EnsureGameObject("UnityCliAlignmentProbe");
    alignmentProbe.transform.position = Vector3.zero;

    var directionalLight = EnsureGameObject("Directional Light");
    var light = EnsureComponent<Light>(directionalLight);
    light.type = LightType.Directional;
    EnsureComponent<ReflectionProbe>(EnsureGameObject("Reflection Probe"));
    EnsureComponent<LightProbeGroup>(EnsureGameObject("Light Probe Group"));
    EnsureComponent<ReflectionProbe>(EnsureGameObject("ReflectionProbe"));
    EnsureComponent<LightProbeGroup>(EnsureGameObject("LightProbes"));
    var probes = EnsureGameObject("Probes");
    EnsureComponent<LightProbeGroup>(probes);
    EnsureComponent<ReflectionProbe>(probes);
    var directionalLightAlias = EnsureComponent<Light>(EnsureGameObject("DirectionalLight"));
    directionalLightAlias.type = LightType.Directional;

    var effects = EnsureGameObject("Effects");
    var emitter = EnsureGameObject("Emitter");
    emitter.transform.SetParent(effects.transform, false);
    EnsureReflectedComponent(emitter, "UnityEngine.VFX.VisualEffect");
    var newEmitter = EnsureGameObject("NewEmitter");
    newEmitter.transform.SetParent(effects.transform, false);
    var dust = EnsureGameObject("Dust");
    dust.transform.SetParent(effects.transform, false);
    EnsureComponent<ParticleSystem>(dust);
    EnsureComponent<LineRenderer>(EnsureGameObject("Line"));
    EnsureComponent<TrailRenderer>(EnsureGameObject("Trail"));

    var canvasObject = EnsureGameObject("Canvas");
    EnsureComponent<Canvas>(canvasObject).renderMode = RenderMode.ScreenSpaceOverlay;
    EnsureComponent<UnityEngine.UI.CanvasScaler>(canvasObject);
    EnsureComponent<UnityEngine.UI.GraphicRaycaster>(canvasObject);
    var uiRoot = EnsureGameObject("UIRoot");
    uiRoot.transform.SetParent(canvasObject.transform, false);

    EnsureMaterial("Assets/Materials/A.mat", Color.white);
    EnsureMaterial("Assets/Materials/Line.mat", Color.white);
    EnsureMaterial("Assets/Materials/Player.mat", Color.blue);
    EnsureMaterial("Assets/Materials/Probe.mat", Color.gray);
    EnsureMaterial("Assets/Materials/Red.mat", Color.red);
    var skyboxMaterial = EnsureSkyboxMaterial("Assets/Skyboxes/Day.mat");
    RenderSettings.skybox = skyboxMaterial;
    player.GetComponent<Renderer>().sharedMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Player.mat");

    EnsureTexture("Assets/Icons/old.png", Color.yellow);
    EnsureTexture("Assets/Imported/Icon.png", Color.magenta);
    EnsureTexture("Assets/Old/Icon.png", Color.green);
    EnsureTexture("Assets/Textures/Hero.png", Color.blue);
    EnsureTexture("Assets/Textures/Icon.png", Color.white);
    EnsureTexture("Assets/Textures/Old.png", Color.black);

    EnsureText("Assets/Generated/notes.txt", "matrix notes\n");
    EnsureText("Assets/Generated/old.txt", "matrix old\n");
    EnsureText("Assets/Matrix Folder/file name.txt", "matrix quoted path\n");
    EnsureText("Assets/Scripts/Player.cs", "using UnityEngine; public sealed class Player : MonoBehaviour { }\n");
    EnsureText("Assets/Scripts/MyConfig.cs", "using UnityEngine; public sealed class MyConfig : ScriptableObject { public int maxItems; }\n");
    EnsureText("Assets/Shaders/Example.shader", ShaderSource("Matrix/Example"));
    EnsureText("Assets/Shaders/Old.shader", ShaderSource("Matrix/Old"));
    EnsureText("Assets/UI/Main.uss", "#MatrixRoot { color: white; }\n");
    EnsureText("Assets/UI/Main.uxml", "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\"><ui:VisualElement name=\"MatrixRoot\"><ui:Label name=\"label\" text=\"Matrix\" /></ui:VisualElement></ui:UXML>\n");
    EnsureText("Assets/UnityCLIValidation/Matrix.uxml", "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\"><ui:Label name=\"MatrixCacheLabel\" text=\"initial\" /></ui:UXML>\n");

    EnsureAnimationClip("Assets/Animations/Idle.anim");
    EnsureAnimationClip("Assets/Animations/Walk.anim");
    var controller = EnsureAnimatorController("Assets/Animations/Player.controller");
    ConfigureAnimatorController(controller);
    animator.runtimeAnimatorController = controller;
    EnsurePhysicsMaterial("Assets/Physics/Bouncy.physicMaterial");
    EnsureMyConfig("Assets/Data/Config.asset");
    EnsurePanelSettings("Assets/UI/Panel.asset");

    EnsurePrefab("Assets/Prefabs/Player.prefab", player);
    EnsurePrefab("Assets/Prefabs/Enemy.prefab", enemy);
    EnsurePrefab("Assets/UnityCLIValidation/Matrix.prefab", EnsureGameObject("Matrix"));

    // Unity 不允许在未保存的 untitled scene 上 additive 创建场景，先固定主夹具场景路径。
    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(
        UnityEngine.SceneManagement.SceneManager.GetActiveScene(),
        fixtureScenePath);

    EnsureScene("Assets/Scenes/Main.unity");
    EnsureScene("Assets/Scenes/Gameplay.unity");
    EnsureScene("Assets/Scenes/Level2.unity");
    UnityEditor.EditorBuildSettings.scenes = new[]
    {
        new UnityEditor.EditorBuildSettingsScene("Assets/Scenes/Main.unity", true),
        new UnityEditor.EditorBuildSettingsScene("Assets/Scenes/Gameplay.unity", true)
    };

    DeleteOutputs(
        "Assets/Animations/Created.anim", "Assets/Animations/Created.controller",
        "Assets/Generated/created.txt", "Assets/Generated/Sub", "Assets/UnityCliMatrix",
        "Assets/Icons/new.png", "Assets/Materials/A_copy.mat", "Assets/Materials/New.mat", "Assets/New/Icon.png",
        "Assets/Shaders/New.shader", "Assets/Textures/Checker.png", "Assets/Textures/Red.png",
        "Assets/UI/Preview.png", "Assets/VFX/UnityCliCreatedExplosion.vfx",
        "Assets/Shots/game.png", "Assets/Shots/player.png", "Assets/Shots/scene.png");
    DeleteFiles(
        "baseline.raw", "baseline.raw.metadata.json",
        "profile.json", "profile.json.metadata.json",
        "snap1.snap", "snap2.snap",
        "Artifacts/profiler/baseline.raw", "Artifacts/profiler/baseline.raw.metadata.json",
        "Artifacts/profiler/hierarchy.raw", "Artifacts/profiler/hierarchy.raw.metadata.json");

    // helper 已对磁盘资产定向 ImportAsset；全局 Refresh 会在 effect 栈内触发 compile/domain reload 并截断 completion proof。
    UnityEditor.AssetDatabase.SaveAssets();

    ConfigureUiDocument(EnsureGameObject("UIDocument"));
    ConfigureUiDocument(player);

    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), fixtureScenePath);

    // debug.code.execute 通过这一未保存的场景变更提供 request-specific completion proof；调用方随后恢复为 0。
    mainCamera.transform.position = new Vector3(31337f, 0f, 0f);
    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(mainCamera.scene);
    return new Dictionary<string, object>
    {
        { "fixture", "live-matrix" },
        { "ready", true },
        { "player", player.name },
        { "prefab", "Assets/UnityCLIValidation/Matrix.prefab" }
    };
}

private static GameObject EnsureGameObject(string name)
{
    var existing = GameObject.Find(name);
    return existing != null ? existing : new GameObject(name);
}

private static GameObject EnsureRootGameObject(string name)
{
    foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
    {
        if (root.name == name) return root;
    }
    return new GameObject(name);
}

private static GameObject EnsureChild(GameObject parent, string name)
{
    var existing = parent.transform.Find(name);
    if (existing != null) return existing.gameObject;
    var child = new GameObject(name);
    child.transform.SetParent(parent.transform, false);
    return child;
}

private static GameObject EnsurePrimitive(string name, PrimitiveType type)
{
    var existing = GameObject.Find(name);
    if (existing != null && existing.GetComponent<Renderer>() != null) return existing;
    if (existing != null) UnityEngine.Object.DestroyImmediate(existing);
    var created = GameObject.CreatePrimitive(type);
    created.name = name;
    return created;
}

private static T EnsureComponent<T>(GameObject target) where T : Component
{
    var existing = target.GetComponent<T>();
    return existing != null ? existing : target.AddComponent<T>();
}

private static Component EnsureReflectedComponent(GameObject target, params string[] typeNames)
{
    var componentType = FindComponentType(typeNames);
    if (componentType == null) return null;
    var existing = target.GetComponent(componentType);
    return existing != null ? existing : target.AddComponent(componentType);
}

// Cinemachine 2.x 把 pipeline component 放在隐藏子对象；必须通过 camera API 创建，不能直接 AddComponent。
private static Component EnsureCinemachinePipelineComponent(Component camera, params string[] typeNames)
{
    if (camera == null) return null;
    var componentType = FindComponentType(typeNames);
    if (componentType == null) return null;
    var methods = camera.GetType().GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
    var get = methods.FirstOrDefault(method => method.Name == "GetCinemachineComponent" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0);
    var add = methods.FirstOrDefault(method => method.Name == "AddCinemachineComponent" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0);
    if (get == null || add == null) throw new InvalidOperationException("Cinemachine camera does not expose its generic pipeline API.");
    var existing = get.MakeGenericMethod(componentType).Invoke(camera, null) as Component;
    if (existing != null) return existing;
    add.MakeGenericMethod(componentType).Invoke(camera, null);
    var observed = get.MakeGenericMethod(componentType).Invoke(camera, null) as Component;
    if (observed == null) throw new InvalidOperationException("Cinemachine Noise pipeline fixture could not be created.");
    return observed;
}

private static Type FindComponentType(params string[] typeNames)
{
    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
    {
        foreach (var typeName in typeNames)
        {
            var componentType = assembly.GetType(typeName, false);
            if (componentType != null && typeof(Component).IsAssignableFrom(componentType)) return componentType;
        }
    }
    return null;
}

private static void EnsureFolders(params string[] paths)
{
    foreach (var path in paths)
    {
        var current = "Assets";
        foreach (var part in path.Substring("Assets/".Length).Split('/'))
        {
            var next = current + "/" + part;
            if (!UnityEditor.AssetDatabase.IsValidFolder(next)) UnityEditor.AssetDatabase.CreateFolder(current, part);
            current = next;
        }
    }
}

private static void EnsureText(string path, string content)
{
    var absolute = Path.Combine(Directory.GetCurrentDirectory(), path.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(absolute));
    if (File.Exists(absolute) && File.ReadAllText(absolute, System.Text.Encoding.UTF8) == content) return;
    File.WriteAllText(absolute, content, new System.Text.UTF8Encoding(false));
    UnityEditor.AssetDatabase.ImportAsset(path, UnityEditor.ImportAssetOptions.ForceSynchronousImport);
}

private static string ShaderSource(string name)
{
    return "Shader \"" + name + "\" { SubShader { Pass { } } }\n";
}

private static void EnsureTexture(string path, Color color)
{
    var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
    var pixels = Enumerable.Repeat(color, 16).ToArray();
    texture.SetPixels(pixels);
    texture.Apply();
    var absolute = Path.Combine(Directory.GetCurrentDirectory(), path.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(absolute));
    File.WriteAllBytes(absolute, texture.EncodeToPNG());
    UnityEngine.Object.DestroyImmediate(texture);
    UnityEditor.AssetDatabase.ImportAsset(path, UnityEditor.ImportAssetOptions.ForceSynchronousImport);
}

private static void EnsureMaterial(string path, Color color)
{
    if (UnityEditor.AssetDatabase.LoadMainAssetAtPath(path) != null) UnityEditor.AssetDatabase.DeleteAsset(path);
    var shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Unlit/Color");
    var material = new Material(shader) { color = color };
    UnityEditor.AssetDatabase.CreateAsset(material, path);
}

// graphics skybox property 夹具必须是可写 Assets/ 资产，并公开 _Exposure float 属性。
private static Material EnsureSkyboxMaterial(string path)
{
    if (UnityEditor.AssetDatabase.LoadMainAssetAtPath(path) != null) UnityEditor.AssetDatabase.DeleteAsset(path);
    var shader = Shader.Find("Skybox/Procedural") ?? Shader.Find("Skybox/6 Sided");
    if (shader == null) throw new InvalidOperationException("Writable skybox shader fixture is unavailable.");
    var material = new Material(shader);
    if (material.HasProperty("_Exposure")) material.SetFloat("_Exposure", 1f);
    UnityEditor.AssetDatabase.CreateAsset(material, path);
    return material;
}

private static void EnsureAnimationClip(string path)
{
    if (UnityEditor.AssetDatabase.LoadMainAssetAtPath(path) != null) UnityEditor.AssetDatabase.DeleteAsset(path);
    UnityEditor.AssetDatabase.CreateAsset(new AnimationClip(), path);
}

private static RuntimeAnimatorController EnsureAnimatorController(string path)
{
    if (UnityEditor.AssetDatabase.LoadMainAssetAtPath(path) != null) UnityEditor.AssetDatabase.DeleteAsset(path);
    return UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(path);
}

private static void ConfigureAnimatorController(RuntimeAnimatorController runtimeController)
{
    var controller = runtimeController as UnityEditor.Animations.AnimatorController;
    if (controller == null) throw new InvalidOperationException("AnimatorController fixture creation failed.");
    controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
    var stateMachine = controller.layers[0].stateMachine;
    stateMachine.AddState("Idle");
    stateMachine.AddState("Walk");
    var move = stateMachine.AddState("Move");
    var blendTree = new UnityEditor.Animations.BlendTree
    {
        name = "Move",
        blendType = UnityEditor.Animations.BlendTreeType.Simple1D,
        blendParameter = "Speed"
    };
    UnityEditor.AssetDatabase.AddObjectToAsset(blendTree, controller);
    move.motion = blendTree;
    controller.AddLayer("Gameplay");
}

private static void EnsurePhysicsMaterial(string path)
{
    if (UnityEditor.AssetDatabase.LoadMainAssetAtPath(path) != null) UnityEditor.AssetDatabase.DeleteAsset(path);
    UnityEditor.AssetDatabase.CreateAsset(new PhysicMaterial("Bouncy") { bounciness = 0.8f }, path);
}

private static void EnsureMyConfig(string path)
{
    if (UnityEditor.AssetDatabase.LoadMainAssetAtPath(path) != null) UnityEditor.AssetDatabase.DeleteAsset(path);
    // 新写入 MyConfig.cs 的首次 setup 仍在编译前；专属 fixture 会在 ready 后创建该 asset。
    var config = ScriptableObject.CreateInstance("MyConfig");
    if (config == null) return;
    UnityEditor.AssetDatabase.CreateAsset(config, path);
}

private static void ConfigureUiDocument(GameObject target)
{
    var uiDocument = EnsureComponent<UnityEngine.UIElements.UIDocument>(target);
    uiDocument.enabled = false;
    uiDocument.panelSettings = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>("Assets/UI/Panel.asset");
    uiDocument.visualTreeAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>("Assets/UI/Main.uxml");
    uiDocument.enabled = true;
    UnityEditor.EditorUtility.SetDirty(uiDocument);
}

private static void EnsurePanelSettings(string path)
{
    if (UnityEditor.AssetDatabase.LoadMainAssetAtPath(path) != null) UnityEditor.AssetDatabase.DeleteAsset(path);
    UnityEditor.AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<UnityEngine.UIElements.PanelSettings>(), path);
}

private static void EnsurePrefab(string path, GameObject source)
{
    UnityEditor.PrefabUtility.SaveAsPrefabAsset(source, path);
}

private static void EnsureScene(string path)
{
    if (UnityEditor.AssetDatabase.LoadMainAssetAtPath(path) != null) UnityEditor.AssetDatabase.DeleteAsset(path);
    var previous = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
    var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
        UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
        UnityEditor.SceneManagement.NewSceneMode.Additive);
    UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);
    new GameObject(Path.GetFileNameWithoutExtension(path));
    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, path);
    UnityEngine.SceneManagement.SceneManager.SetActiveScene(previous);
    UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
}

private static void DeleteOutputs(params string[] paths)
{
    foreach (var path in paths)
    {
        UnityEditor.AssetDatabase.DeleteAsset(path);
        var absolute = Path.Combine(Directory.GetCurrentDirectory(), path.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(absolute)) File.Delete(absolute);
        else if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
        if (File.Exists(absolute + ".meta")) File.Delete(absolute + ".meta");
    }
}

private static void DeleteFiles(params string[] paths)
{
    foreach (var path in paths)
    {
        var absolute = Path.Combine(Directory.GetCurrentDirectory(), path.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(absolute)) File.Delete(absolute);
    }
}
