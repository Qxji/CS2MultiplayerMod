using System;
using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Periodically repairs tree stage, growth and variation from the host. Tree growth advances
    /// independently in each simulation, so reproducing placement alone cannot keep apparent size
    /// synchronized over time.
    /// </summary>
    public sealed class TreeStateChannel : IStateChannel, IDisposable
    {
        public const byte Id = 16;
        public byte ChannelId => Id;

        private const float MatchCellSize = 1f;
        private const float MatchDistanceSq = 0.25f;
        private const int MaxPriority = TreeStateBatch.MaxRecords * 2;

        private readonly List<Entity> _priority = new List<Entity>();
        private readonly HashSet<Entity> _prioritySet = new HashSet<Entity>();
        private readonly Dictionary<TreeCellKey, Entity> _cells =
            new Dictionary<TreeCellKey, Entity>();

        private EntityQuery _trees;
        private EntityQuery _prefabs;
        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private bool _ready;
        private bool _warnedCapture;
        private int _cursor;
        private int _snapshots;
        private int _corrected;
        private int _unmatched;

        /// <summary>Put a newly placed host tree at the front of the next rolling snapshot.</summary>
        public void Prioritize(Entity entity)
        {
            if (entity == Entity.Null || !_prioritySet.Add(entity)) return;
            while (_priority.Count >= MaxPriority)
            {
                Entity dropped = _priority[0];
                _priority.RemoveAt(0);
                _prioritySet.Remove(dropped);
            }
            _priority.Add(entity);
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            var records = new List<TreeStateRecord>(TreeStateBatch.MaxRecords);
            var included = new HashSet<Entity>();

            while (_priority.Count > 0 && records.Count < TreeStateBatch.MaxRecords)
            {
                int last = _priority.Count - 1;
                Entity entity = _priority[last];
                _priority.RemoveAt(last);
                _prioritySet.Remove(entity);
                if (included.Add(entity)) TryCapture(em, entity, records);
            }

            NativeArray<Entity> trees = _trees.ToEntityArray(Allocator.Temp);
            try
            {
                if (trees.Length > 0)
                {
                    if (_cursor >= trees.Length) _cursor = 0;
                    int scanned = 0;
                    while (scanned < trees.Length && records.Count < TreeStateBatch.MaxRecords)
                    {
                        Entity entity = trees[_cursor];
                        _cursor = (_cursor + 1) % trees.Length;
                        scanned++;
                        if (included.Add(entity)) TryCapture(em, entity, records);
                    }
                }
                else
                {
                    _cursor = 0;
                }
            }
            finally
            {
                trees.Dispose();
            }

            if (records.Count == 0) return false;
            try
            {
                byte[] payload = new TreeStateBatch { Records = records.ToArray() }.Encode();
                writer.WriteBytes(payload, 0, payload.Length);
                return true;
            }
            catch (Exception ex)
            {
                if (!_warnedCapture)
                {
                    _warnedCapture = true;
                    Mod.log.Warn("[MP] TreeState capture failed (logged once): " + ex.Message);
                }
                return false;
            }
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            TreeStateBatch batch = TreeStateBatch.Decode(reader.ReadBytes(reader.Remaining));
            if (batch.Records.Length == 0) return;

            _cells.Clear();
            NativeArray<Entity> trees = _trees.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < trees.Length; i++)
                {
                    Entity entity = trees[i];
                    Entity prefab = em.GetComponentData<PrefabRef>(entity).m_Prefab;
                    float3 position = em.GetComponentData<Transform>(entity).m_Position;
                    int seed = em.GetComponentData<PseudoRandomSeed>(entity).m_Seed;
                    IndexTree(TreeCellKey.From(prefab, position, seed), entity);
                    IndexTree(TreeCellKey.From(prefab, position, -1), entity);
                }

                for (int i = 0; i < batch.Records.Length; i++)
                {
                    TreeStateRecord record = batch.Records[i];
                    Entity prefab;
                    if (!_prefabIndex.TryResolve(record.PrefabName, out prefab))
                    {
                        _unmatched++;
                        continue;
                    }

                    Entity entity = FindTree(em, prefab, record);
                    if (entity == Entity.Null)
                    {
                        _unmatched++;
                        continue;
                    }

                    Tree tree = em.GetComponentData<Tree>(entity);
                    PseudoRandomSeed seed = em.GetComponentData<PseudoRandomSeed>(entity);
                    bool changed = (byte)tree.m_State != record.State ||
                                   tree.m_Growth != record.Growth ||
                                   seed.m_Seed != record.RandomSeed;
                    if (!changed) continue;

                    tree.m_State = (TreeState)record.State;
                    tree.m_Growth = record.Growth;
                    seed.m_Seed = record.RandomSeed;
                    em.SetComponentData(entity, tree);
                    em.SetComponentData(entity, seed);
                    if (!em.HasComponent<BatchesUpdated>(entity)) em.AddComponent<BatchesUpdated>(entity);
                    _corrected++;
                }
            }
            finally
            {
                trees.Dispose();
            }

            _snapshots++;
            if (_snapshots % 30 == 0 && (_corrected > 0 || _unmatched > 0))
            {
                Mod.Verbose("[MP] TreeState/30 snapshots: corrected=" + _corrected +
                            " unmatched=" + _unmatched + ".");
                _corrected = 0;
                _unmatched = 0;
            }
        }

        public void Dispose()
        {
            if (_ready)
            {
                _trees.Dispose();
                _prefabs.Dispose();
            }
            _ready = false;
            _priority.Clear();
            _prioritySet.Clear();
            _cells.Clear();
        }

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            _prefabSystem = em.World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabs = em.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>());
            _prefabIndex = new PrefabIndex(_prefabSystem, _prefabs);
            _trees = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Tree>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                    ComponentType.ReadOnly<PseudoRandomSeed>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });
            _ready = true;
        }

        private void TryCapture(EntityManager em, Entity entity, List<TreeStateRecord> records)
        {
            if (!em.Exists(entity) || !em.HasComponent<Tree>(entity) ||
                !em.HasComponent<PrefabRef>(entity) || !em.HasComponent<Transform>(entity) ||
                !em.HasComponent<PseudoRandomSeed>(entity) || em.HasComponent<Temp>(entity) ||
                em.HasComponent<Deleted>(entity) || em.HasComponent<Owner>(entity)) return;

            Entity prefab = em.GetComponentData<PrefabRef>(entity).m_Prefab;
            string prefabName = _prefabSystem.GetPrefabName(prefab);
            Tree tree = em.GetComponentData<Tree>(entity);
            byte state = (byte)tree.m_State;
            if (string.IsNullOrEmpty(prefabName) || !TreeStateBatch.IsValidState(state)) return;

            Transform transform = em.GetComponentData<Transform>(entity);
            records.Add(new TreeStateRecord
            {
                PrefabName = prefabName,
                PosX = transform.m_Position.x,
                PosY = transform.m_Position.y,
                PosZ = transform.m_Position.z,
                RandomSeed = em.GetComponentData<PseudoRandomSeed>(entity).m_Seed,
                State = state,
                Growth = tree.m_Growth,
            });
        }

        private void IndexTree(TreeCellKey key, Entity entity)
        {
            Entity existing;
            if (_cells.TryGetValue(key, out existing))
                _cells[key] = Entity.Null; // ambiguous cell: exact-seed lookup usually still wins
            else
                _cells.Add(key, entity);
        }

        private Entity FindTree(EntityManager em, Entity prefab, TreeStateRecord record)
        {
            Entity exact = FindTree(em, prefab, record, record.RandomSeed);
            return exact != Entity.Null ? exact : FindTree(em, prefab, record, -1);
        }

        private Entity FindTree(EntityManager em, Entity prefab, TreeStateRecord record, int seed)
        {
            float3 wanted = new float3(record.PosX, record.PosY, record.PosZ);
            TreeCellKey centre = TreeCellKey.From(prefab, wanted, seed);
            Entity best = Entity.Null;
            float bestDistance = MatchDistanceSq;

            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                Entity candidate;
                if (!_cells.TryGetValue(centre.Offset(dx, dy, dz), out candidate) ||
                    candidate == Entity.Null) continue;
                float3 position = em.GetComponentData<Transform>(candidate).m_Position;
                float distance = math.distancesq(position, wanted);
                if (distance > bestDistance) continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }

        private struct TreeCellKey : IEquatable<TreeCellKey>
        {
            private Entity _prefab;
            private int _x, _y, _z;
            private int _seed;

            public static TreeCellKey From(Entity prefab, float3 position, int seed) => new TreeCellKey
            {
                _prefab = prefab,
                _x = (int)math.floor(position.x / MatchCellSize),
                _y = (int)math.floor(position.y / MatchCellSize),
                _z = (int)math.floor(position.z / MatchCellSize),
                _seed = seed,
            };

            public TreeCellKey Offset(int x, int y, int z) => new TreeCellKey
            {
                _prefab = _prefab,
                _x = _x + x,
                _y = _y + y,
                _z = _z + z,
                _seed = this._seed,
            };

            public bool Equals(TreeCellKey other) => _prefab.Equals(other._prefab) &&
                _x == other._x && _y == other._y && _z == other._z && _seed == other._seed;

            public override bool Equals(object obj) => obj is TreeCellKey && Equals((TreeCellKey)obj);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _prefab.GetHashCode();
                    hash = hash * 397 ^ _x;
                    hash = hash * 397 ^ _y;
                    hash = hash * 397 ^ _z;
                    return hash * 397 ^ _seed;
                }
            }
        }
    }
}
