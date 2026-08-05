#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityCli
{
    /// <summary>编译事件唯一订阅点；回调只采样并立即交给 durable coordinator。</summary>
    internal static class UnityCliCompilationEventBroker
    {
        /// <summary>跨 domain reload 连续递增的编译事件序列，durable 存于 Library/UnityCli。</summary>
        private static string SequencePath()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.Combine(projectRoot, "Library", "UnityCli", "compile-event-sequence.txt");
        }

        /// <summary>读取 durable 序列；文件缺失视为 0，损坏立即失败（不得静默继续）。</summary>
        internal static long CurrentCompilationEventSequence()
        {
            var path = SequencePath();
            if (!File.Exists(path)) return 0;
            var text = File.ReadAllText(path).Trim();
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var current) || current < 0 || text != current.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("Compilation event sequence sidecar is corrupt.");
            return current;
        }

        /// <summary>分配下一个 durable 序列并立即落盘，reload 后从这里续接。</summary>
        internal static long NextCompilationEventSequence()
        {
            var next = CurrentCompilationEventSequence() + 1;
            var path = SequencePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, next.ToString(CultureInfo.InvariantCulture));
            return next;
        }

        internal sealed class Snapshot
        {
            public long Sequence;
            public long Cycle;
            public bool Started;
            public bool Finished;
            public DateTime StartedAtUtc;
            public DateTime FinishedAtUtc;
        }

        private static bool initialized;
        private static long sequence;
        private static long cycle;
        private static readonly Snapshot snapshot = new Snapshot();

        internal static void Initialize()
        {
            if (initialized) return;
            sequence = CurrentCompilationEventSequence();
            cycle = UnityCliBridge.UnityCliCompileCoordinator.CurrentOwnedCycle();
            snapshot.Sequence = sequence;
            snapshot.Cycle = cycle;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            initialized = true;
        }

        internal static void BeforeReload()
        {
            // reload 前先移除唯一订阅，避免旧 domain 回调写入新 coordinator。
            Shutdown();
        }

        internal static void Shutdown()
        {
            if (!initialized) return;
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            initialized = false;
        }

        internal static void Tick() { }

        internal static Snapshot ReadSnapshot()
        {
            return new Snapshot { Sequence = snapshot.Sequence, Cycle = snapshot.Cycle, Started = snapshot.Started, Finished = snapshot.Finished, StartedAtUtc = snapshot.StartedAtUtc, FinishedAtUtc = snapshot.FinishedAtUtc };
        }

        private static void OnCompilationStarted(object context)
        {
            sequence = NextCompilationEventSequence();
            // A start sequence is globally unique, so it is also a durable cycle identity.
            cycle = sequence;
            snapshot.Sequence = sequence;
            snapshot.Cycle = cycle;
            snapshot.Started = true;
            snapshot.Finished = false;
            snapshot.StartedAtUtc = DateTime.UtcNow;
            UnityCliBridge.UnityCliCompileCoordinator.OnCompilationStarted(snapshot.Cycle, snapshot.Sequence, snapshot.StartedAtUtc);
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            sequence = NextCompilationEventSequence();
            snapshot.Sequence = sequence;
            UnityCliBridge.UnityCliCompileCoordinator.OnAssemblyCompilationFinished(snapshot.Cycle, snapshot.Sequence, assemblyPath, ToDiagnostics(assemblyPath, messages));
        }

        private static void OnCompilationFinished(object context)
        {
            sequence = NextCompilationEventSequence();
            snapshot.Sequence = sequence;
            snapshot.Finished = true;
            snapshot.Started = false;
            snapshot.FinishedAtUtc = DateTime.UtcNow;
            UnityCliBridge.UnityCliCompileCoordinator.OnCompilationFinished(snapshot.Cycle, snapshot.Sequence, snapshot.FinishedAtUtc);
        }

        private static List<Dictionary<string, object>> ToDiagnostics(string assembly, CompilerMessage[] messages)
        {
            var result = new List<Dictionary<string, object>>();
            if (messages == null) return result;
            foreach (var message in messages)
            {
                // severity 直接来自 Unity enum；code 缺失时保持 null，不能靠文本猜测。
                result.Add(new Dictionary<string, object>
                {
                    { "assembly", assembly ?? string.Empty }, { "severity", message.type == CompilerMessageType.Error ? "error" : (message.type == CompilerMessageType.Warning ? "warning" : "info") },
                    { "code", string.IsNullOrEmpty(message.message) ? null : ExtractCode(message.message) }, { "message", message.message ?? string.Empty },
                    { "file", message.file ?? string.Empty }, { "line", (long)Math.Max(0, message.line) }, { "column", (long)Math.Max(0, message.column) }
                });
            }
            return result;
        }

        private static string ExtractCode(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            for (var index = 0; index + 6 <= message.Length; index++)
            {
                if (message[index] != 'C' || message[index + 1] != 'S') continue;
                var digits = 0;
                while (index + 2 + digits < message.Length && char.IsDigit(message[index + 2 + digits])) digits++;
                if (digits == 4) return message.Substring(index, 6);
            }
            return null;
        }
    }
}
#endif
