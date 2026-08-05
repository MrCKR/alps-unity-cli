public static object Run(string argsJson)
{
    const string assetPath = "Assets/UnityCLIValidation/MatrixReloadProbe.cs";
    var absolutePath = Path.Combine(Directory.GetCurrentDirectory(), assetPath.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
    var token = DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    var source = "#if UNITY_EDITOR\nnamespace UnityCliValidation { internal static class MatrixReloadProbe { internal const long Token = "
        + token + "L; } }\n#endif\n";
    File.WriteAllText(absolutePath, source, new System.Text.UTF8Encoding(false));

    // 文件仅落盘不 import；后续 asset.refresh 必须独立证明 domain reload 恢复。
    var camera = GameObject.Find("Main Camera");
    if (camera == null) throw new InvalidOperationException("Main Camera completion-proof target is missing.");
    camera.transform.position = new Vector3(35000f, 0f, 0f);
    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(camera.scene);
    return new Dictionary<string, object>
    {
        { "assetPath", assetPath },
        { "token", token }
    };
}
