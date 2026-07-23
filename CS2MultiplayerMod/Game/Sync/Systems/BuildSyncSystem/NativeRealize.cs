using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class BuildSyncSystem
    {
        private const long NativeObjectTargetRetryMs = 10000;
        private const long NativeObjectReplayRememberMs = 60000;

        private struct NativeObjectOperationKey : System.IEquatable<NativeObjectOperationKey>
        {
            public int Origin;
            public long Operation;
            public bool Equals(NativeObjectOperationKey other) =>
                Origin == other.Origin && Operation == other.Operation;
            public override bool Equals(object obj) =>
                obj is NativeObjectOperationKey && Equals((NativeObjectOperationKey)obj);
            public override int GetHashCode()
            {
                unchecked { return Origin * 397 ^ Operation.GetHashCode(); }
            }
        }

        private enum NativeObjectResult : byte { Completed, Armed, Retry, Rejected }

        private sealed class ResolvedObjectDefinition
        {
            public Entity Prefab;
            public Entity SubPrefab;
            public Entity Original;
            public Entity Owner;
            public Entity Attached;
            public Entity OwnerDefinitionPrefab;
            public Entity StartEntity;
            public Entity EndEntity;
        }

        private bool _hasBlockedNativeObject;
        private SimulationCommandMessage _blockedNativeObject;
        private long _blockedNativeObjectDeadline;
        private readonly CS2MultiplayerMod.Core.Sync.OperationReplayWindow<NativeObjectOperationKey>
            _recentNativeObjectOperations =
                new CS2MultiplayerMod.Core.Sync.OperationReplayWindow<NativeObjectOperationKey>();
        private EntityQuery _portableObjects;
        private EntityQuery _portableAreas;
        private Net.NetSyncSystem _nativeNetCoordinator;

        private void InitializeNativeObjectOperations()
        {
            _nativeNetCoordinator = World.GetOrCreateSystemManaged<Net.NetSyncSystem>();
            _portableObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<global::Game.Objects.Object>(),
                    ComponentType.ReadOnly<global::Game.Objects.Transform>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<global::Game.Objects.Moving>(),
                    ComponentType.ReadOnly<global::Game.Vehicles.Vehicle>(),
                    ComponentType.ReadOnly<global::Game.Creatures.Creature>(),
                },
            });
            _portableAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<global::Game.Areas.Area>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<global::Game.Areas.Node>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
        }

        private void DrainNativeObjectOperations()
        {
            _hasBlockedNativeObject = false;
            _blockedNativeObject = null;
            _blockedNativeObjectDeadline = 0;
            _recentNativeObjectOperations.Clear();
        }

        private void PruneNativeObjectOperations(long now)
        {
            _recentNativeObjectOperations.Prune(now);
        }

        private bool TryRealizeBlockedNativeObject(long now)
        {
            if (!_hasBlockedNativeObject) return true;
            if (_nativeNetCoordinator.IsCommitBusy) return false;

            NativeObjectResult result = TryRealizeNativeObject(_blockedNativeObject, now);
            if (result == NativeObjectResult.Retry)
            {
                if (now < _blockedNativeObjectDeadline) return false;
                Mod.log.Warn("[MP] BuildSync: native object operation target did not resolve; " +
                             "requesting world recovery instead of applying a partial graph.");
                Diagnostics.FlightRecorder.Note("object operation rejected after bounded retry");
                SyncInbox.RequestResync("object operation target did not resolve");
                _hasBlockedNativeObject = false;
                _blockedNativeObject = null;
                _blockedNativeObjectDeadline = 0;
                return false;
            }

            _hasBlockedNativeObject = false;
            _blockedNativeObject = null;
            _blockedNativeObjectDeadline = 0;
            return result == NativeObjectResult.Completed;
        }

        private void BlockNativeObject(SimulationCommandMessage message, long now)
        {
            _blockedNativeObject = message;
            _blockedNativeObjectDeadline = now + NativeObjectTargetRetryMs;
            _hasBlockedNativeObject = true;
            Diagnostics.FlightRecorder.Note("object operation target retrying");
        }

        private NativeObjectResult TryRealizeNativeObject(SimulationCommandMessage message, long now)
        {
            ObjectToolOperationCommand command;
            try { command = ObjectToolOperationCommand.Decode(message.Body); }
            catch (System.Exception ex)
            {
                Mod.log.Warn("[MP] BuildSync: rejecting malformed native object operation: " + ex.Message);
                Diagnostics.FlightRecorder.Note("object operation rejected malformed");
                SyncInbox.RequestResync("malformed object operation");
                return NativeObjectResult.Rejected;
            }

            string unsafePrefab;
            if (TryFindUnsafeSimulationReference(command, out unsafePrefab))
            {
                RecordRefused(unsafePrefab);
                Diagnostics.FlightRecorder.Note("object operation rejected simulation-only prefab");
                SyncInbox.RequestResync("simulation-only object operation rejected");
                return NativeObjectResult.Rejected;
            }

            var key = new NativeObjectOperationKey
            {
                Origin = message.OriginPlayerId,
                Operation = command.OperationId,
            };
            if (_recentNativeObjectOperations.Contains(key, now))
            {
                Diagnostics.FlightRecorder.Note("object operation duplicate suppressed op=" +
                                                  command.OperationId);
                return NativeObjectResult.Completed;
            }

            ResolvedObjectDefinition[] resolved;
            string reason;
            if (!TryResolveObjectOperation(command, out resolved, out reason))
            {
                Diagnostics.FlightRecorder.Note("object operation unresolved op=" + command.OperationId +
                                                  " (" + reason + ")");
                return NativeObjectResult.Retry;
            }

            if (EquivalentObjectOperationAlreadyExists(command, resolved))
            {
                _recentNativeObjectOperations.Remember(key, now, NativeObjectReplayRememberMs);
                Diagnostics.FlightRecorder.Note("object equivalent placement suppressed op=" +
                                                  command.OperationId);
                return NativeObjectResult.Completed;
            }

            if (!_nativeNetCoordinator.CanBuildDefinitions) return NativeObjectResult.Retry;
            _nativeNetCoordinator.PrepareDefinitionFrame();

            var created = new List<Entity>(command.Definitions.Length);
            try
            {
                for (int i = 0; i < command.Definitions.Length; i++)
                    created.Add(CreateObjectToolDefinition(command.Definitions[i], resolved[i]));
            }
            catch (System.Exception ex)
            {
                DestroyDefinitions(created);
                Mod.log.Warn("[MP] BuildSync: native object definitions were rejected: " + ex.Message);
                Diagnostics.FlightRecorder.Note("object definitions rejected=" + ex.GetType().Name);
                SyncInbox.RequestResync("object definitions could not be generated");
                return NativeObjectResult.Rejected;
            }

            SimulationCommandMessage retained = message;
            bool armed = _nativeNetCoordinator.ArmObjectCommit(
                () => ReplayNativeObject(retained),
                () => CompleteNativeObject(key, command, resolved, now),
                "native op=" + command.OperationId + " defs=" + command.Definitions.Length,
                command.IsAssetStamp);
            if (!armed)
            {
                DestroyDefinitions(created);
                return NativeObjectResult.Retry;
            }

            Diagnostics.FlightRecorder.Note("object definitions generated op=" + command.OperationId +
                                              " defs=" + created.Count);
            return NativeObjectResult.Armed;
        }

        /// <summary>
        /// Reject object-tool batches that attempt to create or manipulate entities owned by
        /// simulation spawning. Their prefab names are enough to decide this before resolving
        /// live entity references, so a forged mover target cannot stall the ordered retry queue.
        /// Existing growables may be referenced by a legitimate edit, but they may not be the
        /// newly-created object in a placement batch.
        /// </summary>
        private bool TryFindUnsafeSimulationReference(ObjectToolOperationCommand command,
            out string prefabName)
        {
            bool specializedPlacement = IsSpecializedIndustryPlacement(command);
            for (int i = 0; i < command.Definitions.Length; i++)
            {
                ObjectToolDefinitionIntent definition = command.Definitions[i];
                Entity prefab;

                if (!definition.PrefabIsNull &&
                    _prefabIndex.TryResolve(definition.PrefabName, out prefab))
                {
                    if (EntityManager.HasComponent<MovingObjectData>(prefab) ||
                        (definition.Kind == ObjectToolDefinitionKind.Object &&
                         definition.Original.Kind == PortableEntityKind.None &&
                         EntityManager.HasComponent<SpawnableBuildingData>(prefab) &&
                         !EntityManager.HasComponent<SignatureBuildingData>(prefab) &&
                         !(specializedPlacement &&
                           IsAllowedSpecializedSpawnable(command, i, prefab))))
                    {
                        prefabName = definition.PrefabName;
                        return true;
                    }
                }

                if (IsMovingPrefabName(definition.SubPrefabName, out prefabName) ||
                    IsMovingPrefabName(definition.AttachedPrefabName, out prefabName) ||
                    (definition.HasOwnerDefinition &&
                     IsMovingPrefabName(definition.OwnerDefinitionPrefabName, out prefabName)) ||
                    IsMovingPortableReference(definition.Original, out prefabName) ||
                    IsMovingPortableReference(definition.Owner, out prefabName) ||
                    IsMovingPortableReference(definition.Attached, out prefabName))
                    return true;

                if (definition.Kind == ObjectToolDefinitionKind.NetCourse &&
                    (IsMovingPortableReference(definition.NetCourse.Start.Entity, out prefabName) ||
                     IsMovingPortableReference(definition.NetCourse.End.Entity, out prefabName)))
                    return true;
            }

            prefabName = null;
            return false;
        }

        /// <summary>
        /// A specialized-industry placement is distinguishable from arbitrary growable creation:
        /// its new root owns a closed extractor/storage area declared by that root prefab. Some
        /// facilities use a placeholder root plus one level-one spawnable building attached to the
        /// placeholder prefab; older/direct variants use a spawnable root. Require the complete
        /// graph before exempting either exact form from the generic growable rejection.
        /// </summary>
        private bool IsSpecializedIndustryPlacement(ObjectToolOperationCommand command)
        {
            if (command == null || command.Definitions == null || command.RootIndex < 0 ||
                command.RootIndex >= command.Definitions.Length) return false;
            ObjectToolDefinitionIntent root = command.Definitions[command.RootIndex];
            if (root == null || root.Kind != ObjectToolDefinitionKind.Object || root.PrefabIsNull ||
                root.Original.Kind != PortableEntityKind.None ||
                root.Owner.Kind != PortableEntityKind.None ||
                root.Attached.Kind != PortableEntityKind.None ||
                !string.IsNullOrEmpty(root.AttachedPrefabName)) return false;

            CreationFlags rootFlags = (CreationFlags)root.CreationFlags;
            if ((rootFlags & (CreationFlags.Delete | CreationFlags.Relocate |
                              CreationFlags.Recreate | CreationFlags.Upgrade |
                              CreationFlags.Permanent)) != 0) return false;

            Entity rootPrefab;
            if (!_prefabIndex.TryResolve(root.PrefabName, out rootPrefab)) return false;
            bool directSpawnable =
                EntityManager.HasComponent<SpawnableBuildingData>(rootPrefab) &&
                !EntityManager.HasComponent<SignatureBuildingData>(rootPrefab);
            bool placeholder =
                EntityManager.HasComponent<PlaceholderBuildingData>(rootPrefab) &&
                EntityManager.HasComponent<BuildingData>(rootPrefab);
            if (!directSpawnable && !placeholder) return false;

            bool hasPlaceholderAttachment = false;
            if (placeholder)
            {
                for (int i = 0; i < command.Definitions.Length; i++)
                {
                    Entity candidatePrefab;
                    if (i != command.RootIndex &&
                        TryGetSpecializedPlaceholderAttachment(command, i, root,
                            rootPrefab, out candidatePrefab))
                    {
                        hasPlaceholderAttachment = true;
                        break;
                    }
                }
                if (!hasPlaceholderAttachment) return false;
            }

            for (int i = 0; i < command.Definitions.Length; i++)
            {
                ObjectToolDefinitionIntent area = command.Definitions[i];
                if (area == null || area.Kind != ObjectToolDefinitionKind.Area ||
                    area.PrefabIsNull || !area.HasOwnerDefinition ||
                    area.OwnerDefinitionPrefabName != root.PrefabName ||
                    area.Original.Kind != PortableEntityKind.None ||
                    area.Owner.Kind != PortableEntityKind.None ||
                    area.Attached.Kind != PortableEntityKind.None ||
                    !string.IsNullOrEmpty(area.AttachedPrefabName) ||
                    area.CreationFlags != 0 || area.AreaNodes == null ||
                    !IsClosedAreaNodeRing(area.AreaNodes)) continue;

                float3 rootPosition = new float3(root.Object.PosX, root.Object.PosY,
                    root.Object.PosZ);
                float3 ownerPosition = new float3(area.OwnerDefinitionX,
                    area.OwnerDefinitionY, area.OwnerDefinitionZ);
                if (math.distancesq(rootPosition, ownerPosition) > 0.01f) continue;
                float4 rootRotation = new float4(root.Object.RotX, root.Object.RotY,
                    root.Object.RotZ, root.Object.RotW);
                float4 ownerRotation = new float4(area.OwnerDefinitionRotX,
                    area.OwnerDefinitionRotY, area.OwnerDefinitionRotZ,
                    area.OwnerDefinitionRotW);
                if (math.abs(math.dot(rootRotation, ownerRotation)) < 0.999f) continue;

                Entity areaPrefab;
                if (!_prefabIndex.TryResolve(area.PrefabName, out areaPrefab) ||
                    !IsSpecializedAreaPrefab(areaPrefab) ||
                    !PrefabDeclaresOwnedArea(rootPrefab, areaPrefab)) continue;
                return true;
            }
            return false;
        }

        private bool IsAllowedSpecializedSpawnable(ObjectToolOperationCommand command,
            int definitionIndex, Entity definitionPrefab)
        {
            ObjectToolDefinitionIntent root = command.Definitions[command.RootIndex];
            Entity rootPrefab;
            if (!_prefabIndex.TryResolve(root.PrefabName, out rootPrefab)) return false;
            if (definitionIndex == command.RootIndex)
                return definitionPrefab == rootPrefab &&
                       EntityManager.HasComponent<SpawnableBuildingData>(rootPrefab);

            Entity attachmentPrefab;
            return TryGetSpecializedPlaceholderAttachment(command, definitionIndex,
                       root, rootPrefab, out attachmentPrefab) &&
                   attachmentPrefab == definitionPrefab;
        }

        private bool TryGetSpecializedPlaceholderAttachment(
            ObjectToolOperationCommand command, int definitionIndex,
            ObjectToolDefinitionIntent root, Entity rootPrefab, out Entity attachmentPrefab)
        {
            attachmentPrefab = Entity.Null;
            if (definitionIndex < 0 || definitionIndex >= command.Definitions.Length ||
                rootPrefab == Entity.Null ||
                !EntityManager.HasComponent<PlaceholderBuildingData>(rootPrefab))
                return false;

            ObjectToolDefinitionIntent definition =
                command.Definitions[definitionIndex];
            if (definition == null ||
                definition.Kind != ObjectToolDefinitionKind.Object ||
                definition.PrefabIsNull ||
                definition.Original.Kind != PortableEntityKind.None ||
                definition.Owner.Kind != PortableEntityKind.None ||
                definition.Attached.Kind != PortableEntityKind.None ||
                definition.HasOwnerDefinition ||
                definition.AttachedPrefabName != root.PrefabName ||
                definition.CreationFlags != (uint)CreationFlags.Attach ||
                !_prefabIndex.TryResolve(definition.PrefabName,
                    out attachmentPrefab))
                return false;

            return IsCompatiblePlaceholderAttachment(definition,
                attachmentPrefab, rootPrefab);
        }

        private bool IsCompatiblePlaceholderAttachment(
            ObjectToolDefinitionIntent definition, Entity attachmentPrefab,
            Entity placeholderPrefab)
        {
            if (definition == null ||
                definition.Kind != ObjectToolDefinitionKind.Object ||
                ((CreationFlags)definition.CreationFlags &
                 CreationFlags.Attach) == 0 ||
                attachmentPrefab == Entity.Null ||
                placeholderPrefab == Entity.Null ||
                !EntityManager.HasComponent<PrefabData>(attachmentPrefab) ||
                !EntityManager.HasComponent<ObjectData>(attachmentPrefab) ||
                !EntityManager.HasComponent<SpawnableBuildingData>(attachmentPrefab) ||
                !EntityManager.HasComponent<BuildingData>(attachmentPrefab) ||
                !EntityManager.HasComponent<PrefabData>(placeholderPrefab) ||
                !EntityManager.HasComponent<ObjectData>(placeholderPrefab) ||
                !EntityManager.HasComponent<PlaceholderBuildingData>(placeholderPrefab) ||
                !EntityManager.HasComponent<BuildingData>(placeholderPrefab))
                return false;

            SpawnableBuildingData attachment =
                EntityManager.GetComponentData<SpawnableBuildingData>(
                    attachmentPrefab);
            PlaceholderBuildingData placeholder =
                EntityManager.GetComponentData<PlaceholderBuildingData>(
                    placeholderPrefab);
            if (attachment.m_Level != 1 ||
                attachment.m_ZonePrefab == Entity.Null ||
                placeholder.m_ZonePrefab == Entity.Null ||
                !EntityManager.HasComponent<ZoneData>(attachment.m_ZonePrefab) ||
                !EntityManager.HasComponent<ZoneData>(placeholder.m_ZonePrefab))
                return false;

            ZoneData attachmentZone =
                EntityManager.GetComponentData<ZoneData>(attachment.m_ZonePrefab);
            ZoneData placeholderZone =
                EntityManager.GetComponentData<ZoneData>(placeholder.m_ZonePrefab);
            if (!attachmentZone.m_ZoneType.Equals(
                    placeholderZone.m_ZoneType))
                return false;

            BuildingData attachmentBuilding =
                EntityManager.GetComponentData<BuildingData>(attachmentPrefab);
            BuildingData placeholderBuilding =
                EntityManager.GetComponentData<BuildingData>(placeholderPrefab);
            return math.all(attachmentBuilding.m_LotSize <=
                            placeholderBuilding.m_LotSize);
        }

        private static bool IsClosedAreaNodeRing(ObjectAreaNodeIntent[] nodes)
        {
            if (nodes == null || nodes.Length < 4 ||
                nodes.Length > ObjectToolOperationCommand.MaxAreaNodesPerDefinition) return false;
            ObjectAreaNodeIntent first = nodes[0];
            ObjectAreaNodeIntent last = nodes[nodes.Length - 1];
            return first.X == last.X && first.Y == last.Y && first.Z == last.Z;
        }

        private bool PrefabDeclaresOwnedArea(Entity objectPrefab, Entity areaPrefab)
        {
            if (!EntityManager.HasBuffer<SubArea>(objectPrefab)) return false;
            DynamicBuffer<SubArea> subAreas =
                EntityManager.GetBuffer<SubArea>(objectPrefab, isReadOnly: true);
            for (int i = 0; i < subAreas.Length; i++)
            {
                Entity declared = subAreas[i].m_Prefab;
                if (declared == areaPrefab) return true;
                if (declared == Entity.Null || !EntityManager.Exists(declared)) continue;
                if (!EntityManager.HasBuffer<PlaceholderObjectElement>(declared)) continue;
                DynamicBuffer<PlaceholderObjectElement> candidates =
                    EntityManager.GetBuffer<PlaceholderObjectElement>(declared, isReadOnly: true);
                for (int j = 0; j < candidates.Length; j++)
                    if (candidates[j].m_Object == areaPrefab) return true;
            }
            return false;
        }

        private bool IsMovingPrefabName(string name, out string unsafeName)
        {
            unsafeName = null;
            if (string.IsNullOrEmpty(name)) return false;
            Entity prefab;
            if (!_prefabIndex.TryResolve(name, out prefab)) return false;
            if (!EntityManager.HasComponent<MovingObjectData>(prefab)) return false;
            unsafeName = name;
            return true;
        }

        private bool IsMovingPortableReference(PortableEntityRef reference, out string unsafeName)
        {
            unsafeName = null;
            if (reference.Kind == PortableEntityKind.None ||
                string.IsNullOrEmpty(reference.PrefabName)) return false;
            Entity prefab;
            if (!_prefabIndex.TryResolve(reference.PrefabName, out prefab) ||
                !EntityManager.HasComponent<MovingObjectData>(prefab)) return false;
            unsafeName = reference.PrefabName;
            return true;
        }

        private void ReplayNativeObject(SimulationCommandMessage message)
        {
            if (_hasBlockedNativeObject)
            {
                SyncInbox.RequestResync("object replay collided with an ordered operation");
                return;
            }
            _blockedNativeObject = message;
            _blockedNativeObjectDeadline = (Mod.Service != null ? Mod.Service.NowMs : 0) +
                                           NativeObjectTargetRetryMs;
            _hasBlockedNativeObject = true;
            Diagnostics.FlightRecorder.Note("object transaction rejected/replayed");
        }

        private void CompleteNativeObject(NativeObjectOperationKey key,
            ObjectToolOperationCommand command, ResolvedObjectDefinition[] resolved, long capturedNow)
        {
            long now = Mod.Service != null ? Mod.Service.NowMs : capturedNow;
            _recentNativeObjectOperations.Remember(key, now, NativeObjectReplayRememberMs);
            try
            {
                if (command.IsAssetStamp)
                {
                    Entity stampPrefab;
                    if (_prefabIndex.TryResolve(command.AssetStampPrefabName, out stampPrefab))
                        ConstructionCharger.ChargeObject(EntityManager, stampPrefab,
                            command.AssetStampPrefabName);
                }
                else
                {
                    ObjectToolDefinitionIntent root = command.Definitions[command.RootIndex];
                    Entity rootPrefab = resolved[command.RootIndex].Prefab;
                    CreationFlags flags = (CreationFlags)root.CreationFlags;
                    if ((flags & CreationFlags.Relocate) == 0 && rootPrefab != Entity.Null)
                    {
                        if (root.Owner.Kind != PortableEntityKind.None ||
                            (flags & CreationFlags.Upgrade) != 0)
                            ConstructionCharger.ChargeUpgrade(EntityManager, rootPrefab,
                                root.PrefabName ?? "object upgrade");
                        else
                            ConstructionCharger.ChargeObject(EntityManager, rootPrefab,
                                root.PrefabName ?? "object");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn("[MP] BuildSync: committed object charge failed: " + ex.Message);
            }
            Diagnostics.FlightRecorder.Note((command.IsAssetStamp
                ? "asset stamp transaction committed/drained op="
                : "object transaction committed/drained op=") + command.OperationId);
        }

        private bool TryResolveObjectOperation(ObjectToolOperationCommand command,
            out ResolvedObjectDefinition[] resolved, out string reason)
        {
            resolved = new ResolvedObjectDefinition[command.Definitions.Length];
            if (command.IsAssetStamp)
            {
                Entity stampPrefab;
                if (!_prefabIndex.TryResolve(command.AssetStampPrefabName, out stampPrefab) ||
                    stampPrefab == Entity.Null || !EntityManager.Exists(stampPrefab) ||
                    !EntityManager.HasComponent<AssetStampData>(stampPrefab))
                {
                    reason = "asset-stamp prefab is unavailable or incompatible";
                    return false;
                }
            }
            for (int i = 0; i < command.Definitions.Length; i++)
            {
                ObjectToolDefinitionIntent definition = command.Definitions[i];
                var target = new ResolvedObjectDefinition();
                if (((CreationFlags)definition.CreationFlags & CreationFlags.Permanent) != 0)
                {
                    reason = "remote native definitions may not bypass transaction apply";
                    return false;
                }
                if (!definition.PrefabIsNull &&
                    (!_prefabIndex.TryResolve(definition.PrefabName, out target.Prefab) ||
                     !ValidateDefinitionPrefab(definition.Kind, target.Prefab)))
                {
                    reason = "definition prefab is unavailable or incompatible";
                    return false;
                }
                if (!string.IsNullOrEmpty(definition.SubPrefabName) &&
                    (!_prefabIndex.TryResolve(definition.SubPrefabName, out target.SubPrefab) ||
                     !EntityManager.HasComponent<PrefabData>(target.SubPrefab)))
                {
                    reason = "definition sub-prefab is unavailable";
                    return false;
                }
                if (!TryResolvePortableRef(definition.Original, out target.Original) ||
                    !TryResolvePortableRef(definition.Owner, out target.Owner) ||
                    !TryResolvePortableRef(definition.Attached, out target.Attached))
                {
                    reason = "original, owner, or attachment is not present";
                    return false;
                }
                if (!string.IsNullOrEmpty(definition.AttachedPrefabName))
                {
                    Entity attachedPrefab;
                    if (target.Attached != Entity.Null ||
                        !_prefabIndex.TryResolve(definition.AttachedPrefabName,
                            out attachedPrefab) ||
                        !IsCompatiblePlaceholderAttachment(definition,
                            target.Prefab, attachedPrefab))
                    {
                        reason = "prefab-local attachment is unavailable or incompatible";
                        return false;
                    }
                    target.Attached = attachedPrefab;
                }
                if (definition.PrefabIsNull && target.Original == Entity.Null)
                {
                    reason = "a null-prefab definition has no original";
                    return false;
                }
                if (definition.HasOwnerDefinition &&
                    (!_prefabIndex.TryResolve(definition.OwnerDefinitionPrefabName,
                        out target.OwnerDefinitionPrefab) ||
                     !EntityManager.HasComponent<ObjectData>(target.OwnerDefinitionPrefab)))
                {
                    reason = "owner-definition prefab is unavailable";
                    return false;
                }
                if (definition.Kind == ObjectToolDefinitionKind.NetCourse &&
                    (!TryResolvePortableRef(definition.NetCourse.Start.Entity, out target.StartEntity) ||
                     !TryResolvePortableRef(definition.NetCourse.End.Entity, out target.EndEntity)))
                {
                    reason = "network endpoint target is not present";
                    return false;
                }
                if (definition.Kind == ObjectToolDefinitionKind.Object &&
                    (((CreationFlags)definition.CreationFlags & CreationFlags.Delete) == 0) &&
                    !QuaternionIsPlausible(definition.Object.RotX, definition.Object.RotY,
                        definition.Object.RotZ, definition.Object.RotW))
                {
                    reason = "object definition has an invalid source rotation";
                    return false;
                }
                resolved[i] = target;
            }
            reason = null;
            Diagnostics.FlightRecorder.Note("object operation targets resolved defs=" + resolved.Length);
            return true;
        }

        private static bool QuaternionIsPlausible(float x, float y, float z, float w)
        {
            float lengthSq = x * x + y * y + z * z + w * w;
            return math.isfinite(lengthSq) && lengthSq >= 0.25f && lengthSq <= 2.25f;
        }

        private bool ValidateDefinitionPrefab(ObjectToolDefinitionKind kind, Entity prefab)
        {
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<PrefabData>(prefab)) return false;
            switch (kind)
            {
                case ObjectToolDefinitionKind.Object:
                    return EntityManager.HasComponent<ObjectData>(prefab);
                case ObjectToolDefinitionKind.NetCourse:
                    return EntityManager.HasComponent<NetData>(prefab) &&
                           EntityManager.HasComponent<NetGeometryData>(prefab);
                case ObjectToolDefinitionKind.Area:
                    return EntityManager.HasComponent<AreaData>(prefab);
                default:
                    return false;
            }
        }

        private Entity CreateObjectToolDefinition(ObjectToolDefinitionIntent source,
            ResolvedObjectDefinition resolved)
        {
            Entity entity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(entity, new CreationDefinition
            {
                m_Prefab = resolved.Prefab,
                m_SubPrefab = resolved.SubPrefab,
                m_Original = resolved.Original,
                m_Owner = resolved.Owner,
                m_Attached = resolved.Attached,
                m_Flags = (CreationFlags)source.CreationFlags,
                m_RandomSeed = source.RandomSeed,
            });
            if (source.HasOwnerDefinition)
            {
                EntityManager.AddComponentData(entity, new OwnerDefinition
                {
                    m_Prefab = resolved.OwnerDefinitionPrefab,
                    m_Position = new float3(source.OwnerDefinitionX,
                        source.OwnerDefinitionY, source.OwnerDefinitionZ),
                    m_Rotation = new quaternion(source.OwnerDefinitionRotX,
                        source.OwnerDefinitionRotY, source.OwnerDefinitionRotZ,
                        source.OwnerDefinitionRotW),
                });
            }

            if (source.Kind == ObjectToolDefinitionKind.Object)
            {
                ObjectDefinitionIntent value = source.Object;
                EntityManager.AddComponentData(entity, new ObjectDefinition
                {
                    m_Position = new float3(value.PosX, value.PosY, value.PosZ),
                    m_LocalPosition = new float3(value.LocalX, value.LocalY, value.LocalZ),
                    m_Scale = new float3(value.ScaleX, value.ScaleY, value.ScaleZ),
                    m_Rotation = new quaternion(value.RotX, value.RotY, value.RotZ, value.RotW),
                    m_LocalRotation = new quaternion(value.LocalRotX, value.LocalRotY,
                        value.LocalRotZ, value.LocalRotW),
                    m_Elevation = value.Elevation,
                    m_Intensity = value.Intensity,
                    m_Age = value.Age,
                    m_IsDecoration = value.IsDecoration,
                    m_ParentMesh = value.ParentMesh,
                    m_GroupIndex = value.GroupIndex,
                    m_Probability = value.Probability,
                    m_PrefabSubIndex = value.PrefabSubIndex,
                });
            }
            else if (source.Kind == ObjectToolDefinitionKind.NetCourse)
            {
                ObjectNetCourseIntent value = source.NetCourse;
                EntityManager.AddComponentData(entity, new NetCourse
                {
                    m_StartPosition = CreateCoursePos(value.Start, resolved.StartEntity),
                    m_EndPosition = CreateCoursePos(value.End, resolved.EndEntity),
                    m_Curve = new Bezier4x3
                    {
                        a = new float3(value.Ax, value.Ay, value.Az),
                        b = new float3(value.Bx, value.By, value.Bz),
                        c = new float3(value.Cx, value.Cy, value.Cz),
                        d = new float3(value.Dx, value.Dy, value.Dz),
                    },
                    m_Elevation = new float2(value.ElevationLeft, value.ElevationRight),
                    m_Length = value.Length,
                    m_FixedIndex = value.FixedIndex,
                });
            }
            else
            {
                DynamicBuffer<global::Game.Areas.Node> nodes =
                    EntityManager.AddBuffer<global::Game.Areas.Node>(entity);
                ObjectAreaNodeIntent[] sourceNodes = source.AreaNodes;
                nodes.ResizeUninitialized(sourceNodes.Length);
                for (int i = 0; i < sourceNodes.Length; i++)
                    nodes[i] = new global::Game.Areas.Node(
                        new float3(sourceNodes[i].X, sourceNodes[i].Y, sourceNodes[i].Z),
                        sourceNodes[i].Elevation);
            }

            if (source.HasUpgraded)
            {
                EntityManager.AddComponentData(entity, new Upgraded
                {
                    m_Flags = new CompositionFlags(
                        (CompositionFlags.General)source.UpgradeGeneral,
                        (CompositionFlags.Side)source.UpgradeLeft,
                        (CompositionFlags.Side)source.UpgradeRight),
                });
            }
            EntityManager.AddComponent<Updated>(entity);
            EntityManager.AddComponent<Deleted>(entity);
            return entity;
        }

        private static CoursePos CreateCoursePos(ObjectCoursePositionIntent source, Entity target)
        {
            return new CoursePos
            {
                m_Entity = target,
                m_Position = new float3(source.PosX, source.PosY, source.PosZ),
                m_Rotation = new quaternion(source.RotX, source.RotY, source.RotZ, source.RotW),
                m_Elevation = new float2(source.ElevationLeft, source.ElevationRight),
                m_CourseDelta = source.CourseDelta,
                m_SplitPosition = source.SplitPosition,
                m_Flags = (CoursePosFlags)source.Flags,
                m_ParentMesh = source.ParentMesh,
            };
        }

        private void DestroyDefinitions(List<Entity> definitions)
        {
            for (int i = 0; i < definitions.Count; i++)
                if (EntityManager.Exists(definitions[i])) EntityManager.DestroyEntity(definitions[i]);
        }

        private bool EquivalentObjectOperationAlreadyExists(ObjectToolOperationCommand command,
            ResolvedObjectDefinition[] resolved)
        {
            // A stamp has no root object identity. Replay suppression is handled by OperationId;
            // geometry proximity would incorrectly suppress two intentional adjacent stamps.
            if (command.IsAssetStamp) return false;
            ObjectToolDefinitionIntent root = command.Definitions[command.RootIndex];
            if (root.Kind != ObjectToolDefinitionKind.Object ||
                root.Original.Kind != PortableEntityKind.None) return false;
            ObjectDefinitionIntent data = root.Object;
            PortableEntityRef wantedIdentity = default(PortableEntityRef);
            if (root.Owner.Kind != PortableEntityKind.None)
            {
                wantedIdentity.OwnerPrefabName = root.Owner.PrefabName;
                wantedIdentity.OwnerX = root.Owner.PosX;
                wantedIdentity.OwnerY = root.Owner.PosY;
                wantedIdentity.OwnerZ = root.Owner.PosZ;
            }
            else if (root.HasOwnerDefinition)
            {
                wantedIdentity.OwnerPrefabName = root.OwnerDefinitionPrefabName;
                wantedIdentity.OwnerX = root.OwnerDefinitionX;
                wantedIdentity.OwnerY = root.OwnerDefinitionY;
                wantedIdentity.OwnerZ = root.OwnerDefinitionZ;
            }
            return FindPortableObject(resolved[command.RootIndex].Prefab,
                new float3(data.PosX, data.PosY, data.PosZ), wantedIdentity) != Entity.Null;
        }

        private bool TryResolvePortableRef(PortableEntityRef source, out Entity result)
        {
            result = Entity.Null;
            if (source.Kind == PortableEntityKind.None) return true;
            Entity prefab;
            if (!_prefabIndex.TryResolve(source.PrefabName, out prefab)) return false;
            float3 position = new float3(source.PosX, source.PosY, source.PosZ);
            switch (source.Kind)
            {
                case PortableEntityKind.Object:
                    result = FindPortableObject(prefab, position, source);
                    return result != Entity.Null;
                case PortableEntityKind.NetNode:
                    result = FindPortableNode(prefab, position, source);
                    return result != Entity.Null;
                case PortableEntityKind.NetEdge:
                    result = FindPortableEdge(prefab, source);
                    return result != Entity.Null;
                case PortableEntityKind.Area:
                    result = FindPortableArea(prefab, position, source);
                    return result != Entity.Null;
                default:
                    return false;
            }
        }

        private Entity FindPortableObject(Entity prefab, float3 position, PortableEntityRef identity)
        {
            NativeArray<Entity> entities = _portableObjects.ToEntityArray(Allocator.Temp);
            try
            {
                Entity best = Entity.Null;
                float bestDistance = 4f;
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity candidate = entities[i];
                    if (EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != prefab ||
                        !MatchesPortableOwner(candidate, identity)) continue;
                    float distance = math.distancesq(EntityManager
                        .GetComponentData<global::Game.Objects.Transform>(candidate).m_Position, position);
                    if (distance >= bestDistance) continue;
                    best = candidate;
                    bestDistance = distance;
                }
                return best;
            }
            finally { entities.Dispose(); }
        }

        private Entity FindPortableNode(Entity prefab, float3 position, PortableEntityRef identity)
        {
            NativeArray<Entity> entities = _liveNodes.ToEntityArray(Allocator.Temp);
            try
            {
                Entity best = Entity.Null;
                float bestDistance = 4f;
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity candidate = entities[i];
                    if (!EntityManager.HasComponent<PrefabRef>(candidate) ||
                        EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != prefab ||
                        !MatchesNetContract(prefab, identity) ||
                        !MatchesPortableOwner(candidate, identity)) continue;
                    float3 candidatePosition = EntityManager.GetComponentData<Node>(candidate).m_Position;
                    if (math.abs(candidatePosition.y - position.y) > 3f) continue;
                    float distance = math.distancesq(candidatePosition.xz, position.xz);
                    if (distance >= bestDistance) continue;
                    best = candidate;
                    bestDistance = distance;
                }
                return best;
            }
            finally { entities.Dispose(); }
        }

        private Entity FindPortableEdge(Entity prefab, PortableEntityRef identity)
        {
            var sourceCurve = new Bezier4x3
            {
                a = new float3(identity.Ax, identity.Ay, identity.Az),
                b = new float3(identity.Bx, identity.By, identity.Bz),
                c = new float3(identity.Cx, identity.Cy, identity.Cz),
                d = new float3(identity.Dx, identity.Dy, identity.Dz),
            };
            float3 anchor = new float3(identity.PosX, identity.PosY, identity.PosZ);
            NativeArray<Entity> entities = _liveEdges.ToEntityArray(Allocator.Temp);
            try
            {
                Entity best = Entity.Null;
                float bestDistance = 2f;
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity candidate = entities[i];
                    if (!EntityManager.HasComponent<PrefabRef>(candidate) ||
                        EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != prefab ||
                        !MatchesNetContract(prefab, identity) ||
                        !MatchesPortableOwner(candidate, identity)) continue;
                    Bezier4x3 curve = EntityManager.GetComponentData<global::Game.Net.Curve>(candidate).m_Bezier;
                    if (!SplitMatch.IsSubCurve3D(curve, sourceCurve) &&
                        !SplitMatch.IsSubCurve3D(sourceCurve, curve)) continue;
                    float t;
                    float distance = MathUtils.Distance(curve, anchor, out t);
                    if (distance >= bestDistance) continue;
                    best = candidate;
                    bestDistance = distance;
                }
                return best;
            }
            finally { entities.Dispose(); }
        }

        private Entity FindPortableArea(Entity prefab, float3 anchor, PortableEntityRef identity)
        {
            NativeArray<Entity> entities = _portableAreas.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity candidate = entities[i];
                    if (EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != prefab ||
                        !MatchesPortableOwner(candidate, identity)) continue;
                    DynamicBuffer<global::Game.Areas.Node> nodes =
                        EntityManager.GetBuffer<global::Game.Areas.Node>(candidate, isReadOnly: true);
                    if (nodes.Length > 0 && math.distancesq(nodes[0].m_Position, anchor) <= 4f)
                        return candidate;
                }
            }
            finally { entities.Dispose(); }
            return Entity.Null;
        }

        private bool MatchesNetContract(Entity prefab, PortableEntityRef identity)
        {
            if (!EntityManager.HasComponent<NetData>(prefab)) return false;
            NetData data = EntityManager.GetComponentData<NetData>(prefab);
            return (uint)data.m_RequiredLayers == identity.RequiredLayers &&
                   (uint)data.m_ConnectLayers == identity.ConnectLayers;
        }

        private bool MatchesPortableOwner(Entity candidate, PortableEntityRef identity)
        {
            bool wantsOwner = !string.IsNullOrEmpty(identity.OwnerPrefabName);
            Entity topOwner;
            if (!TryFindTopOwner(candidate, out topOwner)) return false;
            if (!wantsOwner) return topOwner == Entity.Null;
            if (topOwner == Entity.Null || !EntityManager.HasComponent<PrefabRef>(topOwner) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(topOwner)) return false;
            string ownerName = _prefabSystem.GetPrefabName(
                EntityManager.GetComponentData<PrefabRef>(topOwner).m_Prefab);
            if (ownerName != identity.OwnerPrefabName) return false;
            float3 ownerPosition = EntityManager
                .GetComponentData<global::Game.Objects.Transform>(topOwner).m_Position;
            return math.distancesq(ownerPosition,
                new float3(identity.OwnerX, identity.OwnerY, identity.OwnerZ)) <= 4f;
        }
    }
}
