#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityCli.Protocol;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        internal static partial class UnityCliPackageResolveCoordinator
        {
            private const string ActiveScope = "package-resolve";
            private static readonly Dictionary<string, ActivePackageOperationRecord> TerminalRoots = new Dictionary<string, ActivePackageOperationRecord>(StringComparer.Ordinal);

            private sealed class PackageActiveDecision { internal bool Execute; internal object TerminalEnvelope; }
            private sealed class ActivePackageOperationRecord
            {
                internal string RequestId; internal string EditorSessionId; internal DateTimeOffset AbsoluteDeadlineUtc;
                internal string State; internal string Phase; internal DateTimeOffset? AcceptedAt; internal DateTimeOffset? StartedAt;
                internal object TerminalEnvelope; internal Dictionary<string, object> PackageOperation;
            }

            /// <summary>每帧只推进当前全局 lease owner；status/read 不进入 mutation lane。</summary>
            private static void TickPackageRoots()
            {
                var roots = OpenRoots().OrderBy(root => root.AcceptedAt).ThenBy(root => root.RequestId, StringComparer.Ordinal).ToList();
                foreach (var root in roots.Where(root => !string.Equals(root.EditorSessionId, EditorSessionId(), StringComparison.Ordinal)))
                {
                    if (PackageEffectStarted(root.RequestId)) TerminalTimeout(root, "The Editor session changed after the Package root crossed effect_started.");
                    else TerminalError(root, "E_EDITOR_SESSION_REPLACED", "The Editor session changed before Package files or Resolve began.", null);
                }
                foreach (var root in roots.Where(root => string.Equals(root.EditorSessionId, EditorSessionId(), StringComparison.Ordinal) && !IsTerminal(root)))
                    if (UnityCliSessionMutationLease.IsOwnedBy(root.RequestId)) TickRoot(root);
            }

            private static void TickRoot(ActivePackageOperationRecord root)
            {
                var phase = Text(root.PackageOperation, "phase");
                var expired = DateTimeOffset.UtcNow >= root.AbsoluteDeadlineUtc;
                if (expired && !PackageEffectStarted(root.RequestId)) { TerminalError(root, "E_EXECUTION_TIMEOUT", "Package root did not begin before its immutable deadline.", null); return; }
                if (phase == "awaiting_files") return;
                if (phase == "files_committing")
                {
                    if (expired)
                    {
                        TerminalTimeout(root, FileExecutorAlive(root.PackageOperation)
                            ? "Package file executor did not settle before the immutable deadline."
                            : "Package file executor exited without fresh proof before the immutable deadline.");
                    }
                    return;
                }
                if (expired && (phase == "files_proven" || phase == "resolve_dispatching")) { TerminalTimeout(root, "Package root reached its immutable deadline before Resolve dispatch completed."); return; }
                if (phase == "files_proven") { DispatchResolve(root); return; }
                if (phase == "resolve_returned") { StartList(root, false); return; }
                if (phase == "reload_observed") { if (!UnityBusy()) StartList(root, true); return; }
                if (phase == "list_succeeded")
                {
                    ObserveStablePackageInfo(root);
                    if (expired && !UnityBusy())
                    {
                        var current = RequiredRoot(root.RequestId);
                        if (!IsTerminal(current) && Long(current.PackageOperation, "stableFrames") == 0) TerminalTimeout(current, "PackageInfo, manifest, or lock readback did not converge after explicit List success.");
                    }
                    return;
                }
                if (phase == "list_pending") ObserveList(root);
            }

            /// <summary>先持久化 dispatching，再唯一调用 void Resolve；返回后才允许建立 List fence。</summary>
            private static void DispatchResolve(ActivePackageOperationRecord root)
            {
                if (UnityBusy()) return;
                // PlayMode 下 PackageManager.Resolve 无法完成；同步调用会永久阻塞主线程（heartbeat/queue 全停）。
                // pump 每帧重试：PlayMode 期间不 dispatch，immutable deadline 到期由 TickRoot 判超时，主线程保持可用。
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                if (!Same(Text(root.PackageOperation, "expectedManifestSha256"), HashFile(ManifestPath())))
                {
                    TerminalTimeout(root, "manifest.json changed after files_proven and before PackageManager.Resolve.");
                    return;
                }
                if (root.Phase == "accepted_before_effect")
                    MarkPackageResolveEffectStarted(root.RequestId, "package-resolve-v1", new Dictionary<string, object> { { "operation", Text(root.PackageOperation, "operation") }, { "expectedManifestSha256", Text(root.PackageOperation, "expectedManifestSha256") } });
                AdvancePackageOperation(root.RequestId, current => current.PackageOperation["phase"] = "resolve_dispatching");
                try
                {
                    Client.Resolve();
                    AdvancePackageOperation(root.RequestId, current => { current.PackageOperation["resolveReturned"] = true; current.PackageOperation["phase"] = "resolve_returned"; });
                }
                catch (Exception exception)
                {
                    TerminalTimeout(RequiredRoot(root.RequestId), "PackageManager.Resolve threw after effect_started: " + exception.GetType().Name + ".");
                    return;
                }
                StartList(RequiredRoot(root.RequestId), false);
            }

            /// <summary>reload 后只允许 read-only List recovery fence，绝不重放 Resolve。</summary>
            private static void StartList(ActivePackageOperationRecord root, bool afterReload)
            {
                try
                {
                    var request = Client.List(false, true);
                    if (request == null) { TerminalTimeout(root, "PackageManager.List did not return a completion fence."); return; }
                    ActiveLists[root.RequestId] = request;
                    AdvancePackageOperation(root.RequestId, current => { current.PackageOperation["phase"] = "list_pending"; current.PackageOperation["listAfterReload"] = afterReload; });
                }
                catch (Exception exception) { TerminalTimeout(root, "PackageManager.List could not establish a completion fence: " + exception.GetType().Name + "."); }
            }

            private static void ObserveList(ActivePackageOperationRecord root)
            {
                ListRequest request;
                if (!ActiveLists.TryGetValue(root.RequestId, out request) || request == null)
                {
                    if (Bool(root.PackageOperation, "reloadObserved")) { AdvancePackageOperation(root.RequestId, current => current.PackageOperation["phase"] = "reload_observed"); return; }
                    TerminalTimeout(root, "The PackageManager List completion fence was lost before terminal observation.");
                    return;
                }
                if (!request.IsCompleted) return;
                ActiveLists.Remove(root.RequestId);
                if (request.Status >= StatusCode.Failure)
                {
                    if (Bool(root.PackageOperation, "listAfterReload")) { TerminalTimeout(root, "The recovery PackageManager List fence failed after reload."); return; }
                    var error = request.Error;
                    ResolveFailure(root, error == null ? "unknown" : error.errorCode.ToString(), error == null ? "PackageManager List failed." : error.message);
                    return;
                }
                List<Dictionary<string, object>> listed;
                var predicateNames = new HashSet<string>(Dictionaries(root.PackageOperation, "predicates").Where(predicate => predicate.ContainsKey("packageTreeHash")).Select(predicate => Text(predicate, "name")), StringComparer.Ordinal);
                try { listed = Snapshot(request.Result, predicateNames).OrderBy(package => Text(package, "name"), StringComparer.Ordinal).ToList(); }
                catch (Exception exception) { TerminalTimeout(root, "PackageManager List success could not be persisted: " + exception.GetType().Name + "."); return; }
                AdvancePackageOperation(root.RequestId, current =>
                {
                    current.PackageOperation["listPackages"] = listed.Cast<object>().ToList();
                    current.PackageOperation["listSucceeded"] = true;
                    current.PackageOperation["stableFrames"] = (long)0;
                    current.PackageOperation["phase"] = "list_succeeded";
                });
            }

            /// <summary>显式 List success 先持久化，再等待 fresh PackageInfo、manifest 与 lock 连续稳定。</summary>
            private static void ObserveStablePackageInfo(ActivePackageOperationRecord root)
            {
                if (UnityBusy()) return;
                if (RefreshListAfterRegisteredSnapshotMoves(root)) return;
                Dictionary<string, object> proof;
                try { proof = Observe(root.PackageOperation, Dictionaries(root.PackageOperation, "listPackages"), PackageInfo.GetAllRegisteredPackages()); }
                catch (Exception exception) { TerminalTimeout(root, "Fresh PackageInfo readback failed: " + exception.GetType().Name + "."); return; }
                if (proof == null) { AdvancePackageOperation(root.RequestId, current => current.PackageOperation["stableFrames"] = (long)0); return; }
                var snapshotHash = HashJson(proof);
                var previousHash = Text(root.PackageOperation, "observedPackageSnapshotHash");
                var frames = Same(snapshotHash, previousHash) ? Long(root.PackageOperation, "stableFrames") + 1 : 1L;
                AdvancePackageOperation(root.RequestId, current => { current.PackageOperation["observedPackageSnapshotHash"] = snapshotHash; current.PackageOperation["stableFrames"] = frames; });
                if (frames < 3) return;
                var terminalData = PublicTerminalData(root.PackageOperation, proof);
                var envelope = ProtocolEnvelopeFactory.Success(root.RequestId, terminalData);
                MarkPackageResolveEffectProven(root.RequestId, "package-list-packageinfo-files-v1", Encoding.UTF8.GetBytes(UnityCliSerializer.Serialize(terminalData)), envelope);
                PersistPackageTerminal(root.RequestId, envelope, "completed");
            }

            /// <summary>
            /// PackageInfo 在文件替换后可能晚于首个 List fence 更新。只有 fresh snapshot 连续稳定且仍与
            /// 已持久化 List 不同，才重建一个 read-only List fence；不得重放 Resolve 或文件 effect。
            /// </summary>
            private static bool RefreshListAfterRegisteredSnapshotMoves(ActivePackageOperationRecord root)
            {
                var predicates = Dictionaries(root.PackageOperation, "predicates");
                var predicateNames = new HashSet<string>(predicates.Where(predicate => predicate.ContainsKey("packageTreeHash")).Select(predicate => Text(predicate, "name")), StringComparer.Ordinal);
                List<Dictionary<string, object>> registered;
                try { registered = Snapshot(PackageInfo.GetAllRegisteredPackages(), predicateNames).OrderBy(package => Text(package, "name"), StringComparer.Ordinal).ToList(); }
                catch (Exception exception) { TerminalTimeout(root, "Fresh PackageInfo convergence snapshot failed: " + exception.GetType().Name + "."); return true; }
                var listed = Dictionaries(root.PackageOperation, "listPackages").OrderBy(package => Text(package, "name"), StringComparer.Ordinal).ToList();
                if (SameJson(listed, registered)) return false;

                var snapshotHash = HashJson(registered);
                var frames = Same(snapshotHash, Text(root.PackageOperation, "registeredMismatchHash"))
                    ? Long(root.PackageOperation, "registeredMismatchStableFrames") + 1
                    : 1L;
                AdvancePackageOperation(root.RequestId, current =>
                {
                    current.PackageOperation["registeredMismatchHash"] = snapshotHash;
                    current.PackageOperation["registeredMismatchStableFrames"] = frames;
                    current.PackageOperation["stableFrames"] = (long)0;
                });
                if (frames < 2) return true;

                var currentRoot = RequiredRoot(root.RequestId);
                if (Long(currentRoot.PackageOperation, "listRefreshAttempts") >= 8) return true;
                AdvancePackageOperation(root.RequestId, current =>
                {
                    current.PackageOperation["listRefreshAttempts"] = Long(current.PackageOperation, "listRefreshAttempts") + 1;
                    current.PackageOperation["registeredMismatchHash"] = string.Empty;
                    current.PackageOperation["registeredMismatchStableFrames"] = (long)0;
                });
                StartList(RequiredRoot(root.RequestId), false);
                return true;
            }

            private static Dictionary<string, object> Observe(Dictionary<string, object> operation, IEnumerable<Dictionary<string, object>> listed, IEnumerable<PackageInfo> registered)
            {
                if (!Same(Text(operation, "expectedManifestSha256"), HashFile(ManifestPath()))) return null;
                var predicates = Dictionaries(operation, "predicates");
                var predicateNames = new HashSet<string>(predicates.Where(predicate => predicate.ContainsKey("packageTreeHash")).Select(predicate => Text(predicate, "name")), StringComparer.Ordinal);
                var left = (listed ?? Enumerable.Empty<Dictionary<string, object>>()).OrderBy(x => Text(x, "name"), StringComparer.Ordinal).ToList();
                var right = Snapshot(registered, predicateNames).OrderBy(x => Text(x, "name"), StringComparer.Ordinal).ToList();
                if (!SameJson(left, right)) return null;
                foreach (var predicate in predicates) if (!PredicateHolds(predicate, right)) return null;
                var selectedNames = new HashSet<string>(predicates.Select(p => Text(p, "name")), StringComparer.Ordinal);
                var selected = (predicates.Count == 0 ? right : right.Where(p => selectedNames.Contains(Text(p, "name"))).ToList())
                    .Select(package => new Dictionary<string, object> { { "name", Text(package, "name") }, { "version", Text(package, "version") }, { "source", Text(package, "source") } }).ToList();
                var lockHash = HashFile(Path.Combine(ProjectPath(), "Packages", "packages-lock.json"));
                if (!IsSha256Text(lockHash)) return null;
                return new Dictionary<string, object> { { "status", "resolved" }, { "manifestHash", Text(operation, "expectedManifestSha256") }, { "lockHash", lockHash }, { "packageSnapshotHash", HashJson(right) }, { "packageCount", (long)right.Count }, { "packages", selected } };
            }

            private static List<Dictionary<string, object>> Snapshot(IEnumerable<PackageInfo> packages, HashSet<string> treeProofNames)
            {
                return (packages ?? Enumerable.Empty<PackageInfo>()).Where(package => package != null).Select(package => new Dictionary<string, object>
                {
                    { "name", package.name ?? string.Empty }, { "version", package.version ?? string.Empty },
                    { "source", package.source.ToString().ToLowerInvariant() }, { "resolvedPath", package.resolvedPath ?? string.Empty },
                    { "packageTreeHash", treeProofNames != null && treeProofNames.Contains(package.name ?? string.Empty) ? HashDirectory(package.resolvedPath) : null }
                }).ToList();
            }

            private static bool PredicateHolds(Dictionary<string, object> predicate, List<Dictionary<string, object>> packages)
            {
                var package = packages.FirstOrDefault(item => Same(Text(item, "name"), Text(predicate, "name")));
                object presentValue;
                if (predicate.TryGetValue("present", out presentValue) && presentValue is bool && !(bool)presentValue) return package == null;
                if (package == null) return false;
                foreach (var key in new[] { "version", "packageTreeHash" })
                    if (predicate.ContainsKey(key) && !Same(Text(predicate, key), Text(package, key))) return false;
                if (predicate.ContainsKey("resolvedPath") && !SamePath(Text(predicate, "resolvedPath"), Text(package, "resolvedPath"))) return false;
                if (predicate.ContainsKey("requestedVersion") && !Same(Text(predicate, "requestedVersion"), Text(package, "version"))) return false;
                return true;
            }

            private static object PublicTerminalData(Dictionary<string, object> operation, Dictionary<string, object> resolve)
            {
                object seed;
                if (!operation.TryGetValue("publicResultSeed", out seed) || seed == null) return resolve;
                var source = seed as Dictionary<string, object>;
                if (source == null) throw new InvalidDataException("Package public result seed is invalid.");
                var result = source.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                result["resolve"] = resolve;
                return result;
            }

            private static void InitializeRoot(ActivePackageOperationRecord root, StartArgs args)
            {
                if (root.PackageOperation != null) return;
                root.State = "accepted"; root.Phase = "accepted_before_effect"; root.AcceptedAt = DateTimeOffset.UtcNow;
                root.PackageOperation = new Dictionary<string, object> {
                    { "phase", args.Operation == "resolve" ? "files_proven" : "awaiting_files" }, { "operation", args.Operation },
                    { "input", args.Input ?? new Dictionary<string, object>() }, { "resolveReturned", false }, { "reloadObserved", false }
                };
                if (args.Operation == "resolve") { root.PackageOperation["expectedManifestSha256"] = HashFile(ManifestPath()); root.PackageOperation["predicates"] = new List<object>(); root.PackageOperation["filesProof"] = null; root.PackageOperation["publicResultSeed"] = null; }
            }

            private static void ValidateStart(StartArgs args)
            {
                if (args == null || string.IsNullOrWhiteSpace(args.ExpectedEditorSessionId) || !(args.Operation == "resolve" || args.Operation == "install" || args.Operation == "remove" || args.Operation == "deploy" || args.Operation == "restore") || args.Input == null) throw Schema("Package root input is invalid.");
                if (!string.Equals(args.ExpectedEditorSessionId, EditorSessionId(), StringComparison.Ordinal)) throw new BridgeCommandException("E_EDITOR_SESSION_REPLACED", "The selected Editor session changed before Package execution.");
            }

            private static void ValidateProve(ProveArgs args)
            {
                if (args == null || !IsSafeId(args.RootRequestId) || args.ExecutorPid <= 0 || !IsSha256Text(args.ExecutorCapability) || !IsSha256Text(args.ExpectedManifestSha256) || args.FilesProof == null || args.PublicResultSeed == null) throw Schema("Package file proof input is invalid.");
            }

            /// <summary>文件 proof 必须由 Editor fresh readback重算，不能把 TS request echo 当作成功依据。</summary>
            private static void VerifyFileProof(string operation, string expectedManifestHash, Dictionary<string, object> proof, Dictionary<string, object> seed)
            {
                if (!Same(expectedManifestHash, HashFile(ManifestPath()))) throw Unknown("manifest.json does not match the claimed Package file proof.");
                object seedFiles;
                var publicProof = PublicFileProof(proof);
                if (!seed.TryGetValue("files", out seedFiles) || !SameJson(seedFiles, publicProof)) throw Unknown("Package public result files do not match the claimed file proof.");
                if (operation == "install" || operation == "remove") return;
                var target = Text(proof, "targetPath");
                if (!IsUnderPackages(target)) throw Unknown("Package target path is outside the project Packages directory.");
                if (operation == "deploy")
                {
                    if (!Same(Text(proof, "targetHash"), HashDirectory(target)) || Long(proof, "copiedFileCount") != CountFiles(target)) throw Unknown("Deployed package tree does not match the claimed file proof.");
                }
                else
                {
                    if (Bool(proof, "restoredFromBackup")) { if (!Same(Text(proof, "targetHash"), HashDirectory(target))) throw Unknown("Restored package tree does not match the claimed file proof."); }
                    else if (Directory.Exists(target)) throw Unknown("Package target still exists after a remove-only restore.");
                    var backup = Text(proof, "backupPath");
                    if (!string.IsNullOrEmpty(backup) && Directory.Exists(backup)) throw Unknown("Package backup still exists after restore.");
                }
                var metadata = Text(proof, "metadataPath");
                if (!string.IsNullOrEmpty(metadata) && !Same(Text(proof, "metadataHash"), HashFile(metadata))) throw Unknown("Package deployment metadata does not match the claimed proof.");
            }

            /// <summary>内部 proof 保留绝对路径；公开 seed 只允许当前项目内的相对业务路径。</summary>
            private static Dictionary<string, object> PublicFileProof(Dictionary<string, object> proof)
            {
                var result = proof.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                foreach (var field in new[] { "manifestPath", "targetPath", "metadataPath", "backupPath" })
                {
                    object value;
                    if (result.TryGetValue(field, out value) && value is string && !string.IsNullOrEmpty((string)value))
                        result[field] = PublicProjectPath((string)value, field);
                }
                return result;
            }

            /// <summary>Package 公开路径必须位于当前项目内，越界值按未证明终态处理。</summary>
            private static string PublicProjectPath(string value, string field)
            {
                var root = Path.GetFullPath(ProjectRoot()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var full = Path.GetFullPath(value);
                if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                    || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw Unknown("Package public " + field + " is outside the selected project.");
                return Path.GetRelativePath(root, full).Replace('\\', '/');
            }

            private static void ResolveFailure(ActivePackageOperationRecord root, string errorCode, string message)
            {
                var effects = CommittedEffects(root.PackageOperation);
                var details = new Dictionary<string, object> { { "packageErrorCode", errorCode }, { "committedEffects", effects } };
                TerminalError(root, "E_PACKAGE_RESOLVE_FAILED", string.IsNullOrWhiteSpace(message) ? "PackageManager resolution failed." : message, details);
            }

            private static object CommittedEffects(Dictionary<string, object> operation)
            {
                object proof;
                return operation.TryGetValue("filesProof", out proof) && proof != null ? proof : new Dictionary<string, object> { { "resolveRequested", true } };
            }

            private static void TerminalTimeout(ActivePackageOperationRecord root, string message)
            {
                var details = new Dictionary<string, object> { { "committedEffects", CommittedEffects(root.PackageOperation) } };
                PersistPackageTerminal(root.RequestId, ProtocolEnvelopeFactory.Error(root.RequestId, "E_EXECUTION_TIMEOUT", message + " The result could not be confirmed.", details, new[] { "Inspect Package Manager and package files before submitting a new request." }), "failed");
            }

            private static void TerminalError(ActivePackageOperationRecord root, string code, string message, object details, IEnumerable<string> nextActions = null)
            {
                PersistPackageTerminal(root.RequestId, ProtocolEnvelopeFactory.Error(root.RequestId, code, message, details, nextActions ?? new[] { "Inspect Package Manager and package files before retrying." }), "failed");
            }

            private static string BusyEnvelope(string requestId) { return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(requestId, "E_UNITY_BUSY", "Another mutation owns the selected Unity instance.", null, new[] { "Wait for the current mutation to reach its terminal result, then retry." }, true, 250)); }
            private static bool UnityBusy() { return EditorApplication.isCompiling || EditorApplication.isUpdating; }
            private static void RequireLease(string requestId) { if (!UnityCliSessionMutationLease.IsOwnedBy(requestId)) throw new BridgeCommandException("E_UNITY_BUSY", "Package root no longer owns the session mutation lease."); }
            private static ActivePackageOperationRecord RequiredRoot(string requestId)
            {
                ActivePackageOperationRecord terminal;
                if (TerminalRoots.TryGetValue(requestId, out terminal)) return terminal;
                var root = LoadPackageActiveOperation();
                if (root == null || !string.Equals(root.RequestId, requestId, StringComparison.Ordinal)) throw new BridgeCommandException("E_REQUEST_NOT_FOUND", "Package active operation was not found.");
                return root;
            }
            private static List<ActivePackageOperationRecord> OpenRoots()
            {
                var root = LoadPackageActiveOperation();
                return root == null ? new List<ActivePackageOperationRecord>() : new List<ActivePackageOperationRecord> { root };
            }
            private static PackageActiveDecision AcceptPackageActiveOperation(BridgeRequest request, byte[] raw, UnityCliToolCompletionContract contract, Action beforeAccess, Action beforeCreate, Action<ActivePackageOperationRecord> initialize)
            {
                var root = LoadPackageActiveOperation();
                if (root == null)
                {
                    if (beforeCreate != null) beforeCreate();
                    if (beforeAccess != null) beforeAccess();
                    DateTimeOffset deadline;
                    if (!DateTimeOffset.TryParse(request.Execution.AbsoluteDeadlineUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out deadline)) throw Schema("Package deadline is invalid.");
                    root = new ActivePackageOperationRecord { RequestId = request.RequestId, EditorSessionId = EditorSessionId(), AbsoluteDeadlineUtc = deadline, State = "accepted", Phase = "accepted_before_effect", AcceptedAt = DateTimeOffset.UtcNow };
                    initialize(root);
                    SavePackageActiveOperation(root);
                    return new PackageActiveDecision { Execute = true };
                }
                if (!string.Equals(root.EditorSessionId, EditorSessionId(), StringComparison.Ordinal))
                {
                    TerminalTimeout(root, "The Editor session changed while a Package operation was active.");
                    return new PackageActiveDecision { TerminalEnvelope = RequiredRoot(root.RequestId).TerminalEnvelope };
                }
                if (string.Equals(root.RequestId, request.RequestId, StringComparison.Ordinal)) return new PackageActiveDecision { Execute = true };
                return new PackageActiveDecision { TerminalEnvelope = ProtocolEnvelopeFactory.Error(request.RequestId, "E_UNITY_BUSY", "Another Package operation is active.", null) };
            }

            private static void AdvancePackageOperation(string requestId, Action<ActivePackageOperationRecord> change)
            {
                var root = RequiredRoot(requestId);
                if (IsTerminal(root)) return;
                change(root);
                SavePackageActiveOperation(root);
            }
            private static bool PackageEffectStarted(string requestId)
            {
                var root = RequiredRoot(requestId);
                return root.StartedAt.HasValue || root.Phase == "effect_started" || root.Phase == "effect_proven";
            }
            private static void MarkPackageFileEffectStartedActive(string requestId, Dictionary<string, object> plan, string operation)
            {
                AdvancePackageOperation(requestId, root =>
                {
                    if (root.Phase != "accepted_before_effect" || Text(root.PackageOperation, "phase") != "awaiting_files" || root.PackageOperation.ContainsKey("filePlan")) throw new BridgeCommandException("E_EXECUTION_FAILED", "Package active operation is not awaiting its file barrier.");
                    root.PackageOperation["filePlan"] = plan; root.PackageOperation["phase"] = "files_committing";
                    root.Phase = "effect_started"; root.State = "running"; root.StartedAt = DateTimeOffset.UtcNow;
                });
            }
            private static void MarkPackageResolveEffectStarted(string requestId, string kind, object continuation)
            {
                AdvancePackageOperation(requestId, root => { root.Phase = "effect_started"; root.State = "running"; root.StartedAt = DateTimeOffset.UtcNow; });
            }
            private static void MarkPackageResolveEffectProven(string requestId, string kind, byte[] proof, object envelope)
            {
                AdvancePackageOperation(requestId, root => { root.Phase = "effect_proven"; });
            }
            private static object PersistPackageTerminal(string requestId, object envelope, string state)
            {
                var root = RequiredRoot(requestId);
                root.State = state; root.Phase = "result_persisted"; root.TerminalEnvelope = envelope;
                TerminalRoots[requestId] = root;
                UnityCliActiveOperation.Clear(ActiveScope);
                UnityCliSessionMutationLease.Release(requestId);
                return envelope;
            }
            private static bool IsTerminal(ActivePackageOperationRecord root) { return root != null && root.Phase == "result_persisted"; }
            private static void SavePackageActiveOperation(ActivePackageOperationRecord root)
            {
                ValidatePackageActiveOperation(root);
                UnityCliActiveOperation.Save(ActiveScope, new Dictionary<string, object> {
                    { "requestId", root.RequestId }, { "editorSessionId", root.EditorSessionId }, { "absoluteDeadlineUtc", root.AbsoluteDeadlineUtc.ToString("O", CultureInfo.InvariantCulture) },
                    { "state", root.State }, { "phase", root.Phase }, { "acceptedAt", root.AcceptedAt.Value.ToString("O", CultureInfo.InvariantCulture) },
                    { "startedAt", root.StartedAt.HasValue ? (object)root.StartedAt.Value.ToString("O", CultureInfo.InvariantCulture) : null }, { "packageOperation", root.PackageOperation }
                });
            }
            private static ActivePackageOperationRecord LoadPackageActiveOperation()
            {
                if (!UnityCliActiveOperation.Exists(ActiveScope)) return null;
                try
                {
                    var data = UnityCliActiveOperation.Load(ActiveScope); var allowed = new HashSet<string>(new[] { "requestId", "editorSessionId", "absoluteDeadlineUtc", "state", "phase", "acceptedAt", "startedAt", "packageOperation" }, StringComparer.Ordinal);
                    if (data.Keys.Any(key => !allowed.Contains(key))) throw new InvalidDataException("Package active state has unknown fields.");
                    DateTimeOffset deadline; DateTimeOffset accepted; DateTimeOffset started;
                    if (!DateTimeOffset.TryParse(Text(data, "absoluteDeadlineUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out deadline) || !DateTimeOffset.TryParse(Text(data, "acceptedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out accepted)) throw new InvalidDataException("Package active state timestamps are invalid.");
                    var root = new ActivePackageOperationRecord { RequestId = Text(data, "requestId"), EditorSessionId = Text(data, "editorSessionId"), AbsoluteDeadlineUtc = deadline, State = Text(data, "state"), Phase = Text(data, "phase"), AcceptedAt = accepted, PackageOperation = data.ContainsKey("packageOperation") ? data["packageOperation"] as Dictionary<string, object> : null };
                    if (data.ContainsKey("startedAt") && data["startedAt"] != null) { if (!DateTimeOffset.TryParse(Text(data, "startedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out started)) throw new InvalidDataException("Package active state start time is invalid."); root.StartedAt = started; }
                    ValidatePackageActiveOperation(root); return root;
                }
                catch (Exception exception) { UnityCliActiveOperation.Clear(ActiveScope); throw new BridgeCommandException("E_EXECUTION_FAILED", "Package active state is corrupt: " + exception.Message); }
            }
            private static void ValidatePackageActiveOperation(ActivePackageOperationRecord root)
            {
                if (root == null || !IsSafeId(root.RequestId) || !IsPrefixedLowerHex(root.EditorSessionId, "session-", 64) || root.AcceptedAt == null || root.PackageOperation == null || (root.Phase != "accepted_before_effect" && root.Phase != "effect_started" && root.Phase != "effect_proven")) throw new InvalidDataException("Package active state is incomplete.");
                var operation = root.PackageOperation; var allowed = new HashSet<string>(new[] { "phase", "operation", "input", "filePlan", "fileExecutorPid", "fileExecutorStartIdentity", "fileExecutorCapabilitySha256", "expectedManifestSha256", "predicates", "filesProof", "publicResultSeed", "resolveReturned", "reloadObserved", "listAfterReload", "listSucceeded", "listPackages", "stableFrames", "observedPackageSnapshotHash", "registeredMismatchHash", "registeredMismatchStableFrames", "listRefreshAttempts" }, StringComparer.Ordinal);
                if (operation.Keys.Any(key => !allowed.Contains(key)) || !operation.ContainsKey("phase") || !operation.ContainsKey("operation") || !operation.ContainsKey("input") || !operation.ContainsKey("resolveReturned") || !operation.ContainsKey("reloadObserved")) throw new InvalidDataException("Package active operation fields are incomplete or unknown.");
                var phase = Text(operation, "phase"); if (new[] { "awaiting_files", "files_committing", "files_proven", "resolve_dispatching", "resolve_returned", "list_pending", "list_succeeded", "reload_observed" }.All(value => value != phase)) throw new InvalidDataException("Package active operation phase is invalid.");
                var operationName = Text(operation, "operation"); if (operationName != "resolve" && operationName != "install" && operationName != "remove" && operationName != "deploy" && operationName != "restore") throw new InvalidDataException("Package active operation kind is invalid.");
                if (!(operation["input"] is Dictionary<string, object>) || !(operation["resolveReturned"] is bool) || !(operation["reloadObserved"] is bool)) throw new InvalidDataException("Package active operation input is invalid.");
                var ownerCount = new[] { "fileExecutorPid", "fileExecutorStartIdentity", "fileExecutorCapabilitySha256" }.Count(operation.ContainsKey);
                if (ownerCount != 0 && ownerCount != 3) throw new InvalidDataException("Package active executor identity is incomplete.");
                if (ownerCount == 3 && (!(operation["fileExecutorPid"] is long) || (long)operation["fileExecutorPid"] <= 0 || string.IsNullOrWhiteSpace(Text(operation, "fileExecutorStartIdentity")) || !IsSha256Text(Text(operation, "fileExecutorCapabilitySha256")))) throw new InvalidDataException("Package active executor identity is invalid.");
                if (operation.ContainsKey("expectedManifestSha256") && !IsSha256Text(Text(operation, "expectedManifestSha256"))) throw new InvalidDataException("Package active manifest hash is invalid.");
                if (phase != "awaiting_files" && operationName != "resolve" && (!operation.ContainsKey("filePlan") || ownerCount != 3)) throw new InvalidDataException("Package active file effect fields are missing.");
                var proofPhase = phase == "files_proven" || phase == "resolve_dispatching" || phase == "resolve_returned" || phase == "list_pending" || phase == "list_succeeded" || phase == "reload_observed";
                if (proofPhase && (!operation.ContainsKey("expectedManifestSha256") || !(operation["predicates"] is System.Collections.IList))) throw new InvalidDataException("Package active proof fields are incomplete.");
                if (phase == "list_succeeded" && (!operation.ContainsKey("listPackages") || !(operation["listPackages"] is System.Collections.IList) || !operation.ContainsKey("stableFrames") || !(operation["stableFrames"] is long) || (long)operation["stableFrames"] < 0)) throw new InvalidDataException("Package active List state is incomplete.");
            }

            private static string ProjectPath() { return Directory.GetParent(UnityEngine.Application.dataPath).FullName; }
            private static string ManifestPath() { return Path.Combine(ProjectPath(), "Packages", "manifest.json"); }
            private static bool IsUnderPackages(string path) { if (string.IsNullOrWhiteSpace(path)) return false; var root = Path.GetFullPath(Path.Combine(ProjectPath(), "Packages") + Path.DirectorySeparatorChar); return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase); }
            private static string HashFile(string path) { if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null; using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return HexText(sha.ComputeHash(stream)); }
            private static string HashDirectory(string root) { if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null; using (var sha = SHA256.Create()) { foreach (var path in Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).OrderBy(path => path.Substring(root.Length).Replace('\\', '/'), StringComparer.Ordinal)) { var relative = path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/'); var prefix = Encoding.UTF8.GetBytes(Directory.Exists(path) ? "dir:" + relative + "/\n" : "file:" + relative + "\n"); sha.TransformBlock(prefix, 0, prefix.Length, null, 0); if (File.Exists(path)) { var bytes = File.ReadAllBytes(path); sha.TransformBlock(bytes, 0, bytes.Length, null, 0); var newline = new byte[] { 10 }; sha.TransformBlock(newline, 0, 1, null, 0); } } sha.TransformFinalBlock(new byte[0], 0, 0); return HexText(sha.Hash); } }
            private static long CountFiles(string root) { return Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).LongLength : 0L; }
            private static string HashJson(object value) { using (var sha = SHA256.Create()) return HexText(sha.ComputeHash(Encoding.UTF8.GetBytes(UnityCliSerializer.Serialize(value)))); }
            private static string HexText(byte[] bytes) { return string.Concat(bytes.Select(value => value.ToString("x2", CultureInfo.InvariantCulture))); }
            private static string Text(Dictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) ? value as string : null; }
            private static bool Bool(Dictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) && value is bool && (bool)value; }
            private static long Long(Dictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : -1L; }
            private static bool Same(string left, string right) { return !string.IsNullOrEmpty(left) && string.Equals(left, right, StringComparison.Ordinal); }
            private static bool SamePath(string left, string right) { return !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right) && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
            private static bool SameJson(object left, object right) { return string.Equals(UnityCliSerializer.Serialize(left), UnityCliSerializer.Serialize(right), StringComparison.Ordinal); }
            private static bool IsSha256Text(string value) { return value != null && value.Length == 64 && value.All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')); }
            private static List<Dictionary<string, object>> Dictionaries(Dictionary<string, object> data, string key) { object value; if (data == null || !data.TryGetValue(key, out value)) return new List<Dictionary<string, object>>(); var list = value as System.Collections.IEnumerable; return list == null ? new List<Dictionary<string, object>>() : list.Cast<object>().OfType<Dictionary<string, object>>().ToList(); }
            private static BridgeCommandException Schema(string message) { return new BridgeCommandException("E_SCHEMA_INVALID", message); }
            private static BridgeCommandException Unknown(string message) { return new BridgeCommandException("E_EXECUTION_TIMEOUT", message + " The result could not be confirmed.", null, new[] { "Inspect Package files before submitting a new request." }); }
        }
    }
}
#endif
