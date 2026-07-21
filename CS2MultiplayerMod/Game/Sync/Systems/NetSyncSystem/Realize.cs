using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Realize (client) side of NetSyncSystem: drain queued NetPlacementCommands into one working set,
    // resolve captured native targets (or classify fallback geometry), then route every course
    // through one serialized Temp+ApplyTool transaction. Dependent systems wait for its drain so
    // they never observe half-realized network geometry.
    public partial class NetSyncSystem
    {
        private const long OperationAssemblyWindowMs = 3000;

        private struct NetOperationKey : System.IEquatable<NetOperationKey>
        {
            public int Origin;
            public long Operation;

            public bool Equals(NetOperationKey other) =>
                Origin == other.Origin && Operation == other.Operation;

            public override bool Equals(object obj) =>
                obj is NetOperationKey && Equals((NetOperationKey)obj);

            public override int GetHashCode()
            {
                unchecked { return (Origin * 397) ^ Operation.GetHashCode(); }
            }
        }

        private readonly Dictionary<NetOperationKey, long> _operationAssemblyDeadlines =
            new Dictionary<NetOperationKey, long>();
        private readonly Dictionary<NetOperationKey, long> _nativeOperationDeadlines =
            new Dictionary<NetOperationKey, long>();
        private readonly Dictionary<NetOperationKey, int> _operationBuildFailures =
            new Dictionary<NetOperationKey, int>();

        private struct PreparedNativeCourse
        {
            public NetPlacementCommand Command;
            public Entity Prefab;
            public Bezier4x3 Curve;
            public float MeasuredLength;
            public bool Point;
            public bool AlreadyBuilt;
        }

        private struct RealizedCourse
        {
            public Entity Prefab;
            public string PrefabName;
            public Bezier4x3 Curve;
            public float Length;
            public bool Charge;
            public Entity StartSnap;
            public Entity EndSnap;
            public float StartT;
            public float EndT;
            public int StartKind;
            public int EndKind;
        }

        private void RealizeIncoming(MultiplayerSession session, long now)
        {
            PruneCompletedNetOperations(now);
            if (_incoming.IsEmpty && _remoteDeferred.Count == 0) return;

            // One Temp batch in flight at a time (a course built before the previous batch's
            // nodes/edges are query-able could not connect to them), and never on the frame the
            // player's own gesture applies. A selected tool is allowed while its preview is being
            // regenerated or cleared; only the single frame that commits a local Apply has priority.
            if (!CanBuildDefinitions) return;

            // One source Apply may emit several native courses. Keep that operation intact: a
            // junction or point-mode network object is not equivalent to a sequence of independent
            // clicks, and applying only a prefix lets intermediate node reduction deform the rest.
            List<SimulationCommandMessage> work;
            bool nativeOperation;
            if (!TryTakeCompleteOperation(session, now, out work, out nativeOperation)) return;

            NetOperationKey completedKey = default(NetOperationKey);
            bool hasCompletedKey = false;
            if (nativeOperation && work.Count > 0)
            {
                NetPlacementCommand completedHeader = NetPlacementCommand.Decode(work[0].Body);
                completedKey = new NetOperationKey
                {
                    Origin = work[0].OriginPlayerId,
                    Operation = completedHeader.OperationId,
                };
                if (_completedNetOperations.Contains(completedKey, now))
                {
                    Diagnostics.FlightRecorder.Note("net operation duplicate suppressed op=" +
                                                      completedHeader.OperationId);
                    return;
                }
                hasCompletedKey = true;
            }

            int maxBatch = work.Count;

            NativeArray<Entity> nodeEntities = default, edgeEntities = default,
                ownedNodeEntities = default, ownedEdgeEntities = default;
            NativeArray<Node> nodeData = default, ownedNodeData = default;
            NativeArray<Curve> edgeCurves = default, ownedEdgeCurves = default;
            TerrainHeightData heightData = default;
            WaterSurfaceData<SurfaceWater> waterData = default;
            bool haveSnapshot = false;
            int built = 0;
            bool splitUsed = false;
            PreparedNativeCourse[] preparedNative = nativeOperation
                ? new PreparedNativeCourse[work.Count]
                : null;
            var createdDefinitions = new List<Entity>(work.Count);
            var realizedCourses = new List<RealizedCourse>(work.Count);
            bool abortWholeOperation = false;
            string abortReason = null;
            long constructionCost = 0;
            int chargedCourses = 0;

            // Source messages of the courses the Temp batch builds, retained until the commit
            // actually runs: if the armed batch is wiped before committing (see _onCommitLost) they
            // are re-enqueued and the batch rebuilds instead of being lost.
            List<SimulationCommandMessage> retained = null;

            // New nodes / edges the Temp batch will create, so a later course can recognise (a) an
            // endpoint that coincides with one of our pending new nodes — it will MERGE, so it is not
            // a split — and (b) an endpoint that taps the middle of a pending batch edge, which must
            // wait until that edge is real (deferred to the next, post-commit cycle).
            var batchNewNodes = new NativeList<float3>(maxBatch, Allocator.Temp);
            var batchEdges = new NativeList<Bezier4x3>(maxBatch, Allocator.Temp);
            try
            {
                if (nativeOperation)
                {
                    // Resolve every external target before creating the first definition. If course
                    // N depends on geometry that has not arrived yet, committing courses 0..N-1 and
                    // retrying only the suffix would destroy the source operation's junction shape.
                    nodeEntities = _existingNodes.ToEntityArray(Allocator.Temp);
                    nodeData = _existingNodes.ToComponentDataArray<Node>(Allocator.Temp);
                    edgeEntities = _existingEdges.ToEntityArray(Allocator.Temp);
                    edgeCurves = _existingEdges.ToComponentDataArray<Curve>(Allocator.Temp);
                    ownedNodeEntities = _ownedNodes.ToEntityArray(Allocator.Temp);
                    ownedNodeData = _ownedNodes.ToComponentDataArray<Node>(Allocator.Temp);
                    ownedEdgeEntities = _ownedEdges.ToEntityArray(Allocator.Temp);
                    ownedEdgeCurves = _ownedEdges.ToComponentDataArray<Curve>(Allocator.Temp);
                    _terrainSystem.GetHeightData(waitForPending: true);
                    heightData = _terrainSystem.GetHeightData(waitForPending: true);
                    JobHandle preflightWaterDeps;
                    waterData = _waterSystem.GetSurfaceData(out preflightWaterDeps);
                    preflightWaterDeps.Complete();
                    haveSnapshot = true;

                    NetPlacementCommand operationHeader = NetPlacementCommand.Decode(work[0].Body);
                    var operationRetryKey = new NetOperationKey
                    {
                        Origin = work[0].OriginPlayerId,
                        Operation = operationHeader.OperationId,
                    };
                    bool unresolvedOperationTarget = false;
                    int alreadyBuiltCourses = 0;

                    for (int i = 0; i < work.Count; i++)
                    {
                        NetPlacementCommand command;
                        try { command = NetPlacementCommand.Decode(work[i].Body); }
                        catch (System.Exception ex)
                        {
                            Mod.log.Warn("[MP] NetSync: native operation became malformed during preflight: " +
                                         ex.Message + "; dropping whole operation.");
                            return;
                        }

                        Entity prefab;
                        if (!_prefabIndex.TryResolve(command.PrefabName, out prefab) ||
                            !EntityManager.HasComponent<global::Game.Prefabs.NetData>(prefab) ||
                            !EntityManager.HasComponent<global::Game.Prefabs.NetGeometryData>(prefab))
                        {
                            Mod.log.Warn("[MP] NetSync: native operation references unavailable net prefab '" +
                                         command.PrefabName + "'; dropping whole operation.");
                            return;
                        }
                        if (!string.IsNullOrEmpty(command.SubPrefabName))
                        {
                            Entity subPrefab;
                            if (!_prefabIndex.TryResolve(command.SubPrefabName, out subPrefab) ||
                                !EntityManager.HasComponent<global::Game.Prefabs.NetLaneData>(subPrefab))
                            {
                                Mod.log.Warn("[MP] NetSync: native operation references unavailable lane prefab '" +
                                             command.SubPrefabName + "'; dropping whole operation.");
                                return;
                            }
                        }

                        var curve = new Bezier4x3
                        {
                            a = new float3(command.Ax, command.Ay, command.Az),
                            b = new float3(command.Bx, command.By, command.Bz),
                            c = new float3(command.Cx, command.Cy, command.Cz),
                            d = new float3(command.Dx, command.Dy, command.Dz),
                        };
                        float measuredLength = MathUtils.Length(curve);
                        const uint pointFlags = (uint)(global::Game.Tools.CoursePosFlags.IsFirst |
                                                       global::Game.Tools.CoursePosFlags.IsLast);
                        bool nativePoint = measuredLength < 0.1f &&
                                           (command.Start.Flags & pointFlags) == pointFlags &&
                                           (command.End.Flags & pointFlags) == pointFlags;
                        if (!math.isfinite(measuredLength) || (measuredLength < 0.1f && !nativePoint))
                        {
                            Mod.log.Warn("[MP] NetSync: native operation " + command.OperationId +
                                         " contains a degenerate course; dropping the whole operation.");
                            return;
                        }

                        // Preserve the source NetCourse length exactly, but reject a forged or
                        // corrupt scalar that materially disagrees with the transmitted curve.
                        float lengthTolerance = math.max(0.05f, measuredLength * 0.01f);
                        if (math.abs(command.Length - measuredLength) > lengthTolerance)
                        {
                            Mod.log.Warn("[MP] NetSync: native operation " + command.OperationId +
                                         " has an inconsistent course length; dropping the whole operation.");
                            return;
                        }

                        const CreationFlags allowedNativeFlags = CreationFlags.Invert |
                            CreationFlags.Align | CreationFlags.Hidden | CreationFlags.Optional |
                            CreationFlags.Lowered | CreationFlags.Native |
                            CreationFlags.Construction | CreationFlags.SubElevation;
                        if ((((CreationFlags)command.CreationFlags) & ~allowedNativeFlags) != 0)
                        {
                            Mod.log.Warn("[MP] NetSync: native operation " + command.OperationId +
                                         " contains an unsafe creation mode; dropping the whole operation.");
                            SyncInbox.RequestResync("unsafe native net creation flags");
                            return;
                        }
                        // NetCourse elevations are exact native generator state, not values limited
                        // by PlaceableNetData's UI range. Snaps and underground transitions can
                        // legitimately exceed that range. The wire decoder already rejects every
                        // non-finite or globally implausible value, so preserve these values intact.

                        bool alreadyBuilt = !nativePoint &&
                                            SpanAlreadyBuilt(prefab, curve, edgeEntities, edgeCurves);
                        if (alreadyBuilt) alreadyBuiltCourses++;
                        preparedNative[i] = new PreparedNativeCourse
                        {
                            Command = command,
                            Prefab = prefab,
                            Curve = curve,
                            MeasuredLength = measuredLength,
                            Point = nativePoint,
                            AlreadyBuilt = alreadyBuilt,
                        };

                        // A course already present is this operation's idempotent portion. It needs
                        // no source target, and the remaining missing courses reconcile atomically.
                        if (alreadyBuilt) continue;

                        NetPrefabInfo placedInfo = NetInfoOf(prefab);
                        bool resolved = true;
                        Entity ignoredEntity;
                        float ignoredT;
                        int ignoredKind;
                        if (HasExternalNativeTarget(command.Start.Kind))
                            resolved &= TryResolveNativeEndpoint(command.Start, placedInfo,
                                nodeEntities, nodeData, edgeEntities, edgeCurves,
                                ownedNodeEntities, ownedNodeData, ownedEdgeEntities, ownedEdgeCurves,
                                out ignoredEntity, out ignoredT, out ignoredKind);
                        if (HasExternalNativeTarget(command.End.Kind))
                            resolved &= TryResolveNativeEndpoint(command.End, placedInfo,
                                nodeEntities, nodeData, edgeEntities, edgeCurves,
                                ownedNodeEntities, ownedNodeData, ownedEdgeEntities, ownedEdgeCurves,
                                out ignoredEntity, out ignoredT, out ignoredKind);
                        if (!resolved) unresolvedOperationTarget = true;
                    }

                    if (alreadyBuiltCourses == work.Count)
                    {
                        _nativeOperationDeadlines.Remove(operationRetryKey);
                        _operationBuildFailures.Remove(operationRetryKey);
                        Diagnostics.FlightRecorder.Note("net native op already present=" +
                                                          operationHeader.OperationId +
                                                          " courses=" + work.Count);
                        _completedNetOperations.Remember(operationRetryKey, now, 60000);
                        return;
                    }
                    if (alreadyBuiltCourses > 0)
                        Diagnostics.FlightRecorder.Note("net native op reconcile existing=" +
                                                          alreadyBuiltCourses + "/" + work.Count);

                    if (unresolvedOperationTarget)
                    {
                        long deadline;
                        if (!_nativeOperationDeadlines.TryGetValue(operationRetryKey, out deadline))
                        {
                            deadline = now + NativeTargetRetryWindowMs;
                            _nativeOperationDeadlines[operationRetryKey] = deadline;
                        }
                        if (now < deadline)
                        {
                            RequeueAtFront(work);
                            return;
                        }

                        _nativeOperationDeadlines.Remove(operationRetryKey);
                        Mod.log.Warn("[MP] NetSync: native operation " + operationHeader.OperationId +
                                     " has an unresolved target after its retry window; rejecting " +
                                     "the complete operation and requesting world recovery.");
                        Diagnostics.FlightRecorder.Note("net native operation rejected/resync op=" +
                                                          operationHeader.OperationId);
                        SyncInbox.RequestResync("native net target did not resolve");
                        return;
                    }
                    else _nativeOperationDeadlines.Remove(operationRetryKey);
                }

                for (int i = 0; i < work.Count; i++)
                {
                    SimulationCommandMessage message = work[i];
                    if (message.OriginPlayerId == session.LocalPlayerId)
                    {
                        continue;
                    }

                    NetPlacementCommand command;
                    Entity prefab;
                    Bezier4x3 bezier;
                    float measuredLength;
                    bool nativePoint;
                    if (nativeOperation)
                    {
                        PreparedNativeCourse prepared = preparedNative[i];
                        if (prepared.AlreadyBuilt) continue;
                        command = prepared.Command;
                        prefab = prepared.Prefab;
                        bezier = prepared.Curve;
                        measuredLength = prepared.MeasuredLength;
                        nativePoint = prepared.Point;
                    }
                    else
                    {
                        try { command = NetPlacementCommand.Decode(message.Body); }
                        catch (System.Exception ex)
                        {
                            Mod.log.Warn("[MP] NetSync: dropping malformed command: " + ex.Message);
                            continue;
                        }

                        if (!_prefabIndex.TryResolve(command.PrefabName, out prefab) ||
                            !EntityManager.HasComponent<global::Game.Prefabs.NetData>(prefab) ||
                            !EntityManager.HasComponent<global::Game.Prefabs.NetGeometryData>(prefab))
                        {
                            Mod.log.Warn("[MP] NetSync realize: unavailable net prefab '" +
                                         command.PrefabName + "' from player " +
                                         message.OriginPlayerId + "; skipping.");
                            continue;
                        }

                        bezier = new Bezier4x3
                        {
                            a = new float3(command.Ax, command.Ay, command.Az),
                            b = new float3(command.Bx, command.By, command.Bz),
                            c = new float3(command.Cx, command.Cy, command.Cz),
                            d = new float3(command.Dx, command.Dy, command.Dz),
                        };
                        measuredLength = MathUtils.Length(bezier);
                        nativePoint = false;
                        if (!math.isfinite(measuredLength) || measuredLength < 0.1f)
                        {
                            Mod.log.Warn("[MP] NetSync realize: degenerate fallback course for '" +
                                         command.PrefabName + "'; skipping.");
                            continue;
                        }
                        // Geometry-only fallback has no exact native length, so derive it locally.
                        command.Length = measuredLength;
                    }

                    float3 a = bezier.a;
                    float3 d = bezier.d;

                    if (!haveSnapshot)
                    {
                        nodeEntities = _existingNodes.ToEntityArray(Allocator.Temp);
                        nodeData = _existingNodes.ToComponentDataArray<Node>(Allocator.Temp);
                        edgeEntities = _existingEdges.ToEntityArray(Allocator.Temp);
                        edgeCurves = _existingEdges.ToComponentDataArray<Curve>(Allocator.Temp);
                        // Building sub-net stubs a utility endpoint may connect to (FindUtilityNodeAt).
                        ownedNodeEntities = _ownedNodes.ToEntityArray(Allocator.Temp);
                        ownedNodeData = _ownedNodes.ToComponentDataArray<Node>(Allocator.Temp);
                        ownedEdgeEntities = _ownedEdges.ToEntityArray(Allocator.Temp);
                        ownedEdgeCurves = _ownedEdges.ToComponentDataArray<Curve>(Allocator.Temp);
                        // Surface samplers for the courses' endpoint elevations (see EndElevation).
                        // The water dependency completes here so the data is main-thread readable;
                        // between simulation steps the handle is already complete.
                        _terrainSystem.GetHeightData(waitForPending: true);
                        heightData = _terrainSystem.GetHeightData(waitForPending: true);
                        JobHandle waterDeps;
                        waterData = _waterSystem.GetSurfaceData(out waterDeps);
                        waterDeps.Complete();
                        haveSnapshot = true;
                    }

                    // Idempotence: skip a span this machine already has as live same-prefab geometry.
                    // The game's node reduction can merge a committed span into a neighbour and
                    // re-surface it as a wider create on the other machine; without this check that
                    // echo would stack a duplicate road on top of the existing one (and ping-pong).
                    // The tolerances are SplitMatch-tight (~1 m), far below a parallel lane, and a
                    // span rebuilt at another elevation fails the height match — never wrongly skipped.
                    if (!nativeOperation && SpanAlreadyBuilt(prefab, bezier, edgeEntities, edgeCurves))
                    {
                        if (command.HasNativeCourse)
                            _nativeTargetDeadlines.Remove(NativeRetryKey(message, command));
                        continue;
                    }

                    NetPrefabInfo placedInfo = NetInfoOf(prefab);
                    int startKind, endKind;
                    float startT, endT;
                    Entity startSnap, endSnap;
                    bool nativeTargetsResolved = true;

                    if (command.HasNativeCourse)
                    {
                        if (command.Start.Kind == NetEndpointTargetKind.Infer)
                            startSnap = ClassifyEndpoint(a, placedInfo, nodeEntities, nodeData,
                                edgeEntities, edgeCurves, ownedNodeEntities, ownedNodeData,
                                batchNewNodes, batchEdges, out startT, out startKind);
                        else
                            nativeTargetsResolved &= TryResolveNativeEndpoint(command.Start, placedInfo,
                                nodeEntities, nodeData, edgeEntities, edgeCurves,
                                ownedNodeEntities, ownedNodeData, ownedEdgeEntities, ownedEdgeCurves,
                                out startSnap, out startT, out startKind);

                        if (command.End.Kind == NetEndpointTargetKind.Infer)
                            endSnap = ClassifyEndpoint(d, placedInfo, nodeEntities, nodeData,
                                edgeEntities, edgeCurves, ownedNodeEntities, ownedNodeData,
                                batchNewNodes, batchEdges, out endT, out endKind);
                        else
                            nativeTargetsResolved &= TryResolveNativeEndpoint(command.End, placedInfo,
                                nodeEntities, nodeData, edgeEntities, edgeCurves,
                                ownedNodeEntities, ownedNodeData, ownedEdgeEntities, ownedEdgeCurves,
                                out endSnap, out endT, out endKind);

                        NativeTargetRetryKey retryKey = NativeRetryKey(message, command);
                        if (!nativeTargetsResolved)
                        {
                            // The operation-level preflight resolved every external target against
                            // this same snapshot. If one vanished now, do not leave an already-built
                            // prefix behind; retry the complete source operation on a fresh frame.
                            _nativeTargetDeadlines.Remove(retryKey);
                            abortWholeOperation = true;
                            abortReason = "a native target changed after operation preflight";
                            break;
                        }
                        else
                        {
                            _nativeTargetDeadlines.Remove(retryKey);
                        }
                    }
                    else
                    {
                        if (command.HasNativeCourse)
                            _nativeTargetDeadlines.Remove(NativeRetryKey(message, command));
                        startSnap = ClassifyEndpoint(a, placedInfo, nodeEntities, nodeData,
                            edgeEntities, edgeCurves, ownedNodeEntities, ownedNodeData,
                            batchNewNodes, batchEdges, out startT, out startKind);
                        endSnap = ClassifyEndpoint(d, placedInfo, nodeEntities, nodeData,
                            edgeEntities, edgeCurves, ownedNodeEntities, ownedNodeData,
                            batchNewNodes, batchEdges, out endT, out endKind);
                    }

                    // The elevation each course end must carry (a reused node's committed value, or
                    // derived from the transmitted Y against the local surface — see EndElevation).
                    float2 startElevation = command.HasNativeCourse
                        ? new float2(command.Start.ElevationLeft, command.Start.ElevationRight)
                        : EndElevation(prefab, startSnap, startKind, a, ref heightData, ref waterData);
                    float2 endElevation = command.HasNativeCourse
                        ? new float2(command.End.ElevationLeft, command.End.ElevationRight)
                        : EndElevation(prefab, endSnap, endKind, d, ref heightData, ref waterData);

                    // A captured native operation is the exact set the source applied together, so
                    // its courses stay together even when one references geometry another course in
                    // that same operation creates. Geometry-only fallback commands remain serialized.
                    bool defer = !nativeOperation &&
                                 (startKind == KindDeferBatchEdge || endKind == KindDeferBatchEdge);
                    bool splittingCourse = startKind == KindSplit || endKind == KindSplit;
                    // A course whose BODY crosses or hugs an existing edge splits it at Temp generation
                    // exactly like an endpoint tap, but ClassifyEndpoint only sees the two endpoints —
                    // probe the span interior too, or two quick drags across the same road slip into one
                    // batch and hit the stale-edge crash below.
                    if (!defer && !splittingCourse)
                        splittingCourse = BodyTouchesExistingEdge(bezier, placedInfo, edgeEntities, edgeCurves);
                    // At most ONE existing-edge-splitting course per batch: two courses committed in the
                    // same ApplyTool pass that both touch an existing edge can make ApplyNetSystem
                    // dereference a stale (already-split/deleted) edge and crash the process natively.
                    // Courses touching nothing pre-existing are unbounded (safe — the net tool grids
                    // many at once).
                    if (!defer && splittingCourse && splitUsed && !nativeOperation) defer = true;

                    if (defer)
                    {
                        // Re-queue this and every remaining item, in order, for the next cycle - after
                        // this frame's committed edges have become query-able.
                        RequeueFrom(work, i);
                        break;
                    }

                    try
                    {
                        // All replicated courses use the same Temp/apply transaction as the source.
                        // The former Permanent shortcut could not recover a missed contact or split
                        // and exposed half-realized geometry to dependent commands in this frame.
                        if (built == 0) PrepareDefinitionFrame();
                        Entity definition;
                        if (command.HasNativeCourse)
                            definition = CreateNativeCourse(prefab, command, bezier,
                                startSnap, startT, startKind, endSnap, endT, endKind);
                        else
                            definition = CreateCourse(prefab, bezier, command.Length,
                                startSnap, startT, endSnap, endT,
                                startElevation, endElevation);
                        createdDefinitions.Add(definition);
                        built++;
                        (retained ?? (retained = new List<SimulationCommandMessage>())).Add(message);
                        if (splittingCourse) splitUsed = true;
                        if (startKind == KindFree) batchNewNodes.Add(a);
                        if (endKind == KindFree) batchNewNodes.Add(d);
                        if (!nativePoint) batchEdges.Add(bezier);
                        realizedCourses.Add(new RealizedCourse
                        {
                            Prefab = prefab,
                            PrefabName = command.PrefabName,
                            Curve = bezier,
                            Length = command.Length,
                            Charge = !command.HasNativeCourse ||
                                     ((((global::Game.Tools.CoursePosFlags)command.Start.Flags |
                                        (global::Game.Tools.CoursePosFlags)command.End.Flags) &
                                       global::Game.Tools.CoursePosFlags.DontCreate) == 0),
                            StartSnap = startSnap,
                            EndSnap = endSnap,
                            StartT = startT,
                            EndT = endT,
                            StartKind = startKind,
                            EndKind = endKind,
                        });
                    }
                    catch (System.Exception ex)
                    {
                        if (nativeOperation)
                        {
                            abortWholeOperation = true;
                            abortReason = "course " + command.CourseIndex + " definition failed (" +
                                          ex.GetType().Name + ")";
                            break;
                        }
                        Mod.log.Error("[MP] NetSync realize FAILED for '" + command.PrefabName +
                                      "': " + ex);
                    }
                }

                if (abortWholeOperation)
                {
                    for (int i = 0; i < createdDefinitions.Count; i++)
                    {
                        Entity definition = createdDefinitions[i];
                        if (EntityManager.Exists(definition)) EntityManager.DestroyEntity(definition);
                    }
                    built = 0;
                    retained = null;
                    NetPlacementCommand header = preparedNative[0].Command;
                    var failureKey = new NetOperationKey
                    {
                        Origin = work[0].OriginPlayerId,
                        Operation = header.OperationId,
                    };
                    int failures;
                    _operationBuildFailures.TryGetValue(failureKey, out failures);
                    failures++;
                    bool retry = failures <= 3;
                    if (retry)
                    {
                        _operationBuildFailures[failureKey] = failures;
                        RequeueAtFront(work);
                    }
                    else
                    {
                        _operationBuildFailures.Remove(failureKey);
                    }
                    ReleaseTrackedTemps(_isolatedLocalTemps);
                    ForceActiveToolUpdate();
                    Mod.log.Warn("[MP] NetSync: native operation rolled back before generation - " +
                                 abortReason + (retry
                                     ? "; retrying the whole operation (" + failures + "/3)."
                                     : "; dropped after 3 retries."));
                    Diagnostics.FlightRecorder.Note("net native op rollback before generation retry=" +
                                                      (retry ? failures : 0));
                    return;
                }

                if (nativeOperation)
                {
                    NetPlacementCommand header = preparedNative[0].Command;
                    _operationBuildFailures.Remove(new NetOperationKey
                    {
                        Origin = work[0].OriginPlayerId,
                        Operation = header.OperationId,
                    });
                }

                // Accumulate the operation only after every selected definition exists. The actual
                // host treasury update is one write after this Temp transaction has drained, so a
                // failed/replayed later grid or parallel course cannot leave a partial charge.
                try
                {
                    for (int i = 0; i < realizedCourses.Count; i++)
                    {
                        RealizedCourse realized = realizedCourses[i];
                        if (!realized.Charge) continue;
                        constructionCost += ConstructionCharger.CalculateNetCost(
                            EntityManager, realized.Prefab, realized.Length);
                        chargedCourses++;
                    }
                }
                catch (System.Exception ex)
                {
                    constructionCost = 0;
                    chargedCourses = 0;
                    Mod.log.Warn("[MP] NetSync: could not calculate remote net charge: " + ex.Message);
                }
                // Publish echo guards and diagnostics only after every definition selected for this
                // operation exists. A failed later course therefore cannot leave a phantom realized
                // span suppressing unrelated local capture.
                for (int i = 0; i < realizedCourses.Count; i++)
                {
                    RealizedCourse realized = realizedCourses[i];
                    MarkRealizeGuards(realized.PrefabName, realized.Curve.a, realized.Curve.d,
                        realized.StartSnap, realized.StartKind, realized.StartT,
                        realized.EndSnap, realized.EndKind, realized.EndT, now);
                    RecordRealizedSpan(realized.Curve);
                    _rzSegments++;
                    TallyEnd(realized.StartKind);
                    TallyEnd(realized.EndKind);
                }
            }
            finally
            {
                if (haveSnapshot)
                {
                    nodeEntities.Dispose(); nodeData.Dispose(); edgeEntities.Dispose(); edgeCurves.Dispose();
                    ownedNodeEntities.Dispose(); ownedNodeData.Dispose();
                    ownedEdgeEntities.Dispose(); ownedEdgeCurves.Dispose();
                }
                batchNewNodes.Dispose();
                batchEdges.Dispose();
            }

            if (built == 0 && _isolatedLocalTemps.Count > 0)
            {
                ReleaseTrackedTemps(_isolatedLocalTemps);
                ForceActiveToolUpdate();
            }

            // Arm the commit for the Temp batch: those definitions become Temp edges at this frame's
            // Modification, and the next quiet frame applies that isolated set through the net domain.
            if (built > 0)
            {
                _pendingApply = true;
                _pendingTransactionKind = RemoteToolTransactionKind.Net;
                _armTick = System.Environment.TickCount;
                _pendingNetConstructionCharge = constructionCost;
                _pendingNetConstructionChargeCourses = chargedCourses;
                // A partially reconciled native operation may have skipped courses that were
                // already present locally. If this commit is lost, replay the complete source
                // operation so it can be assembled atomically again; replaying only the missing
                // fragments could never satisfy CourseCount.
                List<SimulationCommandMessage> batchSources = nativeOperation
                    ? new List<SimulationCommandMessage>(work)
                    : retained;
                _onCommitLost = delegate
                {
                    RequeueAtFront(batchSources);
                };
                if (hasCompletedKey)
                {
                    NetOperationKey completionKey = completedKey;
                    _onCommitComplete = delegate
                    {
                        long completedNow = Mod.Service != null ? Mod.Service.NowMs : now;
                        _completedNetOperations.Remember(completionKey, completedNow, 60000);
                        Diagnostics.FlightRecorder.Note("net operation committed/drained op=" +
                                                          completionKey.Operation);
                    };
                }
                Diagnostics.FlightRecorder.Note("net build batch armed n=" + built + (splitUsed ? " +split" : ""));
            }
        }

        private void PruneCompletedNetOperations(long now)
        {
            _completedNetOperations.Prune(now);
        }

        /// <summary>
        /// Re-queue <paramref name="work"/>[<paramref name="from"/>..] ahead of the shared inbox.
        /// </summary>
        private void RequeueFrom(List<SimulationCommandMessage> work, int from)
        {
            if (from < work.Count)
                _remoteDeferred.InsertRange(0, work.GetRange(from, work.Count - from));
        }

        private static bool HasExternalNativeTarget(NetEndpointTargetKind kind) =>
            kind == NetEndpointTargetKind.Node || kind == NetEndpointTargetKind.Edge ||
            kind == NetEndpointTargetKind.OwnedNode || kind == NetEndpointTargetKind.OwnedEdge;

        /// <summary>
        /// Pull one complete source operation from the ordered command streams. Messages belonging
        /// to later operations may be encountered while waiting for an interleaved course; they are
        /// returned to the simulation-thread prefix in their original order. An incomplete operation
        /// waits briefly and is then dropped as a whole, never realized as broken geometry.
        /// </summary>
        private bool TryTakeCompleteOperation(MultiplayerSession session, long now,
            out List<SimulationCommandMessage> operation, out bool nativeOperation)
        {
            operation = null;
            nativeOperation = false;

            const int MaxScan = NetInboxCap;
            var scanned = new List<SimulationCommandMessage>();
            NetOperationKey key = default(NetOperationKey);
            int expected = 0;
            SimulationCommandMessage[] courses = null;
            NetPlacementCommand[] decodedCourses = null;
            int received = 0;

            for (int scan = 0; scan < MaxScan && (expected == 0 || received < expected); scan++)
            {
                SimulationCommandMessage message;
                if (!TryTakeNextPlacementMessage(out message)) break;
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                NetPlacementCommand command;
                try { command = NetPlacementCommand.Decode(message.Body); }
                catch (System.Exception ex)
                {
                    Mod.log.Warn("[MP] NetSync: dropping malformed command: " + ex.Message);
                    continue;
                }

                scanned.Add(message);
                if (expected == 0)
                {
                    key = new NetOperationKey
                    {
                        Origin = message.OriginPlayerId,
                        Operation = command.OperationId,
                    };
                    expected = command.CourseCount;
                    courses = new SimulationCommandMessage[expected];
                    decodedCourses = new NetPlacementCommand[expected];
                }

                if (message.OriginPlayerId != key.Origin || command.OperationId != key.Operation)
                    continue;
                if (command.CourseCount != expected)
                {
                    Mod.log.Warn("[MP] NetSync: dropping inconsistent course count for op=" +
                                 key.Operation + " from player " + key.Origin + ".");
                    continue;
                }

                int index = command.CourseIndex;
                if (courses[index] != null) continue;
                courses[index] = message;
                decodedCourses[index] = command;
                received++;
            }

            if (expected == 0) return false;

            if (received != expected)
            {
                long deadline;
                if (!_operationAssemblyDeadlines.TryGetValue(key, out deadline))
                {
                    deadline = now + OperationAssemblyWindowMs;
                    _operationAssemblyDeadlines[key] = deadline;
                }

                if (now < deadline)
                {
                    RequeueAtFront(scanned);
                    return false;
                }

                _operationAssemblyDeadlines.Remove(key);
                var later = new List<SimulationCommandMessage>();
                for (int i = 0; i < scanned.Count; i++)
                {
                    NetPlacementCommand command;
                    try { command = NetPlacementCommand.Decode(scanned[i].Body); }
                    catch { continue; }
                    if (scanned[i].OriginPlayerId != key.Origin || command.OperationId != key.Operation)
                        later.Add(scanned[i]);
                }
                RequeueAtFront(later);
                Mod.log.Warn("[MP] NetSync: incomplete operation " + key.Operation + " from player " +
                             key.Origin + " expired (" + received + "/" + expected + "); dropped whole operation.");
                Diagnostics.FlightRecorder.Note("net incomplete op dropped=" + key.Operation +
                    " courses=" + received + "/" + expected);
                SyncInbox.RequestResync("incomplete net operation expired");
                return false;
            }

            _operationAssemblyDeadlines.Remove(key);
            operation = new List<SimulationCommandMessage>(expected);
            nativeOperation = true;
            bool hasNativeCourse = false;
            bool hasGeometryOnlyCourse = false;
            for (int i = 0; i < expected; i++)
            {
                operation.Add(courses[i]);
                nativeOperation &= decodedCourses[i].HasNativeCourse;
                hasNativeCourse |= decodedCourses[i].HasNativeCourse;
                hasGeometryOnlyCourse |= !decodedCourses[i].HasNativeCourse;
            }

            // Preserve later operations in their original receive order. Extra messages carrying
            // the completed key are duplicates or inconsistent fragments and are discarded.
            var deferred = new List<SimulationCommandMessage>();
            for (int i = 0; i < scanned.Count; i++)
            {
                NetPlacementCommand command;
                try { command = NetPlacementCommand.Decode(scanned[i].Body); }
                catch { continue; }
                if (scanned[i].OriginPlayerId == key.Origin && command.OperationId == key.Operation)
                    continue;
                deferred.Add(scanned[i]);
            }
            RequeueAtFront(deferred);

            // Current senders only group exact native definitions. Geometry-only capture represents
            // one final edge per command. Rejecting mixed or grouped fallback input prevents a peer
            // from smuggling a partially native operation into per-course fallback realization.
            if ((hasNativeCourse && hasGeometryOnlyCourse) || (expected > 1 && !nativeOperation))
            {
                Mod.log.Warn("[MP] NetSync: operation " + key.Operation + " from player " +
                             key.Origin + " mixed incompatible course encodings; dropped whole operation.");
                Diagnostics.FlightRecorder.Note("net incompatible multi-course op dropped=" +
                                                  key.Operation);
                SyncInbox.RequestResync("incompatible net operation rejected");
                operation = null;
                nativeOperation = false;
                return false;
            }
            return true;
        }

        private bool TryTakeNextPlacementMessage(out SimulationCommandMessage message)
        {
            if (DeferForTerrain)
            {
                message = default(SimulationCommandMessage);
                return false;
            }
            if (_remoteDeferred.Count > 0)
            {
                message = _remoteDeferred[0];
                _remoteDeferred.RemoveAt(0);
                return true;
            }
            return _incoming.TryDequeue(out message);
        }

        private void RequeueAtFront(List<SimulationCommandMessage> messages)
        {
            if (messages != null && messages.Count > 0)
                _remoteDeferred.InsertRange(0, messages);
        }

        private static NativeTargetRetryKey NativeRetryKey(SimulationCommandMessage message,
            NetPlacementCommand command)
        {
            return new NativeTargetRetryKey
            {
                Origin = message.OriginPlayerId,
                Operation = command.OperationId,
                Course = command.CourseIndex,
            };
        }

        /// <summary>
        /// True when every point of <paramref name="span"/> already lies on live same-prefab geometry
        /// - five samples along the curve, each of which must sit on SOME existing edge of that prefab
        /// (the span may map to several local sub-edges). Uses the tight SplitMatch tolerances so a
        /// parallel road or a span rebuilt at another elevation is never wrongly treated as a
        /// duplicate.
        /// </summary>
        private bool SpanAlreadyBuilt(Entity prefab, Bezier4x3 span,
            NativeArray<Entity> edgeEntities, NativeArray<Curve> edgeCurves)
        {
            for (int s = 0; s <= 4; s++)
            {
                float3 p = MathUtils.Position(span, s / 4f);
                bool covered = false;
                for (int i = 0; i < edgeCurves.Length; i++)
                {
                    Bezier4x3 bez = edgeCurves[i].m_Bezier;
                    float t;
                    if (MathUtils.Distance(bez.xz, p.xz, out t) > SplitMatch.TolXZ) continue;
                    if (math.abs(MathUtils.Position(bez, t).y - p.y) > SplitMatch.TolY) continue;
                    if (EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(edgeEntities[i]).m_Prefab
                        != prefab) continue;
                    covered = true;
                    break;
                }
                if (!covered) return false;
            }
            return true;
        }

        /// <summary>
        /// True when the course's interior (away from both endpoints, which
        /// <see cref="ClassifyEndpoint"/> already resolved) comes within splitting range of any
        /// existing edge — a transversal crossing or a lengthwise overlap. The game cuts every such
        /// edge during Temp generation, so the course counts against the one-splitting-course-per-batch
        /// rule even though neither endpoint classifies as a split. The fallback probe uses native
        /// connection layers and physical widths; a conservative false positive only serializes work,
        /// while a false negative could place two conflicting split courses in one commit.
        /// </summary>
        private bool BodyTouchesExistingEdge(Bezier4x3 course, NetPrefabInfo placedInfo,
            NativeArray<Entity> edgeEntities, NativeArray<Curve> edgeCurves)
        {
            // The control hull contains the curve, so an expanded-AABB miss is an exact reject.
            float3 lo = math.min(math.min(course.a, course.b), math.min(course.c, course.d))
                - new float3(MaxEndpointSearch, VerticalSnapTol, MaxEndpointSearch);
            float3 hi = math.max(math.max(course.a, course.b), math.max(course.c, course.d))
                + new float3(MaxEndpointSearch, VerticalSnapTol, MaxEndpointSearch);

            // Sample tightly enough (≈ EdgeSnapDistance apart, via the control-polygon length upper
            // bound) that a perpendicular crossing cannot slip between two samples.
            float approxLen = math.distance(course.a, course.b) + math.distance(course.b, course.c)
                + math.distance(course.c, course.d);
            int samples = math.clamp((int)(approxLen / EdgeSnapDistance), 8, 128);

            for (int i = 0; i < edgeCurves.Length; i++)
            {
                Bezier4x3 bez = edgeCurves[i].m_Bezier;
                NetPrefabInfo targetInfo = default(NetPrefabInfo);
                if (EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(edgeEntities[i]))
                    targetInfo = NetInfoOf(EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(edgeEntities[i]).m_Prefab);
                if (!LayersCanConnect(placedInfo, targetInfo)) continue;
                float touchDistance = math.max(EdgeSnapDistance,
                    placedInfo.HalfWidth + EdgeHalfWidth(edgeEntities[i], targetInfo.HalfWidth) +
                    placedInfo.SnapDistance);
                float3 elo = math.min(math.min(bez.a, bez.b), math.min(bez.c, bez.d));
                float3 ehi = math.max(math.max(bez.a, bez.b), math.max(bez.c, bez.d));
                if (math.any(elo > hi) || math.any(ehi < lo)) continue;

                for (int s = 1; s < samples; s++)
                {
                    float3 p = MathUtils.Position(course, s / (float)samples);
                    // Endpoint neighbourhoods belong to endpoint classification (reuse/split/merge).
                    if (math.distance(p.xz, course.a.xz) < NodeSnapDistance) continue;
                    if (math.distance(p.xz, course.d.xz) < NodeSnapDistance) continue;
                    float t;
                    if (MathUtils.Distance(bez.xz, p.xz, out t) >= touchDistance) continue;
                    if (math.abs(MathUtils.Position(bez, t).y - p.y) > VerticalSnapTol) continue; // other level
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Resolve where one course endpoint connects, in priority order: an existing real node (reuse),
        /// a building's utility sub-net node (utility nets only - a power/pipe connector stub), a
        /// pending new node another course in this batch creates (merge), a pending batch edge it taps
        /// mid-span (defer until real), an existing real edge - reusing an end node for taps inside its
        /// end zone, splitting for interior taps - else free ground. Returns the snap entity (node to
        /// reuse, or edge to split, or Entity.Null) and, via out params, the split parameter and the
        /// <c>Kind*</c> classification.
        /// </summary>
        private Entity ClassifyEndpoint(float3 p, NetPrefabInfo placedInfo,
            NativeArray<Entity> nodeEntities, NativeArray<Node> nodeData,
            NativeArray<Entity> edgeEntities, NativeArray<Curve> edgeCurves,
            NativeArray<Entity> ownedNodeEntities, NativeArray<Node> ownedNodeData,
            NativeList<float3> batchNewNodes, NativeList<Bezier4x3> batchEdges,
            out float t, out int kind)
        {
            t = 0f;
            Entity node = FindNodeAt(p, placedInfo, nodeEntities, nodeData);
            if (node != Entity.Null) { kind = KindReuseNode; return node; }
            // A power line / pipe endpoint lying on a building's connector stub connects to it —
            // the sender drew it onto that stub, so the committed segment ends exactly there.
            if ((placedInfo.ConnectLayers & UtilityConnectLayers) != Layer.None)
            {
                node = FindUtilityNodeAt(p, ownedNodeEntities, ownedNodeData, placedInfo);
                if (node != Entity.Null) { kind = KindReuseConnector; return node; }
            }
            // Coincides with a new node another course in this batch creates -> leave it as a fresh node
            // (Entity.Null) and let GenerateNodesSystem merge the two by exact position.
            if (NearAny(p, batchNewNodes, NodeSnapDistance)) { kind = KindMergeBatch; return Entity.Null; }
            // Taps the middle of an edge this batch is still building -> can't split a not-yet-real edge;
            // defer the whole course to the next cycle, where that edge is real and this becomes a split.
            if (MidSpanOfAnyBatch(p, batchEdges)) { kind = KindDeferBatchEdge; return Entity.Null; }
            Entity edge, endNode;
            FindEdgeAt(p, placedInfo, edgeEntities, edgeCurves, out edge, out t, out endNode);
            // A tap inside an existing edge's end zone reuses that end's node (see FindEdgeAt).
            if (endNode != Entity.Null) { kind = KindReuseNode; return endNode; }
            if (edge != Entity.Null) { kind = KindSplit; return edge; }
            kind = KindFree;
            return Entity.Null;
        }

        /// <summary>
        /// Mark the echo-suppression guard for a course being realized. The capture side
        /// consumes the key of the committed edge's START (its <c>a</c> endpoint), but the
        /// committed geometry can differ from the command: an endpoint that reuses a node
        /// lands exactly ON that node - up to <see cref="NodeSnapDistance"/> from the
        /// commanded point, past the guard's 0.5 m buckets - a split lands on the split
        /// point, and the game may commit the edge with its endpoints swapped. So mark
        /// every position the committed start can be: both raw endpoints plus each end's
        /// resolved snap target. Stale extras simply age out (15 s TTL).
        /// </summary>
        private void MarkRealizeGuards(string prefabName, float3 a, float3 d,
            Entity startSnap, int startKind, float startT,
            Entity endSnap, int endKind, float endT, long now)
        {
            _guard.Mark(ReplicationGuard.Key(prefabName, a), now);
            _guard.Mark(ReplicationGuard.Key(prefabName, d), now);
            MarkResolvedEndpoint(prefabName, startSnap, startKind, startT, now);
            MarkResolvedEndpoint(prefabName, endSnap, endKind, endT, now);
        }

        private void MarkResolvedEndpoint(string prefabName, Entity snap, int kind, float t, long now)
        {
            if (snap == Entity.Null || !EntityManager.Exists(snap)) return;
            float3 position;
            if ((kind == KindReuseNode || kind == KindReuseConnector) && EntityManager.HasComponent<Node>(snap))
                position = EntityManager.GetComponentData<Node>(snap).m_Position;
            else if (kind == KindSplit && EntityManager.HasComponent<Curve>(snap))
                position = MathUtils.Position(EntityManager.GetComponentData<Curve>(snap).m_Bezier, t);
            else return;
            _guard.Mark(ReplicationGuard.Key(prefabName, position), now);
        }

        // Diagnostic tally by endpoint classification.
        private void TallyEnd(int kind)
        {
            switch (kind)
            {
                case KindReuseNode: _rzSnapEnds++; break;
                case KindReuseConnector: _rzSnapEnds++; break;
                case KindMergeBatch: _rzMergeEnds++; break;
                case KindSplit: _rzMidEnds++; break;
                default: _rzFreeEnds++; break;
            }
        }

        /// <summary>
        /// True when <paramref name="p"/> lies within <paramref name="tol"/> (XZ) of any point at a
        /// matching height. The height gate mirrors the game's node merge, which is by position - a
        /// batch containing both a ground road and a bridge above it must not classify the bridge's
        /// endpoint as merging with the ground node.
        /// </summary>
        private static bool NearAny(float3 p, NativeList<float3> points, float tol)
        {
            float2 xz = p.xz;
            float tolSq = tol * tol;
            for (int i = 0; i < points.Length; i++)
                if (math.distancesq(xz, points[i].xz) < tolSq
                    && math.abs(points[i].y - p.y) <= VerticalSnapTol) return true;
            return false;
        }

        /// <summary>
        /// True when <paramref name="point"/> taps the MIDDLE (away from both ends) of any curve this
        /// batch is creating - the same mid-span test as <see cref="FindEdgeAt"/>, against pending
        /// batch edges rather than real ones, with the same height gate (a crossing on another level
        /// is not a tap).
        /// </summary>
        private static bool MidSpanOfAnyBatch(float3 point, NativeList<Bezier4x3> curves)
        {
            float2 p = point.xz;
            for (int i = 0; i < curves.Length; i++)
            {
                Bezier4x3 bez = curves[i];
                float tt;
                if (MathUtils.Distance(bez.xz, p, out tt) >= EdgeSnapDistance) continue;
                float3 sp = MathUtils.Position(bez, tt);
                if (math.abs(sp.y - point.y) > VerticalSnapTol) continue;
                if (math.distance(sp.xz, bez.a.xz) < MinSplitOffset) continue;
                if (math.distance(sp.xz, bez.d.xz) < MinSplitOffset) continue;
                return true;
            }
            return false;
        }
    }
}
