#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;

namespace UnityCli
{
    /// <summary>编译事件唯一订阅点；回调只采样并立即交给 durable coordinator。</summary>
    internal static class UnityCliCompilationEventBroker
    {
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
            sequence = UnityCliBridge.CurrentCompilationEventSequence();
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
            sequence = UnityCliBridge.NextCompilationEventSequence();
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
            sequence = UnityCliBridge.NextCompilationEventSequence();
            snapshot.Sequence = sequence;
            UnityCliBridge.UnityCliCompileCoordinator.OnAssemblyCompilationFinished(snapshot.Cycle, snapshot.Sequence, assemblyPath, ToDiagnostics(assemblyPath, messages));
        }

        private static void OnCompilationFinished(object context)
        {
            sequence = UnityCliBridge.NextCompilationEventSequence();
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
