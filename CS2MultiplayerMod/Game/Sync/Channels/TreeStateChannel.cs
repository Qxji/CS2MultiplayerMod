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

        private const float MatchRadius = 0.5f;
        private const float MatchDistanceSq = MatchRadius * MatchRadius;
        private const int MaxPriority = TreeStateBatch.MaxRecords * 2;

        private readonly List<Entity> _priority = new List<Entity>();
        private readonly HashSet<Entity> _prioritySet = new HashSet<Entity>();
        private readonly HashSet<Entity> _redrawSet = new HashSet<Entity>();

        private EntityQuery _trees;
        private EntityQuery _prefabs;
        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private ObjectSearch _objectSearch;
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

            // One search-tree query per record. Indexing every tree in the city to serve a batch
            // capped at MaxRecords cost the receiver a whole-map walk per snapshot — on a forested
            // map that was the dominant main-thread cost of being a client.
            var candidates = new NativeList<Entity>(16, Allocator.Temp);
            var redraw = new NativeList<Entity>(64, Allocator.Temp);
            _redrawSet.Clear();
            try
            {
                for (int i = 0; i < batch.Records.Length; i++)
                {
                    TreeStateRecord record = batch.Records[i];
                    Entity prefab;
                    if (!_prefabIndex.TryResolve(record.PrefabName, out prefab))
                    {
                        _unmatched++;
                        continue;
                    }

                    Entity entity = FindTree(em, prefab, record, candidates);
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
                    // Deferred: tagging inline made every correction its own structural change.
                    // Two records within the match radius can land on one tree, and the batched
                    // add coalesces by chunk — it must not be handed the same entity twice.
                    if (!em.HasComponent<BatchesUpdated>(entity) && _redrawSet.Add(entity))
                        redraw.Add(entity);
                    _corrected++;
                }

                if (redraw.Length > 0) em.AddComponent<BatchesUpdated>(redraw.AsArray());
            }
            finally
            {
                candidates.Dispose();
                redraw.Dispose();
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
            _redrawSet.Clear();
        }

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            _prefabSystem = em.World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabs = em.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>());
            _prefabIndex = new PrefabIndex(_prefabSystem, _prefabs);
            _objectSearch = new ObjectSearch(
                em.World.GetOrCreateSystemManaged<global::Game.Objects.SearchSystem>());
            // Host-side rolling capture only; Apply resolves through the search tree instead.
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

        /// <summary>
        /// Nearest same-prefab tree within <see cref="MatchRadius"/>, preferring an exact seed
        /// match. Trees are indexed under their geometry bounds, so a canopy-sized box reaches the
        /// query point long before the pivot does and the distance gate below does the real work.
        /// </summary>
        private Entity FindTree(EntityManager em, Entity prefab, TreeStateRecord record,
            NativeList<Entity> candidates)
        {
            float3 wanted = new float3(record.PosX, record.PosY, record.PosZ);
            _objectSearch.CollectNear(wanted, MatchRadius, candidates);

            Entity best = Entity.Null;
            float bestDistance = MatchDistanceSq;
            bool bestSeedMatch = false;

            for (int i = 0; i < candidates.Length; i++)
            {
                Entity candidate = candidates[i];

                // Range gate before the identity checks: bounds are canopy-sized, so most of what
                // the tree reports is standing metres from the pivot we are matching.
                if (!em.Exists(candidate) || !em.HasComponent<Transform>(candidate)) continue;
                float distance = math.distancesq(
                    em.GetComponentData<Transform>(candidate).m_Position, wanted);
                if (distance > MatchDistanceSq) continue;

                if (!IsTreeCandidate(em, candidate)) continue;
                if (em.GetComponentData<PrefabRef>(candidate).m_Prefab != prefab) continue;

                bool seedMatch = em.GetComponentData<PseudoRandomSeed>(candidate).m_Seed ==
                                 record.RandomSeed;
                if (bestSeedMatch && !seedMatch) continue;
                if (seedMatch && !bestSeedMatch)
                {
                    best = candidate;
                    bestDistance = distance;
                    bestSeedMatch = true;
                    continue;
                }
                if (distance > bestDistance) continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }

        /// <summary>
        /// The search tree carries owned sub-objects and everything else static, and its entries are
        /// only as fresh as the last tree update — so the filtering <see cref="_trees"/> expresses as
        /// a query has to be repeated per candidate here. Liveness and <c>Transform</c> are the
        /// caller's, checked ahead of the range gate.
        /// </summary>
        private static bool IsTreeCandidate(EntityManager em, Entity entity) =>
            em.HasComponent<Tree>(entity) &&
            em.HasComponent<PrefabRef>(entity) &&
            em.HasComponent<PseudoRandomSeed>(entity) &&
            !em.HasComponent<Temp>(entity) &&
            !em.HasComponent<Deleted>(entity) &&
            !em.HasComponent<Owner>(entity);
    }
}
