using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class BuildSyncSystem
    {
        private sealed class RecentLocalObjectOperation
        {
            public ObjectToolOperationCommand Operation;
            public long ObservedAtMs;
        }

        private const int MaxRecentLocalObjectOperations = 32;
        private const long RecentLocalObjectOperationLifetimeMs = 5000;

        private ObjectToolOperationCommand _cachedLocalObjectOperation;
        private readonly List<RecentLocalObjectOperation> _recentLocalObjectOperations =
            new List<RecentLocalObjectOperation>(MaxRecentLocalObjectOperations);
        // Sampled before ToolOutputSystem runs. A one-shot stamp can switch active tools while its
        // rootless definition graph is being emitted, so the graph itself cannot tell us which
        // AssetStampPrefab owns the construction cost/contract.
        private string _selectedAssetStampPrefabName;
        private long _nextLocalObjectOperationId = 1;
        private bool _nativeLifecycleCapturedThisFrame;
        private ObjectToolOperationCommand _pendingSpecializedObjectOperation;
        private ObjectToolDefinitionIntent _pendingSpecializedAreaDefinition;
        private Entity _pendingSpecializedArea;
        private bool _completeSpecializedAreaThisFrame;

        /// <summary>
        /// True through ModificationEnd when this frame's object-tool Apply was already published
        /// from native definitions. Legacy final-entity capture systems use it to avoid sending a
        /// second, reduced representation of the same placement, extension, or relocation.
        /// </summary>
        public bool NativeLifecycleCapturedThisFrame => _nativeLifecycleCapturedThisFrame;

        /// <summary>
        /// Process object-tool output after the output barrier. A late Apply must publish the
        /// previously cached standing preview before this frame's replacement preview is cached.
        /// Keeping both actions here makes that ordering an invariant of the capture pipeline.
        /// </summary>
        public void ObserveLocalObjectToolOutput(NativeArray<Entity> definitions,
            bool allowLateApplyCapture)
        {
            if (allowLateApplyCapture)
                CaptureLocalObjectApplyAfterToolOutput();
            ObserveLocalObjectDefinitions(definitions);
        }

        /// <summary>
        /// Cache the active object tool's complete definition batch after the output barrier. This
        /// is the last point at which exact placement, ownership, relocation, area, and connector
        /// intent is available together, before generation reduces it to final entities.
        /// </summary>
        private void ObserveLocalObjectDefinitions(NativeArray<Entity> definitions)
        {
            global::Game.Tools.ToolBaseSystem active = _toolSystem != null ? _toolSystem.activeTool : null;
            Entity recreate = _areaToolSystem != null ? _areaToolSystem.recreate : Entity.Null;

            // Specialized-industry placement is one native action split across two tools. The
            // object tool first commits the main building and hands its owned lot to the area tool;
            // only after the polygon closes does the area tool return to the object tool. Preserve
            // the standing object definition through that handoff, then publish it with the final
            // extractor/storage polygon as one atomic operation.
            bool areaHandoff = recreate != Entity.Null &&
                               (active is AreaToolSystem || active is ObjectToolSystem);
            if (areaHandoff)
            {
                if (_pendingSpecializedObjectOperation == null &&
                    _cachedLocalObjectOperation != null &&
                    TryBeginSpecializedAreaCapture(recreate))
                {
                    Diagnostics.FlightRecorder.Note("specialized object/area handoff tracked");
                }

                if (_pendingSpecializedObjectOperation != null)
                {
                    if (_pendingSpecializedArea != recreate ||
                        !SpecializedAreaOwnerStillMatches(recreate,
                            _pendingSpecializedObjectOperation))
                    {
                        ClearSpecializedAreaCapture();
                    }
                    else
                    {
                        // On the completion frame AreaToolSystem switches activeTool back to the
                        // object tool, while ToolSystem.applyMode still belongs to the area tool
                        // that produced this output batch. The committed live area is captured at
                        // ModificationEnd; the final click does not emit a new definition batch.
                        if (active is ObjectToolSystem &&
                            _toolSystem.applyMode == ApplyMode.Apply)
                            _completeSpecializedAreaThisFrame = true;
                        return;
                    }
                }

                if (active is AreaToolSystem)
                {
                    _cachedLocalObjectOperation = null;
                    return;
                }
            }

            if (_pendingSpecializedObjectOperation != null)
                ClearSpecializedAreaCapture();
            if (!(active is ObjectToolSystem))
            {
                // ToolSystem keeps the tool that actually ran this ToolUpdate as its last tool even
                // when a one-shot placement switches activeTool before the output barrier. Its Apply
                // pulse therefore remains authoritative here. Require an ObjectDefinition or a
                // Stamping NetCourse in this exact output batch so an immediately-applied net/area
                // tool can never publish a stale object preview left over from the previous
                // selection. Asset stamps intentionally emit no root ObjectDefinition.
                if (_toolSystem != null && _toolSystem.applyMode == ApplyMode.Apply &&
                    ContainsObjectOrAssetStampDefinition(definitions))
                {
                    CaptureObjectToolOperation(definitions);
                    return;
                }
                _cachedLocalObjectOperation = null;
                return;
            }

            CaptureObjectToolOperation(definitions);
        }

        private bool ContainsObjectOrAssetStampDefinition(NativeArray<Entity> definitions)
        {
            for (int i = 0; i < definitions.Length; i++)
            {
                Entity entity = definitions[i];
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.HasComponent<CreationDefinition>(entity)) continue;
                if (EntityManager.HasComponent<ObjectDefinition>(entity)) return true;
                CreationDefinition creation = EntityManager.GetComponentData<CreationDefinition>(entity);
                if (EntityManager.HasComponent<NetCourse>(entity) &&
                    (creation.m_Flags & CreationFlags.Stamping) != 0) return true;
            }
            return false;
        }

        private void CaptureObjectToolOperation(NativeArray<Entity> definitions)
        {
            var captured = new List<ObjectToolDefinitionIntent>();
            int root = -1;
            bool hasStampingNet = false;
            for (int i = 0; i < definitions.Length; i++)
            {
                Entity entity = definitions[i];
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.HasComponent<CreationDefinition>(entity)) continue;

                ObjectToolDefinitionIntent definition;
                if (!TryCaptureObjectToolDefinition(entity, out definition))
                {
                    // Never publish a partial native action. The final-entity legacy path remains
                    // available for unsupported tool output, but this cache is all-or-nothing.
                    _cachedLocalObjectOperation = null;
                    return;
                }

                // Owned subobjects carry OwnerDefinition, while the top-level object does not.
                // Prefer that structural distinction first, then a newly-created object over an
                // update definition for an existing owner (the usual attached-upgrade ordering).
                if (definition.Kind == ObjectToolDefinitionKind.Object &&
                    (root < 0 || IsBetterObjectOperationRoot(definition, captured[root])))
                    root = captured.Count;
                if (definition.Kind == ObjectToolDefinitionKind.NetCourse &&
                    (((CreationFlags)definition.CreationFlags & CreationFlags.Stamping) != 0))
                    hasStampingNet = true;
                captured.Add(definition);
                if (captured.Count > ObjectToolOperationCommand.MaxDefinitions)
                {
                    _cachedLocalObjectOperation = null;
                    return;
                }
            }

            if (captured.Count == 0)
            {
                // ObjectToolSystem emits no definitions while an unchanged preview is standing and
                // reports ApplyMode.None. ToolOutputSystem leaves the existing Temp graph intact in
                // that case, so an empty barrier batch means "unchanged", not "no preview". Erasing
                // the cache here made stamp capture depend on clicking in the same frame as cursor
                // movement. Clear only when the tool is actively clearing/applying its output.
                if (_toolSystem == null || _toolSystem.applyMode != ApplyMode.None)
                    _cachedLocalObjectOperation = null;
                return;
            }

            if (!hasStampingNet && root < 0)
            {
                _cachedLocalObjectOperation = null;
                return;
            }

            string stampPrefabName = null;
            if (hasStampingNet)
            {
                stampPrefabName = GetSelectedAssetStampPrefabName(
                    _toolSystem != null ? _toolSystem.activeTool : null) ??
                    _selectedAssetStampPrefabName;
                if (string.IsNullOrEmpty(stampPrefabName))
                {
                    _cachedLocalObjectOperation = null;
                    Diagnostics.FlightRecorder.Note("asset stamp definitions lacked selected prefab");
                    return;
                }
                // Any ObjectDefinitions in this output are independently placed stamp subobjects,
                // not a persistent owner for the subnet graph.
                root = ObjectToolOperationCommand.AssetStampRootIndex;
            }

            _cachedLocalObjectOperation = new ObjectToolOperationCommand
            {
                RootIndex = (short)root,
                AssetStampPrefabName = stampPrefabName,
                Definitions = captured.ToArray(),
            };
            RememberRecentLocalObjectOperation(_cachedLocalObjectOperation);
            Diagnostics.FlightRecorder.Note(hasStampingNet
                ? "asset stamp native definitions observed=" + captured.Count +
                  " prefab=" + stampPrefabName
                : "object native definitions observed=" + captured.Count +
                  " root=" + captured[root].PrefabName +
                  " seed=" + unchecked((ushort)captured[root].RandomSeed));
        }

        private bool IsBetterObjectOperationRoot(ObjectToolDefinitionIntent candidate,
            ObjectToolDefinitionIntent current)
        {
            return ObjectOperationRootScore(candidate) > ObjectOperationRootScore(current);
        }

        private int ObjectOperationRootScore(ObjectToolDefinitionIntent definition)
        {
            int score = 0;
            // An upgrade preview also contains an update definition for the existing building.
            // That definition has no prefab and used to outrank the newly-created extension,
            // leaving the complete preview graph without a committed entity it could bind to.
            if (IsNewServiceUpgradeRoot(definition)) score |= 16;
            if (!definition.HasOwnerDefinition) score |= 4;
            if (definition.Original.Kind == PortableEntityKind.None) score |= 2;
            if (definition.Owner.Kind == PortableEntityKind.None) score |= 1;
            return score;
        }

        private bool IsNewServiceUpgradeRoot(ObjectToolDefinitionIntent definition)
        {
            if (definition == null || definition.Kind != ObjectToolDefinitionKind.Object ||
                definition.PrefabIsNull || string.IsNullOrEmpty(definition.PrefabName) ||
                definition.Original.Kind != PortableEntityKind.None ||
                definition.Owner.Kind != PortableEntityKind.None ||
                !definition.HasOwnerDefinition) return false;

            CreationFlags flags = (CreationFlags)definition.CreationFlags;
            if ((flags & CreationFlags.Upgrade) == 0 ||
                (flags & (CreationFlags.Delete | CreationFlags.Relocate |
                          CreationFlags.Recreate | CreationFlags.Permanent)) != 0) return false;

            Entity prefab;
            return _prefabIndex.TryResolve(definition.PrefabName, out prefab) &&
                   (EntityManager.HasComponent<ServiceUpgradeData>(prefab) ||
                    EntityManager.HasComponent<BuildingExtensionData>(prefab));
        }

        private void RememberSelectedAssetStampPrefab(global::Game.Tools.ToolBaseSystem active)
        {
            _selectedAssetStampPrefabName = GetSelectedAssetStampPrefabName(active);
        }

        private string GetSelectedAssetStampPrefabName(global::Game.Tools.ToolBaseSystem active)
        {
            PrefabBase selected = active != null ? active.GetPrefab() : null;
            if (!(selected is AssetStampPrefab)) return null;
            Entity prefab;
            if (!_prefabSystem.TryGetEntity(selected, out prefab) || prefab == Entity.Null ||
                !EntityManager.Exists(prefab) || !EntityManager.HasComponent<AssetStampData>(prefab))
                return null;
            return _prefabSystem.GetPrefabName(prefab);
        }

        private void RememberRecentLocalObjectOperation(ObjectToolOperationCommand operation)
        {
            ObjectToolDefinitionIntent root;
            if (!TryGetNewCommittedObjectRoot(operation, out root)) return;

            long now = Mod.Service != null ? Mod.Service.NowMs : 0;
            PruneRecentLocalObjectOperations(now);
            for (int i = _recentLocalObjectOperations.Count - 1; i >= 0; i--)
            {
                RecentLocalObjectOperation recent = _recentLocalObjectOperations[i];
                ObjectToolDefinitionIntent recentRoot;
                if (!TryGetNewCommittedObjectRoot(recent.Operation, out recentRoot) ||
                    !SameRootSignature(root, recentRoot)) continue;

                recent.Operation = operation;
                recent.ObservedAtMs = now;
                _recentLocalObjectOperations.RemoveAt(i);
                _recentLocalObjectOperations.Add(recent);
                return;
            }

            _recentLocalObjectOperations.Add(new RecentLocalObjectOperation
            {
                Operation = operation,
                ObservedAtMs = now,
            });
            if (_recentLocalObjectOperations.Count > MaxRecentLocalObjectOperations)
                _recentLocalObjectOperations.RemoveAt(0);
        }

        private bool TryGetNewCommittedObjectRoot(ObjectToolOperationCommand operation,
            out ObjectToolDefinitionIntent root)
        {
            root = null;
            if (operation == null || operation.IsAssetStamp || operation.Definitions == null ||
                operation.RootIndex < 0 || operation.RootIndex >= operation.Definitions.Length)
                return false;

            root = operation.Definitions[operation.RootIndex];
            if (root == null || root.Kind != ObjectToolDefinitionKind.Object ||
                root.PrefabIsNull || string.IsNullOrEmpty(root.PrefabName) ||
                root.Original.Kind != PortableEntityKind.None)
                return false;

            CreationFlags flags = (CreationFlags)root.CreationFlags;
            if ((flags & (CreationFlags.Delete | CreationFlags.Relocate |
                          CreationFlags.Recreate | CreationFlags.Permanent)) != 0) return false;

            if (IsNewServiceUpgradeRoot(root)) return true;
            return root.Owner.Kind == PortableEntityKind.None && !root.HasOwnerDefinition &&
                   (flags & CreationFlags.Upgrade) == 0;
        }

        private static bool SameRootSignature(ObjectToolDefinitionIntent left,
            ObjectToolDefinitionIntent right)
        {
            if (!string.Equals(left.PrefabName, right.PrefabName,
                    System.StringComparison.Ordinal) ||
                left.RandomSeed != right.RandomSeed ||
                left.CreationFlags != right.CreationFlags) return false;

            float3 leftPosition = new float3(left.Object.PosX, left.Object.PosY,
                left.Object.PosZ);
            float3 rightPosition = new float3(right.Object.PosX, right.Object.PosY,
                right.Object.PosZ);
            if (math.distancesq(leftPosition, rightPosition) > 0.0001f) return false;

            float4 leftRotation = new float4(left.Object.RotX, left.Object.RotY,
                left.Object.RotZ, left.Object.RotW);
            float4 rightRotation = new float4(right.Object.RotX, right.Object.RotY,
                right.Object.RotZ, right.Object.RotW);
            return math.abs(math.dot(leftRotation, rightRotation)) >= 0.99999f;
        }

        private void PruneRecentLocalObjectOperations(long now)
        {
            if (now <= 0) return;
            for (int i = _recentLocalObjectOperations.Count - 1; i >= 0; i--)
            {
                long observedAt = _recentLocalObjectOperations[i].ObservedAtMs;
                if (observedAt > 0 && now >= observedAt &&
                    now - observedAt > RecentLocalObjectOperationLifetimeMs)
                    _recentLocalObjectOperations.RemoveAt(i);
            }
        }

        private void ForgetRecentLocalObjectOperation(ObjectToolOperationCommand operation)
        {
            if (operation == null) return;
            for (int i = _recentLocalObjectOperations.Count - 1; i >= 0; i--)
                if (object.ReferenceEquals(_recentLocalObjectOperations[i].Operation, operation))
                    _recentLocalObjectOperations.RemoveAt(i);
        }

        private void ClearRecentLocalObjectOperations()
        {
            _recentLocalObjectOperations.Clear();
        }

        /// <summary>
        /// Correlate any newly-applied object, including an owned service extension, with the exact
        /// object-tool graph that produced it. This runs before the reduced top-level and upgrade
        /// capture paths, so one successful match owns the whole native transaction.
        /// </summary>
        private bool TryPublishCommittedObjectGraph(long now)
        {
            if (_recentLocalObjectOperations.Count == 0 ||
                _nativeLifecycleCapturedThisFrame ||
                (_nativeNetCoordinator != null &&
                 _nativeNetCoordinator.DidCommitObjectGraphThisFrame) ||
                _createdAppliedObjects.IsEmptyIgnoreFilter) return false;

            NativeArray<Entity> entities = _createdAppliedObjects.ToEntityArray(Allocator.Temp);
            try
            {
                var created = new List<Entity>(entities.Length);
                for (int i = 0; i < entities.Length; i++) created.Add(entities[i]);
                return TryPublishMatchingRecentLocalObjectOperation(created, now);
            }
            finally
            {
                entities.Dispose();
            }
        }

        /// <summary>
        /// Bind a full preview graph to the root entity that demonstrably committed. Generated
        /// objects preserve the definition's prefab, transform, and pseudo-random seed, providing
        /// a stable identity after the transient tool Apply pulse has disappeared.
        /// </summary>
        private bool TryPublishMatchingRecentLocalObjectOperation(List<Entity> created, long now)
        {
            PruneRecentLocalObjectOperations(now);
            if (_recentLocalObjectOperations.Count == 0) return false;

            for (int entityIndex = 0; entityIndex < created.Count; entityIndex++)
            {
                Entity entity = created[entityIndex];
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.HasComponent<Applied>(entity) ||
                    !EntityManager.HasComponent<PrefabRef>(entity) ||
                    !EntityManager.HasComponent<global::Game.Objects.Transform>(entity) ||
                    !EntityManager.HasComponent<PseudoRandomSeed>(entity)) continue;

                Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                string prefabName = _prefabSystem.GetPrefabName(prefab);
                global::Game.Objects.Transform transform =
                    EntityManager.GetComponentData<global::Game.Objects.Transform>(entity);
                ushort randomSeed = unchecked((ushort)
                    EntityManager.GetComponentData<PseudoRandomSeed>(entity).m_Seed);

                for (int i = _recentLocalObjectOperations.Count - 1; i >= 0; i--)
                {
                    ObjectToolOperationCommand operation =
                        _recentLocalObjectOperations[i].Operation;
                    ObjectToolDefinitionIntent root;
                    if (!TryGetNewCommittedObjectRoot(operation, out root) ||
                        !CommittedRootMatches(root, prefabName, transform, randomSeed)) continue;

                    int definitionCount = operation.Definitions.Length;
                    try
                    {
                        if (!TryPublishLocalObjectOperation(operation)) return false;
                        if (object.ReferenceEquals(_cachedLocalObjectOperation, operation))
                            _cachedLocalObjectOperation = null;
                        Diagnostics.FlightRecorder.Note("object graph matched committed root op=" +
                            operation.OperationId + " defs=" + definitionCount +
                            " prefab=" + prefabName + " seed=" + randomSeed);
                        return true;
                    }
                    catch (System.Exception ex)
                    {
                        ForgetRecentLocalObjectOperation(operation);
                        if (object.ReferenceEquals(_cachedLocalObjectOperation, operation))
                            _cachedLocalObjectOperation = null;
                        Mod.log.Warn("[MP] BuildSync: committed object graph was not sent: " +
                                     ex.Message);
                        Diagnostics.FlightRecorder.Note("committed object graph rejected=" +
                                                          ex.GetType().Name);
                        return false;
                    }
                }
            }
            return false;
        }

        private static bool CommittedRootMatches(ObjectToolDefinitionIntent root,
            string prefabName, global::Game.Objects.Transform transform, ushort randomSeed)
        {
            if (!string.Equals(root.PrefabName, prefabName, System.StringComparison.Ordinal) ||
                unchecked((ushort)root.RandomSeed) != randomSeed) return false;

            float3 expectedPosition = new float3(root.Object.PosX, root.Object.PosY,
                root.Object.PosZ);
            // Generation copies the definition transform verbatim and ApplyObjectsSystem does not
            // rewrite it for a new entity. Keep this tight so proximity is never the identity.
            if (math.distancesq(expectedPosition, transform.m_Position) > 0.0001f)
                return false;

            float4 expectedRotation = new float4(root.Object.RotX, root.Object.RotY,
                root.Object.RotZ, root.Object.RotW);
            return math.abs(math.dot(expectedRotation, transform.m_Rotation.value)) >= 0.99999f;
        }

        private void NoteCommittedObjectGraphMiss(List<Entity> created)
        {
            if (created.Count == 0) return;
            Entity entity = created[0];
            for (int i = 0; i < created.Count; i++)
            {
                Entity candidate = created[i];
                if (EntityManager.Exists(candidate) &&
                    EntityManager.HasComponent<Applied>(candidate) &&
                    EntityManager.HasComponent<PseudoRandomSeed>(candidate))
                {
                    entity = candidate;
                    break;
                }
            }
            string prefabName = "unknown";
            string seed = "none";
            if (EntityManager.Exists(entity) && EntityManager.HasComponent<PrefabRef>(entity))
            {
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                prefabName = _prefabSystem.GetPrefabName(prefab) ?? "unknown";
            }
            if (EntityManager.Exists(entity) && EntityManager.HasComponent<PseudoRandomSeed>(entity))
                seed = EntityManager.GetComponentData<PseudoRandomSeed>(entity).m_Seed.ToString();

            string newest = "none";
            if (_recentLocalObjectOperations.Count > 0)
            {
                ObjectToolDefinitionIntent root;
                if (TryGetNewCommittedObjectRoot(
                        _recentLocalObjectOperations[_recentLocalObjectOperations.Count - 1].Operation,
                        out root))
                    newest = root.PrefabName + "/" + unchecked((ushort)root.RandomSeed);
            }
            Diagnostics.FlightRecorder.Note("object graph match missed prefab=" + prefabName +
                " seed=" + seed + " recent=" + _recentLocalObjectOperations.Count +
                " newest=" + newest);
        }

        private bool TryBeginSpecializedAreaCapture(Entity recreate)
        {
            if (!SpecializedAreaOwnerStillMatches(recreate, _cachedLocalObjectOperation))
                return false;
            ForgetRecentLocalObjectOperation(_cachedLocalObjectOperation);
            _pendingSpecializedObjectOperation = _cachedLocalObjectOperation;
            _pendingSpecializedArea = recreate;
            _pendingSpecializedAreaDefinition = null;
            _cachedLocalObjectOperation = null;
            return true;
        }

        private bool SpecializedAreaOwnerStillMatches(Entity area,
            ObjectToolOperationCommand operation)
        {
            if (area == Entity.Null || !EntityManager.Exists(area)) return false;

            Entity topOwner;
            return TryFindTopOwner(area, out topOwner) &&
                   SpecializedObjectMatchesRoot(topOwner, operation);
        }

        private bool SpecializedObjectMatchesRoot(Entity topOwner,
            ObjectToolOperationCommand operation)
        {
            if (operation == null || operation.Definitions == null ||
                operation.RootIndex < 0 || operation.RootIndex >= operation.Definitions.Length ||
                topOwner == Entity.Null || !EntityManager.Exists(topOwner) ||
                !EntityManager.HasComponent<PrefabRef>(topOwner) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(topOwner)) return false;

            ObjectToolDefinitionIntent root = operation.Definitions[operation.RootIndex];
            if (root == null || root.Kind != ObjectToolDefinitionKind.Object ||
                root.PrefabIsNull || string.IsNullOrEmpty(root.PrefabName) ||
                root.Original.Kind != PortableEntityKind.None) return false;
            Entity ownerPrefab = EntityManager.GetComponentData<PrefabRef>(topOwner).m_Prefab;
            if (_prefabSystem.GetPrefabName(ownerPrefab) != root.PrefabName) return false;
            global::Game.Objects.Transform ownerTransform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(topOwner);
            float3 wantedPosition = new float3(root.Object.PosX, root.Object.PosY, root.Object.PosZ);
            if (math.distancesq(ownerTransform.m_Position, wantedPosition) > 4f) return false;

            quaternion wantedRotation = new quaternion(root.Object.RotX, root.Object.RotY,
                root.Object.RotZ, root.Object.RotW);
            return math.abs(math.dot(ownerTransform.m_Rotation.value,
                       wantedRotation.value)) >= 0.98f;
        }

        private bool IsSpecializedAreaPrefab(Entity prefab)
        {
            return prefab != Entity.Null && EntityManager.Exists(prefab) &&
                   (EntityManager.HasComponent<ExtractorAreaData>(prefab) ||
                    EntityManager.HasComponent<StorageAreaData>(prefab));
        }

        private bool IsSpecializedAreaDefinitionForRoot(ObjectToolDefinitionIntent definition,
            ObjectToolDefinitionIntent root)
        {
            if (definition == null || definition.Kind != ObjectToolDefinitionKind.Area ||
                !definition.HasOwnerDefinition ||
                definition.OwnerDefinitionPrefabName != root.PrefabName ||
                string.IsNullOrEmpty(definition.PrefabName)) return false;
            Entity prefab;
            return _prefabIndex.TryResolve(definition.PrefabName, out prefab) &&
                   IsSpecializedAreaPrefab(prefab);
        }

        private void PublishSpecializedAreaOperation()
        {
            ObjectToolOperationCommand source = _pendingSpecializedObjectOperation;
            ObjectToolDefinitionIntent root = source.Definitions[source.RootIndex];
            var definitions = new List<ObjectToolDefinitionIntent>(source.Definitions.Length + 1);
            short rootIndex = -1;
            for (int i = 0; i < source.Definitions.Length; i++)
            {
                ObjectToolDefinitionIntent definition = source.Definitions[i];
                if (IsSpecializedAreaDefinitionForRoot(definition, root)) continue;
                if (i == source.RootIndex) rootIndex = (short)definitions.Count;
                definitions.Add(definition);
            }
            definitions.Add(_pendingSpecializedAreaDefinition);

            if (rootIndex < 0 || definitions.Count > ObjectToolOperationCommand.MaxDefinitions)
            {
                Mod.log.Warn("[MP] BuildSync: specialized object/area operation was incomplete; not sent.");
                ClearSpecializedAreaCapture();
                return;
            }

            var operation = new ObjectToolOperationCommand
            {
                RootIndex = rootIndex,
                Definitions = definitions.ToArray(),
            };
            try
            {
                if (TryPublishLocalObjectOperation(operation))
                {
                    Diagnostics.FlightRecorder.Note("specialized object/area operation captured op=" +
                        operation.OperationId + " defs=" + operation.Definitions.Length +
                        " areaNodes=" + _pendingSpecializedAreaDefinition.AreaNodes.Length);
                    PublishOwnedAreaSnapshot(root, _pendingSpecializedAreaDefinition);
                }
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn("[MP] BuildSync: specialized object/area operation was not sent: " +
                             ex.Message);
                Diagnostics.FlightRecorder.Note("specialized object/area capture rejected=" +
                                                  ex.GetType().Name);
            }
            finally
            {
                ClearSpecializedAreaCapture();
                _cachedLocalObjectOperation = null;
            }
        }

        private void PublishOwnedAreaSnapshot(ObjectToolDefinitionIntent root,
            ObjectToolDefinitionIntent area)
        {
            if (root == null || area == null ||
                !IsClosedAreaNodeRing(area.AreaNodes)) return;
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            int count = area.AreaNodes.Length - 1;
            var command = new OwnedAreaSnapshotCommand
            {
                AreaPrefabName = area.PrefabName,
                OwnerPrefabName = root.PrefabName,
                OwnerX = root.Object.PosX,
                OwnerY = root.Object.PosY,
                OwnerZ = root.Object.PosZ,
                OwnerRotX = root.Object.RotX,
                OwnerRotY = root.Object.RotY,
                OwnerRotZ = root.Object.RotZ,
                OwnerRotW = root.Object.RotW,
                NodeX = new float[count],
                NodeY = new float[count],
                NodeZ = new float[count],
                NodeElevation = new float[count],
            };
            for (int i = 0; i < count; i++)
            {
                command.NodeX[i] = area.AreaNodes[i].X;
                command.NodeY[i] = area.AreaNodes[i].Y;
                command.NodeZ[i] = area.AreaNodes[i].Z;
                command.NodeElevation[i] = area.AreaNodes[i].Elevation;
            }

            try
            {
                service.Session.SendCommand(0, OwnedAreaSnapshotCommand.Id,
                    command.Encode());
                Diagnostics.FlightRecorder.Note("specialized owned-area safeguard sent nodes=" +
                                                  count);
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn("[MP] BuildSync: owned-area safeguard was not sent: " +
                             ex.Message);
            }
        }

        private void ClearSpecializedAreaCapture()
        {
            _pendingSpecializedObjectOperation = null;
            _pendingSpecializedAreaDefinition = null;
            _pendingSpecializedArea = Entity.Null;
            _completeSpecializedAreaThisFrame = false;
        }

        /// <summary>
        /// Publish only after the area apply has reached live entities. DefinitionGateSystem can
        /// discard local definitions while a remote transaction owns the apply slot; checking the
        /// live polygon here prevents broadcasting an edit that was not committed on this machine.
        /// </summary>
        private void CaptureCompletedSpecializedArea()
        {
            if (!_completeSpecializedAreaThisFrame) return;
            _completeSpecializedAreaThisFrame = false;
            ObjectToolDefinitionIntent completed;
            if (!TryCaptureCompletedSpecializedArea(out completed))
            {
                Diagnostics.FlightRecorder.Note("specialized object/area apply not observed");
                ClearSpecializedAreaCapture();
                return;
            }
            _pendingSpecializedAreaDefinition = completed;
            PublishSpecializedAreaOperation();
        }

        private bool TryCaptureCompletedSpecializedArea(
            out ObjectToolDefinitionIntent completed)
        {
            completed = null;
            ObjectToolOperationCommand operation = _pendingSpecializedObjectOperation;
            if (operation == null || operation.Definitions == null ||
                operation.RootIndex < 0 || operation.RootIndex >= operation.Definitions.Length)
                return false;

            Entity area = _pendingSpecializedArea;
            if (area == Entity.Null || !EntityManager.Exists(area) ||
                !EntityManager.HasComponent<global::Game.Areas.Area>(area) ||
                !EntityManager.HasComponent<PrefabRef>(area) ||
                !EntityManager.HasBuffer<global::Game.Areas.Node>(area)) return false;

            Entity topOwner;
            if (!TryFindTopOwner(area, out topOwner) ||
                !SpecializedObjectMatchesRoot(topOwner, operation)) return false;

            global::Game.Areas.Area areaData =
                EntityManager.GetComponentData<global::Game.Areas.Area>(area);
            if ((areaData.m_Flags & global::Game.Areas.AreaFlags.Complete) == 0) return false;

            Entity areaPrefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
            Entity ownerPrefab = EntityManager.GetComponentData<PrefabRef>(topOwner).m_Prefab;
            if (!IsSpecializedAreaPrefab(areaPrefab) ||
                !PrefabDeclaresOwnedArea(ownerPrefab, areaPrefab)) return false;
            string areaPrefabName = _prefabSystem.GetPrefabName(areaPrefab);
            if (string.IsNullOrEmpty(areaPrefabName)) return false;

            DynamicBuffer<global::Game.Areas.Node> liveNodes =
                EntityManager.GetBuffer<global::Game.Areas.Node>(area, isReadOnly: true);
            int liveCount = liveNodes.Length;
            if (liveCount >= 4 &&
                liveNodes[0].m_Position.Equals(liveNodes[liveCount - 1].m_Position))
                liveCount--;
            if (liveCount < 3 ||
                liveCount >= ObjectToolOperationCommand.MaxAreaNodesPerDefinition) return false;

            // A live complete area stores only its polygon vertices. GenerateAreasSystem expects a
            // repeated first vertex in a new definition to recognize and commit a closed polygon.
            var wireNodes = new ObjectAreaNodeIntent[liveCount + 1];
            for (int i = 0; i < liveCount; i++)
            {
                global::Game.Areas.Node node = liveNodes[i];
                wireNodes[i] = new ObjectAreaNodeIntent
                {
                    X = node.m_Position.x,
                    Y = node.m_Position.y,
                    Z = node.m_Position.z,
                    Elevation = node.m_Elevation,
                };
            }
            wireNodes[liveCount] = wireNodes[0];

            ObjectToolDefinitionIntent root = operation.Definitions[operation.RootIndex];
            completed = new ObjectToolDefinitionIntent
            {
                Kind = ObjectToolDefinitionKind.Area,
                PrefabName = areaPrefabName,
                CreationFlags = 0,
                RandomSeed = EntityManager.HasComponent<PseudoRandomSeed>(area)
                    ? EntityManager.GetComponentData<PseudoRandomSeed>(area).m_Seed
                    : 0,
                HasOwnerDefinition = true,
                OwnerDefinitionPrefabName = root.PrefabName,
                OwnerDefinitionX = root.Object.PosX,
                OwnerDefinitionY = root.Object.PosY,
                OwnerDefinitionZ = root.Object.PosZ,
                OwnerDefinitionRotX = root.Object.RotX,
                OwnerDefinitionRotY = root.Object.RotY,
                OwnerDefinitionRotZ = root.Object.RotZ,
                OwnerDefinitionRotW = root.Object.RotW,
                AreaNodes = wireNodes,
            };
            return true;
        }

        /// <summary>
        /// Capture a rootless asset stamp in the narrow phase between ObjectToolSystem selecting
        /// Apply and ToolOutputSystem consuming its standing preview. The cached command is that
        /// complete preview graph; the definitions generated later in the frame are the next ghost.
        /// </summary>
        public void CaptureAssetStampApplyBeforeToolOutput()
        {
            ObjectToolOperationCommand operation = _cachedLocalObjectOperation;
            if (_nativeLifecycleCapturedThisFrame || operation == null || !operation.IsAssetStamp ||
                operation.Definitions == null || _toolSystem == null ||
                _toolSystem.applyMode != ApplyMode.Apply) return;

            // A remote net transaction owns this frame's ApplyTool pass. Its isolation deliberately
            // prevents the local preview from committing, so it must not be published as local work.
            if (_nativeNetCoordinator != null && _nativeNetCoordinator.HasArmedToolCommit) return;

            string selectedStamp = GetSelectedAssetStampPrefabName(_toolSystem.activeTool) ??
                                   _selectedAssetStampPrefabName;
            if (!string.Equals(selectedStamp, operation.AssetStampPrefabName,
                    System.StringComparison.Ordinal)) return;

            _localObjectApplyThisFrame = true;
            Diagnostics.FlightRecorder.Note("asset stamp apply captured before tool output defs=" +
                                              operation.Definitions.Length + " prefab=" +
                                              operation.AssetStampPrefabName);
            PublishCachedLocalObjectOperation();
        }

        /// <summary>
        /// Process the cached batch when the object tool enters Apply. New top-level placements
        /// remain cached until their generated root proves which preview graph committed.
        /// </summary>
        public void CaptureLocalObjectApply()
        {
            // ApplyMode is a stored tool state, not an edge-triggered event. A capture performed
            // after the previous early sample still owns that Apply pulse; consume the marker and
            // let the tool update before accepting another one. Any genuinely new Apply later in
            // this ToolUpdate is caught at the output barrier.
            bool applyAlreadyCaptured = _nativeLifecycleCapturedThisFrame;
            _nativeLifecycleCapturedThisFrame = false;
            if (applyAlreadyCaptured) return;
            if (!_localObjectApplyThisFrame || _cachedLocalObjectOperation == null) return;

            // A rootless stamp has no Created object that can prove its commit later. Its dedicated
            // pre-ToolOutput hook observes the current Apply decision while the standing graph is
            // still intact; a stored Apply sampled at the front of the phase is not sufficient.
            if (_cachedLocalObjectOperation.IsAssetStamp) return;

            PublishCachedLocalObjectOperation();
        }

        /// <summary>
        /// Catch one-frame object-tool applies at the first point after the tool has made its update
        /// decision. At this point <see cref="ToolOutputSystem"/> has applied the standing preview,
        /// while <see cref="ToolOutputBarrier"/> has exposed the replacement definitions generated
        /// after the click. The cached operation still describes the graph that actually committed;
        /// callers must invoke this before replacing that cache with the new output batch.
        /// </summary>
        private void CaptureLocalObjectApplyAfterToolOutput()
        {
            if (_nativeLifecycleCapturedThisFrame || _cachedLocalObjectOperation == null) return;

            // ToolSystem chooses its last tool before entering ToolUpdate. Sampling activeTool at
            // the front of that phase therefore identifies the tool that ran even when a one-shot
            // object/stamp switches activeTool while applying. Do not let another tool's Apply
            // publish an object preview cached before a tool switch.
            if (!_localObjectToolRanThisFrame || _toolSystem == null ||
                _toolSystem.applyMode != ApplyMode.Apply) return;

            // Specialized-industry placement intentionally commits its building first and then
            // hands the owned lot to AreaToolSystem. ObserveLocalObjectDefinitions must retain this
            // standing graph until the polygon closes, when both halves are published atomically.
            Entity recreate = _areaToolSystem != null ? _areaToolSystem.recreate : Entity.Null;
            global::Game.Tools.ToolBaseSystem active = _toolSystem.activeTool;
            if (recreate != Entity.Null &&
                (active is AreaToolSystem || active is ObjectToolSystem)) return;

            // Preserve the late observation through ModificationEnd. If native encoding is rejected,
            // the legacy final-entity path still sees this as a genuine local object-tool apply.
            _localObjectApplyThisFrame = true;
            Diagnostics.FlightRecorder.Note("object apply observed after output; processing standing defs=" +
                                              _cachedLocalObjectOperation.Definitions.Length);
            PublishCachedLocalObjectOperation();
        }

        private void PublishCachedLocalObjectOperation()
        {
            if (_cachedLocalObjectOperation == null) return;

            // A new top-level object has a stronger commit signal than ApplyMode: its generated
            // root preserves the preview definition's prefab, transform, and random seed. Keep the
            // graph in the bounded recent set and publish it only after that root exists. This also
            // prevents the replacement ghost generated after a click from becoming a placement.
            ObjectToolDefinitionIntent newRoot;
            if (TryGetNewCommittedObjectRoot(_cachedLocalObjectOperation, out newRoot)) return;

            try
            {
                if (TryPublishLocalObjectOperation(_cachedLocalObjectOperation))
                    Diagnostics.FlightRecorder.Note("object operation captured op=" +
                        _cachedLocalObjectOperation.OperationId + " defs=" +
                        _cachedLocalObjectOperation.Definitions.Length);
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn("[MP] BuildSync: native object operation was not sent: " + ex.Message);
                Diagnostics.FlightRecorder.Note("object operation capture rejected=" +
                                                  ex.GetType().Name);
            }
            finally
            {
                _cachedLocalObjectOperation = null;
            }
        }

        private bool TryPublishLocalObjectOperation(ObjectToolOperationCommand operation)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return false;
            operation.OperationId = _nextLocalObjectOperationId++;
            byte[] body = operation.Encode();
            service.Session.SendCommand(0, ObjectToolOperationCommand.Id, body);
            ForgetRecentLocalObjectOperation(operation);
            _nativeLifecycleCapturedThisFrame = true;
            return true;
        }

        private bool TryCaptureObjectToolDefinition(Entity entity,
            out ObjectToolDefinitionIntent result)
        {
            result = null;
            CreationDefinition creation = EntityManager.GetComponentData<CreationDefinition>(entity);
            bool isObject = EntityManager.HasComponent<ObjectDefinition>(entity);
            bool isNet = EntityManager.HasComponent<NetCourse>(entity);
            bool isArea = EntityManager.HasBuffer<global::Game.Areas.Node>(entity);
            int shapeCount = (isObject ? 1 : 0) + (isNet ? 1 : 0) + (isArea ? 1 : 0);
            if (shapeCount != 1) return false;

            var value = new ObjectToolDefinitionIntent
            {
                Kind = isObject ? ObjectToolDefinitionKind.Object :
                    isNet ? ObjectToolDefinitionKind.NetCourse : ObjectToolDefinitionKind.Area,
                PrefabIsNull = creation.m_Prefab == Entity.Null,
                CreationFlags = (uint)creation.m_Flags,
                RandomSeed = creation.m_RandomSeed,
            };
            if (creation.m_Prefab != Entity.Null &&
                !TryPrefabName(creation.m_Prefab, out value.PrefabName)) return false;
            if (creation.m_SubPrefab != Entity.Null &&
                !TryPrefabName(creation.m_SubPrefab, out value.SubPrefabName)) return false;
            if (!TryCapturePortableRef(creation.m_Original, out value.Original) ||
                !TryCapturePortableRef(creation.m_Owner, out value.Owner) ||
                !TryCaptureAttachment(creation.m_Attached, creation.m_Prefab,
                    creation.m_Flags, out value.Attached,
                    out value.AttachedPrefabName)) return false;

            if (EntityManager.HasComponent<OwnerDefinition>(entity))
            {
                OwnerDefinition owner = EntityManager.GetComponentData<OwnerDefinition>(entity);
                if (owner.m_Prefab == Entity.Null ||
                    !TryPrefabName(owner.m_Prefab, out value.OwnerDefinitionPrefabName)) return false;
                value.HasOwnerDefinition = true;
                value.OwnerDefinitionX = owner.m_Position.x;
                value.OwnerDefinitionY = owner.m_Position.y;
                value.OwnerDefinitionZ = owner.m_Position.z;
                value.OwnerDefinitionRotX = owner.m_Rotation.value.x;
                value.OwnerDefinitionRotY = owner.m_Rotation.value.y;
                value.OwnerDefinitionRotZ = owner.m_Rotation.value.z;
                value.OwnerDefinitionRotW = owner.m_Rotation.value.w;
            }

            if (isObject)
            {
                ObjectDefinition data = EntityManager.GetComponentData<ObjectDefinition>(entity);
                value.Object = new ObjectDefinitionIntent
                {
                    PosX = data.m_Position.x, PosY = data.m_Position.y, PosZ = data.m_Position.z,
                    LocalX = data.m_LocalPosition.x, LocalY = data.m_LocalPosition.y,
                    LocalZ = data.m_LocalPosition.z,
                    ScaleX = data.m_Scale.x, ScaleY = data.m_Scale.y, ScaleZ = data.m_Scale.z,
                    RotX = data.m_Rotation.value.x, RotY = data.m_Rotation.value.y,
                    RotZ = data.m_Rotation.value.z, RotW = data.m_Rotation.value.w,
                    LocalRotX = data.m_LocalRotation.value.x,
                    LocalRotY = data.m_LocalRotation.value.y,
                    LocalRotZ = data.m_LocalRotation.value.z,
                    LocalRotW = data.m_LocalRotation.value.w,
                    Elevation = data.m_Elevation,
                    Intensity = data.m_Intensity,
                    Age = data.m_Age,
                    IsDecoration = data.m_IsDecoration,
                    ParentMesh = data.m_ParentMesh,
                    GroupIndex = data.m_GroupIndex,
                    Probability = data.m_Probability,
                    PrefabSubIndex = data.m_PrefabSubIndex,
                };
            }
            else if (isNet)
            {
                NetCourse data = EntityManager.GetComponentData<NetCourse>(entity);
                ObjectCoursePositionIntent start, end;
                if (!TryCaptureCoursePosition(data.m_StartPosition, out start) ||
                    !TryCaptureCoursePosition(data.m_EndPosition, out end)) return false;
                value.NetCourse = new ObjectNetCourseIntent
                {
                    Start = start,
                    End = end,
                    Ax = data.m_Curve.a.x, Ay = data.m_Curve.a.y, Az = data.m_Curve.a.z,
                    Bx = data.m_Curve.b.x, By = data.m_Curve.b.y, Bz = data.m_Curve.b.z,
                    Cx = data.m_Curve.c.x, Cy = data.m_Curve.c.y, Cz = data.m_Curve.c.z,
                    Dx = data.m_Curve.d.x, Dy = data.m_Curve.d.y, Dz = data.m_Curve.d.z,
                    ElevationLeft = data.m_Elevation.x,
                    ElevationRight = data.m_Elevation.y,
                    Length = data.m_Length,
                    FixedIndex = data.m_FixedIndex,
                };
            }
            else
            {
                DynamicBuffer<global::Game.Areas.Node> nodes =
                    EntityManager.GetBuffer<global::Game.Areas.Node>(entity, isReadOnly: true);
                if (nodes.Length == 0 ||
                    nodes.Length > ObjectToolOperationCommand.MaxAreaNodesPerDefinition) return false;
                value.AreaNodes = new ObjectAreaNodeIntent[nodes.Length];
                for (int i = 0; i < nodes.Length; i++)
                {
                    value.AreaNodes[i] = new ObjectAreaNodeIntent
                    {
                        X = nodes[i].m_Position.x,
                        Y = nodes[i].m_Position.y,
                        Z = nodes[i].m_Position.z,
                        Elevation = nodes[i].m_Elevation,
                    };
                }
            }

            if (EntityManager.HasComponent<Upgraded>(entity))
            {
                CompositionFlags flags = EntityManager.GetComponentData<Upgraded>(entity).m_Flags;
                value.HasUpgraded = true;
                value.UpgradeGeneral = (uint)flags.m_General;
                value.UpgradeLeft = (uint)flags.m_Left;
                value.UpgradeRight = (uint)flags.m_Right;
            }

            result = value;
            return true;
        }

        private bool TryCaptureAttachment(Entity attached, Entity objectPrefab,
            CreationFlags flags, out PortableEntityRef portable, out string prefabName)
        {
            portable = new PortableEntityRef { Kind = PortableEntityKind.None };
            prefabName = null;
            if (attached == Entity.Null) return true;

            // Placeholder facilities emit their visible level-one building as a second object
            // definition whose attachment target is the placeholder prefab entity itself. That is
            // a local prefab relationship, not a live-world entity reference.
            if (EntityManager.Exists(attached) &&
                EntityManager.HasComponent<PrefabData>(attached))
            {
                if ((flags & CreationFlags.Attach) == 0 ||
                    objectPrefab == Entity.Null ||
                    !EntityManager.Exists(objectPrefab) ||
                    !EntityManager.HasComponent<SpawnableBuildingData>(objectPrefab) ||
                    !EntityManager.HasComponent<PlaceholderBuildingData>(attached) ||
                    !TryPrefabName(attached, out prefabName))
                    return false;
                return true;
            }

            return TryCapturePortableRef(attached, out portable);
        }

        private bool TryCaptureCoursePosition(CoursePos data,
            out ObjectCoursePositionIntent value)
        {
            value = new ObjectCoursePositionIntent();
            PortableEntityRef target;
            if (!TryCaptureCourseTarget(data.m_Entity, out target)) return false;
            value.Entity = target;
            value.PosX = data.m_Position.x; value.PosY = data.m_Position.y;
            value.PosZ = data.m_Position.z;
            value.RotX = data.m_Rotation.value.x; value.RotY = data.m_Rotation.value.y;
            value.RotZ = data.m_Rotation.value.z; value.RotW = data.m_Rotation.value.w;
            value.ElevationLeft = data.m_Elevation.x;
            value.ElevationRight = data.m_Elevation.y;
            value.CourseDelta = data.m_CourseDelta;
            value.SplitPosition = data.m_SplitPosition;
            value.Flags = (uint)data.m_Flags;
            value.ParentMesh = data.m_ParentMesh;
            return true;
        }

        private bool TryCaptureCourseTarget(Entity entity, out PortableEntityRef value)
        {
            value = new PortableEntityRef { Kind = PortableEntityKind.None };
            // Course endpoints in a standing object preview can point at the previous preview's
            // Temp nodes. Those entity handles only exist on this machine. Follow their live
            // original when there is one; a preview-only endpoint must be regenerated from the
            // transmitted course position, just as it is for a fresh native sub-network.
            const int maxTempDepth = 16;
            Entity stable = entity;
            for (int depth = 0; stable != Entity.Null && depth < maxTempDepth; depth++)
            {
                if (!EntityManager.Exists(stable)) return false;
                if (!EntityManager.HasComponent<Temp>(stable))
                {
                    if (EntityManager.HasComponent<Deleted>(stable)) return false;
                    return TryCapturePortableRef(stable, out value);
                }

                Entity original = EntityManager.GetComponentData<Temp>(stable).m_Original;
                if (original == stable) return false;
                stable = original;
            }

            return stable == Entity.Null;
        }

        private bool TryCapturePortableRef(Entity entity, out PortableEntityRef value)
        {
            value = new PortableEntityRef { Kind = PortableEntityKind.None };
            if (entity == Entity.Null) return true;
            if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<PrefabRef>(entity))
                return false;

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (!TryPrefabName(prefab, out value.PrefabName)) return false;
            value.RotW = 1f;

            if (EntityManager.HasComponent<global::Game.Net.Edge>(entity) &&
                EntityManager.HasComponent<global::Game.Net.Curve>(entity))
            {
                value.Kind = PortableEntityKind.NetEdge;
                Bezier4x3 curve = EntityManager.GetComponentData<global::Game.Net.Curve>(entity).m_Bezier;
                value.Ax = curve.a.x; value.Ay = curve.a.y; value.Az = curve.a.z;
                value.Bx = curve.b.x; value.By = curve.b.y; value.Bz = curve.b.z;
                value.Cx = curve.c.x; value.Cy = curve.c.y; value.Cz = curve.c.z;
                value.Dx = curve.d.x; value.Dy = curve.d.y; value.Dz = curve.d.z;
                float3 midpoint = MathUtils.Position(curve, 0.5f);
                value.PosX = midpoint.x; value.PosY = midpoint.y; value.PosZ = midpoint.z;
            }
            else if (EntityManager.HasComponent<global::Game.Net.Node>(entity))
            {
                value.Kind = PortableEntityKind.NetNode;
                float3 position = EntityManager.GetComponentData<global::Game.Net.Node>(entity).m_Position;
                value.PosX = position.x; value.PosY = position.y; value.PosZ = position.z;
            }
            else if (EntityManager.HasComponent<global::Game.Areas.Area>(entity) &&
                     EntityManager.HasBuffer<global::Game.Areas.Node>(entity))
            {
                value.Kind = PortableEntityKind.Area;
                DynamicBuffer<global::Game.Areas.Node> nodes =
                    EntityManager.GetBuffer<global::Game.Areas.Node>(entity, isReadOnly: true);
                if (nodes.Length == 0) return false;
                value.PosX = nodes[0].m_Position.x;
                value.PosY = nodes[0].m_Position.y;
                value.PosZ = nodes[0].m_Position.z;
            }
            else if (EntityManager.HasComponent<global::Game.Objects.Transform>(entity))
            {
                value.Kind = PortableEntityKind.Object;
                global::Game.Objects.Transform transform =
                    EntityManager.GetComponentData<global::Game.Objects.Transform>(entity);
                value.PosX = transform.m_Position.x; value.PosY = transform.m_Position.y;
                value.PosZ = transform.m_Position.z;
                value.RotX = transform.m_Rotation.value.x; value.RotY = transform.m_Rotation.value.y;
                value.RotZ = transform.m_Rotation.value.z; value.RotW = transform.m_Rotation.value.w;
            }
            else return false;

            if (EntityManager.HasComponent<NetData>(prefab))
            {
                NetData netData = EntityManager.GetComponentData<NetData>(prefab);
                value.RequiredLayers = (uint)netData.m_RequiredLayers;
                value.ConnectLayers = (uint)netData.m_ConnectLayers;
            }

            Entity topOwner;
            if (!TryFindTopOwner(entity, out topOwner) || topOwner == Entity.Null) return true;
            if (!EntityManager.HasComponent<PrefabRef>(topOwner) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(topOwner)) return false;
            Entity ownerPrefab = EntityManager.GetComponentData<PrefabRef>(topOwner).m_Prefab;
            if (!TryPrefabName(ownerPrefab, out value.OwnerPrefabName)) return false;
            global::Game.Objects.Transform ownerTransform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(topOwner);
            value.OwnerX = ownerTransform.m_Position.x;
            value.OwnerY = ownerTransform.m_Position.y;
            value.OwnerZ = ownerTransform.m_Position.z;
            value.OwnerRotX = ownerTransform.m_Rotation.value.x;
            value.OwnerRotY = ownerTransform.m_Rotation.value.y;
            value.OwnerRotZ = ownerTransform.m_Rotation.value.z;
            value.OwnerRotW = ownerTransform.m_Rotation.value.w;
            return true;
        }

        private bool TryFindTopOwner(Entity entity, out Entity topOwner)
        {
            topOwner = Entity.Null;
            Entity cursor = entity;
            for (int depth = 0; depth < 64 && EntityManager.HasComponent<Owner>(cursor); depth++)
            {
                Entity next = EntityManager.GetComponentData<Owner>(cursor).m_Owner;
                if (next == Entity.Null || next == cursor || !EntityManager.Exists(next)) return false;
                topOwner = next;
                cursor = next;
            }
            return cursor == entity || !EntityManager.HasComponent<Owner>(cursor);
        }

        private bool TryPrefabName(Entity prefab, out string name)
        {
            name = prefab != Entity.Null ? _prefabSystem.GetPrefabName(prefab) : null;
            return !string.IsNullOrEmpty(name);
        }
    }
}
