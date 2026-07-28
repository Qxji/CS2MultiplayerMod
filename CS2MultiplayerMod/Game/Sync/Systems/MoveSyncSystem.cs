using System.Collections.Concurrent;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates relocations. A simple unowned object moves through one relocate definition;
    /// anything with owned geometry (a building's lot, driveways, installed upgrades) is re-derived
    /// on the receiver by the game's own definition generator from the same inputs the move tool had.
    /// </summary>
    public partial class MoveSyncSystem : GameSystemBase
    {
        private const long MoveRetryWindowMs = 10000;
        public bool DeferForTerrain;
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private EntityQuery _movedObjects;
        private EntityQuery _liveObjects;
        private CommandObserver _observer;
        private bool _hasBlockedMove;
        private SimulationCommandMessage _blockedMove;
        private long _blockedMoveDeadline;

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(MoveSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));

            // Top-level objects relocated this frame. Updated narrows MovedLocation (which
            // can persist) to the frame the move actually happened.
            _movedObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<MovedLocation>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Created>(),
                },
            });

            _liveObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<global::Game.Net.Edge>(),
                },
            });

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, ObjectMoveCommand.Id);
                Mod.Service.Session.AddObserver(_observer);
            }
            SyncInbox.RegisterDrain(DrainQueue);
        }

        protected override void OnDestroy()
        {
            SyncInbox.UnregisterDrain(DrainQueue);
            if (_observer != null && Mod.Service != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
        }

        private void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _hasBlockedMove = false;
            _blockedMove = null;
            _blockedMoveDeadline = 0;
            DeferForTerrain = false;
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            long now = service.NowMs;
            _guard.Prune(now);
            CaptureMoves(session, now);
        }

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate (see there for why).</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;
            if (DeferForTerrain) return;
            Net.NetSyncSystem coordinator = World.GetOrCreateSystemManaged<Net.NetSyncSystem>();
            if (!coordinator.CanBuildDefinitions) return;
            RealizeIncoming(session, service.NowMs);
        }

        private void CaptureMoves(MultiplayerSession session, long now)
        {
            BuildSyncSystem buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();
            if (buildSync.NativeLifecycleCapturedThisFrame ||
                World.GetOrCreateSystemManaged<Net.NetSyncSystem>().DidCommitObjectGraphThisFrame) return;
            if (_movedObjects.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _movedObjects.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    string name = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    float3 oldPos = EntityManager.GetComponentData<MovedLocation>(entity).m_OldPosition;
                    Transform transform = EntityManager.GetComponentData<Transform>(entity);

                    // No actual displacement → an unrelated Updated on a once-moved object.
                    if (math.distancesq(oldPos, transform.m_Position) < 0.01f) continue;
                    if (_guard.Consume(MoveKey(name, transform.m_Position), now)) continue;

                    // A building's lot, driveways and installed upgrades move with it, and the move
                    // tool carries them as explicit definitions rather than re-deriving them. The
                    // receiver reproduces that by re-running the game's own generator over the same
                    // inputs, so the whole owned graph follows from prefab + old position + new
                    // transform - no need to ship the sender's several-hundred-definition batch.
                    float elevation = EntityManager.HasComponent<Elevation>(entity)
                        ? EntityManager.GetComponentData<Elevation>(entity).m_Elevation
                        : 0f;
                    var command = new ObjectMoveCommand
                    {
                        PrefabName = name,
                        OldX = oldPos.x, OldY = oldPos.y, OldZ = oldPos.z,
                        NewX = transform.m_Position.x, NewY = transform.m_Position.y, NewZ = transform.m_Position.z,
                        RotX = transform.m_Rotation.value.x, RotY = transform.m_Rotation.value.y,
                        RotZ = transform.m_Rotation.value.z, RotW = transform.m_Rotation.value.w,
                        Elevation = elevation,
                        ToolRandomSeed = buildSync.AppliedLifecycleToolSeed,
                    };
                    session.SendCommand(0, ObjectMoveCommand.Id, command.Encode());
                    Mod.Verbose("[MP] MoveSync captured relocation of '" + name + "'.");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        /// <summary>
        /// Publish a relocation observed in the applying tool's own definitions (see
        /// <c>BuildSyncSystem.CaptureLocalRelocationForApply</c>). That is the reliable signal: the
        /// apply pass records no "came from" marker on the moved entity itself.
        /// </summary>
        public void PublishLocalRelocation(Entity prefab, float3 oldPosition, float3 newPosition,
            quaternion rotation, float elevation, uint toolSeed)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;
            if (math.distancesq(oldPosition, newPosition) < 0.01f) return;

            string name = _prefabSystem.GetPrefabName(prefab);
            if (string.IsNullOrEmpty(name)) return;

            long now = service.NowMs;
            // Also stops the MovedLocation sweep below from sending this same move again.
            _guard.Mark(MoveKey(name, newPosition), now);

            var command = new ObjectMoveCommand
            {
                PrefabName = name,
                OldX = oldPosition.x, OldY = oldPosition.y, OldZ = oldPosition.z,
                NewX = newPosition.x, NewY = newPosition.y, NewZ = newPosition.z,
                RotX = rotation.value.x, RotY = rotation.value.y,
                RotZ = rotation.value.z, RotW = rotation.value.w,
                Elevation = elevation,
                ToolRandomSeed = toolSeed,
            };
            service.Session.SendCommand(0, ObjectMoveCommand.Id, command.Encode());
            Mod.Verbose("[MP] MoveSync captured relocation of '" + name + "' from the tool definition.");
            Diagnostics.FlightRecorder.Note("relocation captured prefab=" + name +
                " seed=" + toolSeed);
        }

        private void RealizeIncoming(MultiplayerSession session, long now)
        {
            if (_hasBlockedMove)
            {
                if (!TryRealizeMove(_blockedMove, now))
                {
                    if (now < _blockedMoveDeadline) return;
                    // The object to relocate never arrived here. Drop the relocation rather than
                    // loop the whole world through recovery (which re-failed every reload).
                    Mod.log.Warn("[MP] MoveSync: relocation target did not resolve within the retry " +
                                 "window; dropping this move (use /sync if the city drifts).");
                    Diagnostics.FlightRecorder.Note("legacy move dropped after retry window");
                    _hasBlockedMove = false;
                    _blockedMove = null;
                    return;
                }
                _hasBlockedMove = false;
                _blockedMove = null;
            }

            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                if (TryRealizeMove(message, now)) continue;
                _hasBlockedMove = true;
                _blockedMove = message;
                _blockedMoveDeadline = now + MoveRetryWindowMs;
                Diagnostics.FlightRecorder.Note("legacy move target retrying");
                return;
            }
        }

        private bool TryRealizeMove(SimulationCommandMessage message, long now)
        {
            ObjectMoveCommand command;
            try { command = ObjectMoveCommand.Decode(message.Body); }
            catch (System.Exception ex)
            {
                // A malformed peer command is not local corruption; drop it, do not resync.
                Mod.log.Warn("[MP] MoveSync: dropping malformed command: " + ex.Message);
                return true;
            }

            Entity prefab;
            if (!_prefabIndex.TryResolve(command.PrefabName, out prefab)) return false;

            var oldPos = new float3(command.OldX, command.OldY, command.OldZ);
            var newPos = new float3(command.NewX, command.NewY, command.NewZ);
            Entity original = FindAt(prefab, oldPos);
            if (original == Entity.Null)
            {
                // A reliable replay may arrive after this same move already committed.
                if (FindAt(prefab, newPos) != Entity.Null) return true;
                return false;
            }
            var rotation = new quaternion(command.RotX, command.RotY, command.RotZ, command.RotW);
            if (RequiresCompleteLifecycle(original))
            {
                // Moving only the root would leave this object's owned lot, driveways and upgrades
                // behind. Hand the same inputs the move tool had to the game's own generator: it
                // emits the relocation of every owned element, re-commits the roads the object was
                // and is attached to, and clears what the new footprint covers.
                SimulationCommandMessage retained = message;
                BuildSyncSystem buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();
                BuildSyncSystem.NativeDeriveResult derived = buildSync.TryDeriveObjectTransaction(
                    prefab, Entity.Null, original, newPos, rotation, command.Elevation,
                    command.ToolRandomSeed, "move " + command.PrefabName,
                    () => _incoming.Enqueue(retained), null);
                if (derived == BuildSyncSystem.NativeDeriveResult.Busy) return false;
                if (derived == BuildSyncSystem.NativeDeriveResult.Armed)
                {
                    _guard.Mark(MoveKey(command.PrefabName, newPos), now);
                    Mod.Verbose("[MP] MoveSync realize: derived relocation of '" +
                                command.PrefabName + "' from player " + message.OriginPlayerId + ".");
                    return true;
                }
                if (derived == BuildSyncSystem.NativeDeriveResult.Failed) return true;

                // No generator on this build of the game: a root-only move is worse than none, since
                // it would strand the owned graph at the old position.
                Mod.log.Warn("[MP] MoveSync: relocation of '" + command.PrefabName +
                             "' needs the game's definition generator; dropping this move.");
                return true;
            }

            _guard.Mark(MoveKey(command.PrefabName, newPos), now);
            try
            {
                    // The move tool's commit definition: m_Original points at the existing
                    // entity, Relocate tells GenerateObjectsSystem to move it instead of
                    // spawning a copy.
                    Entity definition = EntityManager.CreateEntity();
                    EntityManager.AddComponentData(definition, new CreationDefinition
                    {
                        m_Prefab = prefab,
                        m_Original = original,
                        m_RandomSeed = 0,
                        m_Flags = CreationFlags.Permanent | CreationFlags.Relocate,
                    });
                    EntityManager.AddComponentData(definition, new ObjectDefinition
                    {
                        m_Position = newPos,
                        m_Rotation = rotation,
                        m_Scale = new float3(1f, 1f, 1f),
                        m_Probability = 100,
                    });
                    EntityManager.AddComponent<Updated>(definition);
                    EntityManager.AddComponent<Deleted>(definition);
                Mod.Verbose("[MP] MoveSync realize: moved '" + command.PrefabName + "' from player " +
                             message.OriginPlayerId + " to (" + newPos.x.ToString("F1") + "," +
                             newPos.z.ToString("F1") + ").");
            }
            catch (System.Exception ex)
            {
                // The definition was rejected before commit; drop this move rather than freeze
                // the world (the placer can /sync if the object looks out of place).
                Mod.log.Error("[MP] MoveSync realize FAILED for '" + command.PrefabName +
                              "'; dropping this move: " + ex);
                Diagnostics.FlightRecorder.Note("legacy move realize failed; dropped");
            }
            return true;
        }

        private Entity FindAt(Entity prefab, float3 position)
        {
            NativeArray<Entity> candidates = _liveObjects.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (EntityManager.GetComponentData<PrefabRef>(candidates[i]).m_Prefab != prefab) continue;
                    float3 pos = EntityManager.GetComponentData<Transform>(candidates[i]).m_Position;
                    if (math.distancesq(pos, position) <= 4f) return candidates[i];
                }
            }
            finally
            {
                candidates.Dispose();
            }
            return Entity.Null;
        }

        private bool RequiresCompleteLifecycle(Entity entity)
        {
            return EntityManager.HasComponent<Building>(entity) ||
                   EntityManager.HasBuffer<global::Game.Buildings.InstalledUpgrade>(entity) ||
                   EntityManager.HasBuffer<global::Game.Objects.SubObject>(entity) ||
                   EntityManager.HasBuffer<global::Game.Net.SubNet>(entity) ||
                   EntityManager.HasBuffer<global::Game.Areas.SubArea>(entity);
        }

        private static string MoveKey(string prefabName, float3 newPosition) =>
            "mov|" + ReplicationGuard.Key(prefabName, newPosition);

    }
}
