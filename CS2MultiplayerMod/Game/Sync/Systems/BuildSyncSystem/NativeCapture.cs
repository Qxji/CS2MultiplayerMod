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
        private ObjectToolOperationCommand _cachedLocalObjectOperation;
        private long _nextLocalObjectOperationId = 1;
        private bool _nativeLifecycleCapturedThisFrame;

        /// <summary>
        /// True through ModificationEnd when this frame's object-tool Apply was already published
        /// from native definitions. Legacy final-entity capture systems use it to avoid sending a
        /// second, reduced representation of the same placement, extension, or relocation.
        /// </summary>
        public bool NativeLifecycleCapturedThisFrame => _nativeLifecycleCapturedThisFrame;

        /// <summary>
        /// Cache the active object tool's complete definition batch after the output barrier. This
        /// is the last point at which exact placement, ownership, relocation, area, and connector
        /// intent is available together, before generation reduces it to final entities.
        /// </summary>
        public void ObserveLocalObjectDefinitions(NativeArray<Entity> definitions)
        {
            global::Game.Tools.ToolBaseSystem active = _toolSystem != null ? _toolSystem.activeTool : null;
            if (!(active is ObjectToolSystem))
            {
                _cachedLocalObjectOperation = null;
                return;
            }

            var captured = new List<ObjectToolDefinitionIntent>();
            int root = -1;
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

                // An attached upgrade often starts with an update definition for its existing
                // owner. Prefer the newly-created object as the operation root so duplicate
                // suppression and construction charging identify the extension itself.
                if (definition.Kind == ObjectToolDefinitionKind.Object &&
                    (root < 0 ||
                     (captured[root].Original.Kind != PortableEntityKind.None &&
                      definition.Original.Kind == PortableEntityKind.None)))
                    root = captured.Count;
                captured.Add(definition);
                if (captured.Count > ObjectToolOperationCommand.MaxDefinitions)
                {
                    _cachedLocalObjectOperation = null;
                    return;
                }
            }

            if (captured.Count == 0 || root < 0)
            {
                _cachedLocalObjectOperation = null;
                return;
            }

            _cachedLocalObjectOperation = new ObjectToolOperationCommand
            {
                RootIndex = (short)root,
                Definitions = captured.ToArray(),
            };
            Diagnostics.FlightRecorder.Note("object native definitions observed=" + captured.Count);
        }

        /// <summary>Publish the cached batch when the object tool enters Apply.</summary>
        public void CaptureLocalObjectApply()
        {
            _nativeLifecycleCapturedThisFrame = false;
            if (!_localObjectApplyThisFrame || _cachedLocalObjectOperation == null) return;

            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            try
            {
                _cachedLocalObjectOperation.OperationId = _nextLocalObjectOperationId++;
                byte[] body = _cachedLocalObjectOperation.Encode();
                service.Session.SendCommand(0, ObjectToolOperationCommand.Id, body);
                _nativeLifecycleCapturedThisFrame = true;
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
                !TryCapturePortableRef(creation.m_Attached, out value.Attached)) return false;

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

        private bool TryCaptureCoursePosition(CoursePos data,
            out ObjectCoursePositionIntent value)
        {
            value = new ObjectCoursePositionIntent();
            PortableEntityRef target;
            if (!TryCapturePortableRef(data.m_Entity, out target)) return false;
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
