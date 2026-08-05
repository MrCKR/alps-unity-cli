public static object Run(string argsJson)
{
    var snapshotPath = Path.Combine(Directory.GetCurrentDirectory(), "Library", "UnityCliMatrixPlayModeOptions.txt");
    var action = argsJson ?? string.Empty;

    if (action.IndexOf("restore", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        var hadSnapshot = File.Exists(snapshotPath);
        if (hadSnapshot)
        {
            var lines = File.ReadAllLines(snapshotPath);
            if (lines.Length != 2) throw new InvalidOperationException("PlayMode options snapshot is malformed: " + snapshotPath);
            var originalEnabled = bool.Parse(lines[0]);
            var originalOptions = (UnityEditor.EnterPlayModeOptions)int.Parse(lines[1], System.Globalization.CultureInfo.InvariantCulture);
            UnityEditor.EditorSettings.enterPlayModeOptions = originalOptions;
            UnityEditor.EditorSettings.enterPlayModeOptionsEnabled = originalEnabled;
            File.Delete(snapshotPath);
        }
        SetProofPosition();
        return new Dictionary<string, object>
        {
            { "action", "restore" },
            { "ready", true },
            { "hadSnapshot", hadSnapshot }
        };
    }

    // 快照保存在 Library，runner 或 Editor 进程中断后仍能由下一次 initial restore 恢复。
    if (!File.Exists(snapshotPath))
    {
        var snapshot = UnityEditor.EditorSettings.enterPlayModeOptionsEnabled.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + Environment.NewLine
            + ((int)UnityEditor.EditorSettings.enterPlayModeOptions).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var temporaryPath = snapshotPath + ".tmp";
        File.WriteAllText(temporaryPath, snapshot);
        if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
        File.Move(temporaryPath, snapshotPath);
    }

    UnityEditor.EditorSettings.enterPlayModeOptions = UnityEditor.EnterPlayModeOptions.DisableDomainReload;
    UnityEditor.EditorSettings.enterPlayModeOptionsEnabled = true;
    SetProofPosition();
    return new Dictionary<string, object>
    {
        { "action", "enable" },
        { "ready", true },
        { "enabled", true },
        { "options", UnityEditor.EditorSettings.enterPlayModeOptions.ToString() }
    };
}

// completion probe 以场景状态证明设置脚本已经同步执行；fixture 随后恢复相机位置。
private static void SetProofPosition()
{
    var camera = GameObject.Find("Main Camera");
    if (camera == null) throw new InvalidOperationException("Main Camera completion-proof target is missing.");
    camera.transform.position = new Vector3(34000f, 0f, 0f);
    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(camera.scene);
}
