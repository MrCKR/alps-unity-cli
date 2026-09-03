#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// editor/console 域 IUnityCliTool 共享 helper:编辑器状态、PlayMode 控制、
    /// 菜单执行、Console 读取、标签/层管理。仅供 editor 域自包含工具复用。
    /// </summary>
    public static class EditorToolSupport
    {
        /// <summary>拦截编译/导入/构建/PlayMode 过渡期的写入请求。</summary>
        public static void EnsureEditorNotBusy(string command)
        {
            if (EditorApplication.isCompiling)
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_COMPILING", "Unity Editor is compiling scripts.", "Wait for Unity to finish compiling and retry.", transient: true, retryAfterMs: 5000);
            if (EditorApplication.isUpdating)
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_IMPORTING", "Unity Editor is importing assets.", "Wait for Unity to finish importing and retry.", transient: true, retryAfterMs: 5000);
            if (UnityCliBridge.IsPlayModeChanging())
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_BUSY", "Unity Editor is entering or exiting Play mode.", "Wait for Play mode transition to complete and retry.", transient: true, retryAfterMs: 3000);
            if (BuildPipeline.isBuildingPlayer)
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_BUSY", "Unity Editor is building the player.", "Wait for the build to complete and retry.", transient: true, retryAfterMs: 10000);
        }

        /// <summary>构造编辑器状态 DTO。</summary>
        public static EditorStateInfo BuildEditorState()
        {
            var scene = SceneManager.GetActiveScene();
            var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            return new EditorStateInfo
            {
                PlayMode = new PlayModeInfo { IsPlaying = EditorApplication.isPlaying, IsPaused = EditorApplication.isPaused, IsPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode },
                Busy = new BusyInfo { IsBusy = EditorApplication.isCompiling || EditorApplication.isUpdating || UnityCliBridge.IsPlayModeChanging() || BuildPipeline.isBuildingPlayer, Compiling = EditorApplication.isCompiling, Updating = EditorApplication.isUpdating, Building = BuildPipeline.isBuildingPlayer, PlayModeChanging = UnityCliBridge.IsPlayModeChanging() },
                ActiveScene = new ActiveSceneInfo { Name = scene.name, Path = scene.path, IsLoaded = scene.isLoaded, IsDirty = scene.isDirty },
                PrefabStageOpen = prefabStage != null,
                PrefabStagePath = prefabStage != null ? prefabStage.assetPath : null
            };
        }

        /// <summary>按 raw LogEntries 索引向后扫描；页满或预算耗尽都返回指向下一 raw 位置的 sentinel。</summary>
        public static ConsoleReadPage ReadConsolePage(string scope, string typeFilter, string textFilter, int pageSize, string cursor, UnityCliRequestBudget budget)
        {
            if (budget == null) throw new ArgumentNullException("budget");
            budget.CheckpointOrThrow();
            var generation = UnityCliReadGeneration.Session(scope);
            var entryType = FindConsoleType("UnityEditor.LogEntry", budget);
            var logEntries = FindConsoleType("UnityEditor.LogEntries", budget);
            if (entryType == null || logEntries == null)
            {
                if (budget.IsExhausted) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                return ConsoleReadPage.Empty(generation, budget.Usage());
            }
            if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var getEntry = logEntries.GetMethod("GetEntryInternal", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var countMethod = logEntries.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (getEntry == null || countMethod == null) return ConsoleReadPage.Empty(generation, budget.Usage());
            if (!budget.TryConsumeGetter() || !budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var rawCount = Convert.ToInt32(countMethod.Invoke(null, null), CultureInfo.InvariantCulture);
            var upperBound = rawCount - 1;
            var nextIndex = upperBound;
            string expectedTopFingerprint = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                string position;
                if (!UnityCliContinuationToken.TryParse(cursor, scope, generation, out position)) throw StaleConsoleCursor();
                var parts = position.Split(new[] { ':' }, 3);
                if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out upperBound)
                    || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out nextIndex)) throw StaleConsoleCursor();
                expectedTopFingerprint = string.IsNullOrEmpty(parts[2]) ? null : parts[2];
                if (upperBound < 0 || nextIndex < 0 || nextIndex > upperBound || rawCount <= upperBound) throw StaleConsoleCursor();
            }
            if (rawCount == 0) return ConsoleReadPage.Empty(generation, budget.Usage());

            ConsoleFields fields;
            try { fields = ConsoleFields.Create(entryType, budget); }
            catch (UnityCliRequestBudgetExceededException)
            {
                var sentinel = cursor ?? CreateConsoleCursor(scope, generation, upperBound, upperBound, string.Empty);
                return ConsoleReadPage.TruncatedPage(rawCount, sentinel, generation, budget.Usage());
            }
            var entry = Activator.CreateInstance(entryType);
            string topFingerprint = null;
            if (!TryReadConsoleEntry(getEntry, entry, upperBound, fields, budget, out var top))
                return ConsoleReadPage.TruncatedPage(rawCount, cursor ?? CreateConsoleCursor(scope, generation, upperBound, upperBound, string.Empty), generation, budget.Usage());
            topFingerprint = ConsoleFingerprint(top);
            if (expectedTopFingerprint != null && !string.Equals(expectedTopFingerprint, topFingerprint, StringComparison.Ordinal)) throw StaleConsoleCursor();

            var includeAllTypes = string.IsNullOrEmpty(typeFilter) || string.Equals(typeFilter, "all", StringComparison.OrdinalIgnoreCase);
            var items = new List<ConsoleEntry>();
            var scanIndex = nextIndex;
            while (scanIndex >= 0 && items.Count < pageSize)
            {
                ConsoleEntry item;
                if (scanIndex == upperBound) item = top;
                else if (!TryReadConsoleEntry(getEntry, entry, scanIndex, fields, budget, out item)) break;
                var currentIndex = scanIndex;
                scanIndex--;
                if (!includeAllTypes && !string.Equals(item.Type, typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(textFilter)
                    && item.Message.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) < 0
                    && item.File.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!budget.TryReserveSerializedUtf8(item.Message) || !budget.TryReserveSerializedUtf8(item.File))
                {
                    scanIndex = currentIndex;
                    break;
                }
                items.Add(item);
            }

            var hasRawRemainder = scanIndex >= 0;
            var nextCursor = hasRawRemainder ? CreateConsoleCursor(scope, generation, upperBound, scanIndex, topFingerprint) : null;
            return new ConsoleReadPage
            {
                Items = items,
                RawEntryCount = rawCount,
                NextCursor = nextCursor,
                Truncated = hasRawRemainder,
                Generation = generation,
                Budget = budget.Usage()
            };
        }

        private static bool TryReadConsoleEntry(MethodInfo getEntry, object target, int rawIndex, ConsoleFields fields, UnityCliRequestBudget budget, out ConsoleEntry result)
        {
            result = null;
            if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeReflectionCall()) return false;
            getEntry.Invoke(null, new object[] { rawIndex, target });
            if (!budget.TryConsumeGetter() || !budget.TryConsumeReflectionCall()) return false;
            var mode = Convert.ToInt32(fields.Mode.GetValue(target), CultureInfo.InvariantCulture);
            if (!budget.TryConsumeGetter() || !budget.TryConsumeReflectionCall()) return false;
            var message = Convert.ToString(fields.Message.GetValue(target), CultureInfo.InvariantCulture) ?? string.Empty;
            if (!budget.TryConsumeGetter() || !budget.TryConsumeReflectionCall()) return false;
            var file = Convert.ToString(fields.File.GetValue(target), CultureInfo.InvariantCulture) ?? string.Empty;
            result = new ConsoleEntry { RawIndex = rawIndex, Type = ConsoleLevel(mode), Message = message, File = file, Count = 1L };
            return true;
        }

        private static Type FindConsoleType(string fullName, UnityCliRequestBudget budget)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!budget.TryConsumeReflectionCall()) return null;
                Type type = null;
                try { type = assembly.GetType(fullName, false); } catch { }
                if (type != null) return type;
            }
            return null;
        }

        private static string CreateConsoleCursor(string scope, string generation, int upperBound, int nextIndex, string topFingerprint)
        {
            return UnityCliContinuationToken.Create(scope, generation,
                upperBound.ToString(CultureInfo.InvariantCulture) + ":" + nextIndex.ToString(CultureInfo.InvariantCulture) + ":" + (topFingerprint ?? string.Empty));
        }

        private static string ConsoleFingerprint(ConsoleEntry entry)
        {
            return UnityCliContinuationToken.Fingerprint(entry.RawIndex.ToString(CultureInfo.InvariantCulture) + "\0" + entry.Type + "\0" + entry.Message + "\0" + entry.File);
        }

        private static UnityCliBridge.BridgeCommandException StaleConsoleCursor()
        {
            return new UnityCliBridge.BridgeCommandException("E_CONTINUATION_STALE", "The Console continuation no longer identifies the same raw log generation.", "Start a new console.get read without cursor.");
        }

        private sealed class ConsoleFields
        {
            internal FieldInfo Mode;
            internal FieldInfo Message;
            internal FieldInfo File;

            internal static ConsoleFields Create(Type entryType, UnityCliRequestBudget budget)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var mode = entryType.GetField("mode", flags);
                if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var message = entryType.GetField("message", flags);
                if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var file = entryType.GetField("file", flags);
                if (mode == null || message == null || file == null) throw new UnityCliBridge.BridgeCommandException("E_CONSOLE_API_UNAVAILABLE", "Unity Console entry fields are unavailable in this Editor generation.", "Use a supported Unity Editor version.");
                return new ConsoleFields { Mode = mode, Message = message, File = file };
            }
        }


        // Unity 2022 LogEntry.mode 位布局：kError=1, kAssert=2, kLog=4, kFatal=16, kAssetImportError=64,
        // kAssetImportWarning=128, kScriptingError=256, kScriptingWarning=512, kScriptingException=2048, kScriptingAssertion=4096。
        // 错误/警告/断言来自多个位（脚本与资源导入各有独立位），只查低 3 位会把全部条目误归为 log。
        private static string ConsoleLevel(int mode)
        {
            const int errorBits = (1 << 0) | (1 << 4) | (1 << 6) | (1 << 8) | (1 << 11) | (1 << 16);
            const int warningBits = (1 << 7) | (1 << 9) | (1 << 17);
            const int assertBits = (1 << 1) | (1 << 12);
            if ((mode & errorBits) != 0) return "error";
            if ((mode & assertBits) != 0) return "assert";
            if ((mode & warningBits) != 0) return "warning";
            return "log";
        }
    }

    // ── editor 域 DTO ──

    public sealed class EditorStateInfo
    {
        public PlayModeInfo PlayMode { get; set; }
        public BusyInfo Busy { get; set; }
        public ActiveSceneInfo ActiveScene { get; set; }
        public bool PrefabStageOpen { get; set; }
        public string PrefabStagePath { get; set; }
    }

    public sealed class PlayModeInfo { public bool IsPlaying { get; set; } public bool IsPaused { get; set; } public bool IsPlayingOrWillChangePlaymode { get; set; } }
    public sealed class BusyInfo { public bool IsBusy { get; set; } public bool Compiling { get; set; } public bool Updating { get; set; } public bool Building { get; set; } public bool PlayModeChanging { get; set; } }
    public sealed class ActiveSceneInfo { public string Name { get; set; } public string Path { get; set; } public bool IsLoaded { get; set; } public bool IsDirty { get; set; } }
    public sealed class ConsoleEntry { public long RawIndex { get; set; } public string Type { get; set; } public string Message { get; set; } public string File { get; set; } public long Count { get; set; } }
    public sealed class ConsoleReadPage
    {
        public List<ConsoleEntry> Items { get; set; }
        public long RawEntryCount { get; set; }
        public string NextCursor { get; set; }
        public bool Truncated { get; set; }
        public string Generation { get; set; }
        public UnityCliRequestBudgetUsage Budget { get; set; }

        internal static ConsoleReadPage Empty(string generation, UnityCliRequestBudgetUsage usage)
        {
            return new ConsoleReadPage { Items = new List<ConsoleEntry>(), RawEntryCount = 0L, Truncated = false, Generation = generation, Budget = usage };
        }

        internal static ConsoleReadPage TruncatedPage(long rawEntryCount, string cursor, string generation, UnityCliRequestBudgetUsage usage)
        {
            return new ConsoleReadPage { Items = new List<ConsoleEntry>(), RawEntryCount = rawEntryCount, NextCursor = cursor, Truncated = true, Generation = generation, Budget = usage };
        }
    }
    public sealed class EditorPlannedEffect { public string Type { get; set; } public string Path { get; set; } public string Action { get; set; } public string Name { get; set; } }
    public sealed class PlayModeResult { public bool Changed { get; set; } public bool IsPlaying { get; set; } public bool IsPaused { get; set; } }
    public sealed class MenuResult { public string Path { get; set; } public bool Executed { get; set; } public long ElapsedMs { get; set; } }
    public sealed class TagLayerResult { public string Action { get; set; } public string Name { get; set; } public bool Changed { get; set; } }
}
#endif
