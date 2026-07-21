using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Systems.Net;
using Game;
using Game.SceneFlow;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Runs world replacement as a distributed transaction:
    /// Begin -> every client quiesced -> local native work drained -> save -> epoch-tagged map ->
    /// every client loaded -> Resume. Requests are coalesced and a second save can never overtake
    /// an active map stream.
    /// </summary>
    public partial class WorldResyncSystem : GameSystemBase
    {
        private const long QuiesceTimeoutMs = 20000;
        private const long LoadTimeoutMs = 180000;
        private const int RequiredCleanFrames = 2;

        private enum RecoveryState : byte
        {
            Idle,
            WaitingForQuiescence,
            Saving,
            WaitingForLoaded,
        }

        private struct ControlEvent
        {
            public WorldSyncStage Stage;
            public long Epoch;
            public ConnectionId Connection;
        }

        private readonly ConcurrentQueue<ConnectionId> _requests =
            new ConcurrentQueue<ConnectionId>();
        private readonly ConcurrentQueue<ControlEvent> _controls =
            new ConcurrentQueue<ControlEvent>();
        private readonly ConcurrentQueue<ConnectionId> _leaves =
            new ConcurrentQueue<ConnectionId>();
        private readonly List<ConnectionId> _participants = new List<ConnectionId>();
        private readonly HashSet<int> _quiesced = new HashSet<int>();
        private readonly HashSet<int> _loaded = new HashSet<int>();

        private AutoSaveSystem _autoSave;
        private NetSyncSystem _netSync;
        private Observer _observer;
        private Task _saveTask;
        private RecoveryState _state;
        private bool _recoveryRequested;
        private bool _rerunRequested;
        private long _epochCounter;
        private long _epoch;
        private long _deadlineMs;
        private long _lastResyncMs = -1;
        private long _saveStartMs;
        private DateTime _saveStartUtc;
        private float _resumeSpeed;
        private int _cleanFrames;

        protected override void OnCreate()
        {
            base.OnCreate();
            Mod.log.Info(nameof(WorldResyncSystem) + " ready (atomic epoch barrier).");
            _autoSave = World.GetOrCreateSystemManaged<AutoSaveSystem>();
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();

            if (Mod.Service != null)
            {
                _observer = new Observer(_requests, _controls, _leaves);
                Mod.Service.Session.AddObserver(_observer);
            }
        }

        protected override void OnDestroy()
        {
            MultiplayerService service = Mod.Service;
            if (_observer != null && service != null)
                service.Session.RemoveObserver(_observer);
            if (_state != RecoveryState.Idle && service != null &&
                service.Session.Role == SessionRole.Host)
                AbortEpoch(service, "world-sync system was destroyed");
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;
            MultiplayerSession session = service.Session;
            if (session.Role != SessionRole.Host || session.Status != SessionStatus.Connected)
                return;

            long now = service.NowMs;
            DrainObserverEvents(session);

            if (_state == RecoveryState.Idle)
            {
                if (_lastResyncMs < 0 || !HasPeers(session))
                    _lastResyncMs = now;
                else if (now - _lastResyncMs >= service.ResyncIntervalMs)
                    _recoveryRequested = true;

                if (_recoveryRequested) StartEpoch(service, session, now);
                return;
            }

            PruneDisconnectedParticipants(session);
            if (_participants.Count == 0)
            {
                AbortEpoch(service, "all snapshot participants disconnected");
                return;
            }

            switch (_state)
            {
                case RecoveryState.WaitingForQuiescence:
                    PumpQuiescence(service, session, now);
                    break;
                case RecoveryState.Saving:
                    PumpSave(service, session, now);
                    break;
                case RecoveryState.WaitingForLoaded:
                    PumpLoaded(service, session, now);
                    break;
            }
        }

        private void DrainObserverEvents(MultiplayerSession session)
        {
            ConnectionId request;
            while (_requests.TryDequeue(out request))
            {
                if (_state == RecoveryState.Idle) _recoveryRequested = true;
                else _rerunRequested = true;
            }

            ConnectionId left;
            while (_leaves.TryDequeue(out left)) RemoveParticipant(left);

            ControlEvent evt;
            while (_controls.TryDequeue(out evt))
            {
                if (_state == RecoveryState.Idle || evt.Epoch != _epoch ||
                    !ContainsParticipant(evt.Connection))
                    continue;

                if (evt.Stage == WorldSyncStage.Quiesced &&
                    _state == RecoveryState.WaitingForQuiescence)
                    _quiesced.Add(evt.Connection.Value);
                else if (evt.Stage == WorldSyncStage.Loaded &&
                         _state == RecoveryState.WaitingForLoaded)
                    _loaded.Add(evt.Connection.Value);
                else if (evt.Stage == WorldSyncStage.Failed &&
                         _state == RecoveryState.WaitingForLoaded)
                {
                    Mod.log.Error("[MP] " + DescribePeer(session, evt.Connection) +
                                  " could not install world-sync epoch " + _epoch +
                                  "; disconnecting it rather than resuming divergent worlds.");
                    session.DisconnectPeer(evt.Connection);
                    RemoveParticipant(evt.Connection);
                }
            }
        }

        private void StartEpoch(MultiplayerService service, MultiplayerSession session, long now)
        {
            _recoveryRequested = false;
            _participants.Clear();
            foreach (Peer peer in session.Peers)
                if (peer.Handshaked) _participants.Add(peer.Connection);
            if (_participants.Count == 0) return;

            _epoch = ++_epochCounter;
            if (!service.TryBeginHostWorldSync(_epoch, out _resumeSpeed))
            {
                Mod.log.Error("[MP] Could not enter the local world-sync barrier.");
                ResetEpoch(now);
                return;
            }
            if (!session.BeginWorldSync(_epoch, _resumeSpeed, _participants))
            {
                service.AbortHostWorldSync(_epoch, _resumeSpeed);
                Mod.log.Error("[MP] Could not open world-sync epoch " + _epoch + ".");
                ResetEpoch(now);
                return;
            }

            _quiesced.Clear();
            _loaded.Clear();
            _cleanFrames = 0;
            _deadlineMs = now + QuiesceTimeoutMs;
            _state = RecoveryState.WaitingForQuiescence;
            Mod.log.Info("[MP] World sync epoch " + _epoch + " waiting for " +
                         _participants.Count + " client quiescence acknowledgement(s).");
            CS2MultiplayerMod.Game.Diagnostics.FlightRecorder.Note(
                "resync epoch=" + _epoch + " barrier opened participants=" + _participants.Count);
        }

        private void PumpQuiescence(MultiplayerService service, MultiplayerSession session, long now)
        {
            if (now > _deadlineMs)
            {
                DisconnectMissing(session, _quiesced, "quiescence acknowledgement");
                if (_participants.Count == 0)
                {
                    AbortEpoch(service, "no client crossed the quiescence barrier");
                    return;
                }
                if (AllParticipantsIn(_quiesced) && _netSync != null &&
                    !_netSync.IsRecoveryQuiescent)
                {
                    AbortEpoch(service, "local native transaction did not quiesce safely");
                    return;
                }
            }

            if (!AllParticipantsIn(_quiesced) ||
                (_netSync != null && !_netSync.IsRecoveryQuiescent))
            {
                _cleanFrames = 0;
                return;
            }

            // Two complete UI frames after every acknowledgement/native Temp drain close races
            // with a tool apply that was already scheduled when Begin arrived.
            if (++_cleanFrames < RequiredCleanFrames) return;
            StartSave(service, now);
        }

        private void StartSave(MultiplayerService service, long now)
        {
            try
            {
                _saveStartUtc = DateTime.UtcNow.AddSeconds(-5);
                _saveTask = _autoSave.PerformAutoSave(GameManager.instance.settings.general);
                _saveStartMs = now;
                _state = RecoveryState.Saving;
                Mod.log.Info("[MP] World sync epoch " + _epoch +
                             ": barrier closed; saving the authoritative world.");
                CS2MultiplayerMod.Game.Diagnostics.FlightRecorder.Note(
                    "resync epoch=" + _epoch + " save started");
            }
            catch (Exception ex)
            {
                AbortEpoch(service, "could not start authoritative save: " + ex.Message);
            }
        }

        private void PumpSave(MultiplayerService service, MultiplayerSession session, long now)
        {
            if (_saveTask != null && !_saveTask.IsCompleted) return;
            if (_saveTask == null || _saveTask.IsFaulted || _saveTask.IsCanceled)
            {
                string detail = _saveTask != null && _saveTask.Exception != null
                    ? _saveTask.Exception.GetBaseException().Message
                    : (_saveTask != null && _saveTask.IsCanceled ? "save was canceled" : "save task disappeared");
                AbortEpoch(service, "authoritative save failed: " + detail);
                return;
            }

            byte[] snapshot;
            string saveName;
            if (!service.TryReadWorldSnapshot(_saveStartUtc, out snapshot, out saveName))
            {
                AbortEpoch(service, "authoritative save file was unavailable");
                return;
            }

            for (int i = 0; i < _participants.Count; i++)
                service.StreamWorldSnapshot(_participants[i], _epoch, snapshot, saveName);

            _loaded.Clear();
            _deadlineMs = now + LoadTimeoutMs;
            _state = RecoveryState.WaitingForLoaded;
            Mod.log.Info("[MP] World sync epoch " + _epoch + ": queued one " +
                         (snapshot.Length / 1024) + " KB snapshot for " + _participants.Count +
                         " participant(s); waiting for load acknowledgement(s). Save took " +
                         (now - _saveStartMs) + " ms.");
            CS2MultiplayerMod.Game.Diagnostics.FlightRecorder.Note(
                "resync epoch=" + _epoch + " snapshot queued KB=" + (snapshot.Length >> 10));
        }

        private void PumpLoaded(MultiplayerService service, MultiplayerSession session, long now)
        {
            if (AllParticipantsIn(_loaded))
            {
                CompleteEpoch(service, session, now);
                return;
            }
            if (now <= _deadlineMs) return;

            DisconnectMissing(session, _loaded, "snapshot load acknowledgement");
            if (_participants.Count == 0)
                AbortEpoch(service, "no client installed the authoritative snapshot");
            else
                CompleteEpoch(service, session, now);
        }

        private void CompleteEpoch(MultiplayerService service, MultiplayerSession session, long now)
        {
            var targets = new List<ConnectionId>(_participants);
            bool needsRerun = _rerunRequested && HasNewParticipant(session, targets);
            // Resume is queued first. Session command sends are reopened only afterward, preserving
            // Resume-before-command order on every TCP connection.
            session.ResumeWorldSync(_epoch, _resumeSpeed, targets);
            service.CompleteHostWorldSync(_epoch, _resumeSpeed);
            Mod.log.Info("[MP] World sync epoch " + _epoch + " completed for " +
                         targets.Count + " participant(s).");
            ResetEpoch(now);

            // A peer that joined after this snapshot was queued needs another snapshot. Open the
            // next Begin immediately after Resume in the same update, leaving no gameplay frame
            // between epochs and never overlapping blob streams.
            _rerunRequested = false;
            if (needsRerun)
            {
                _recoveryRequested = true;
                StartEpoch(service, session, now);
            }
        }

        private void AbortEpoch(MultiplayerService service, string reason)
        {
            MultiplayerSession session = service.Session;
            var targets = new List<ConnectionId>(_participants);
            if (_epoch > 0)
            {
                session.AbortWorldSync(_epoch, _resumeSpeed, targets);
                service.AbortHostWorldSync(_epoch, _resumeSpeed);
            }
            Mod.log.Error("[MP] World sync epoch " + _epoch + " aborted: " + reason + ".");
            CS2MultiplayerMod.Game.Diagnostics.FlightRecorder.Note(
                "resync epoch=" + _epoch + " ABORTED reason=" + reason);
            ResetEpoch(service.NowMs);
        }

        private void ResetEpoch(long now)
        {
            _state = RecoveryState.Idle;
            _saveTask = null;
            _participants.Clear();
            _quiesced.Clear();
            _loaded.Clear();
            _epoch = 0;
            _deadlineMs = 0;
            _cleanFrames = 0;
            _lastResyncMs = now;
        }

        private void DisconnectMissing(MultiplayerSession session, HashSet<int> acknowledgements,
            string expected)
        {
            for (int i = _participants.Count - 1; i >= 0; i--)
            {
                ConnectionId connection = _participants[i];
                if (acknowledgements.Contains(connection.Value)) continue;
                Mod.log.Error("[MP] " + DescribePeer(session, connection) + " timed out waiting for " +
                              expected + " in epoch " + _epoch + "; disconnecting it.");
                session.DisconnectPeer(connection);
                RemoveParticipant(connection);
            }
        }

        private void PruneDisconnectedParticipants(MultiplayerSession session)
        {
            for (int i = _participants.Count - 1; i >= 0; i--)
                if (!IsConnected(session, _participants[i])) RemoveParticipant(_participants[i]);
        }

        private static bool IsConnected(MultiplayerSession session, ConnectionId connection)
        {
            foreach (Peer peer in session.Peers)
                if (peer.Connection == connection && peer.Handshaked) return true;
            return false;
        }

        private static string DescribePeer(MultiplayerSession session, ConnectionId connection)
        {
            foreach (Peer peer in session.Peers)
                if (peer.Connection == connection) return peer.ToString();
            return connection.ToString();
        }

        private void RemoveParticipant(ConnectionId connection)
        {
            _participants.Remove(connection);
            _quiesced.Remove(connection.Value);
            _loaded.Remove(connection.Value);
        }

        private bool ContainsParticipant(ConnectionId connection) =>
            _participants.Contains(connection);

        private bool AllParticipantsIn(HashSet<int> set)
        {
            if (_participants.Count == 0) return false;
            for (int i = 0; i < _participants.Count; i++)
                if (!set.Contains(_participants[i].Value)) return false;
            return true;
        }

        private static bool HasPeers(MultiplayerSession session)
        {
            foreach (Peer peer in session.Peers)
                if (peer.Handshaked) return true;
            return false;
        }

        private static bool HasNewParticipant(MultiplayerSession session,
            List<ConnectionId> completedParticipants)
        {
            foreach (Peer peer in session.Peers)
                if (peer.Handshaked && !completedParticipants.Contains(peer.Connection)) return true;
            return false;
        }

        private sealed class Observer : SessionObserver
        {
            private readonly ConcurrentQueue<ConnectionId> _requests;
            private readonly ConcurrentQueue<ControlEvent> _controls;
            private readonly ConcurrentQueue<ConnectionId> _leaves;

            public Observer(ConcurrentQueue<ConnectionId> requests,
                ConcurrentQueue<ControlEvent> controls, ConcurrentQueue<ConnectionId> leaves)
            {
                _requests = requests;
                _controls = controls;
                _leaves = leaves;
            }

            public override void OnPeerJoined(Peer peer)
            {
                _requests.Enqueue(peer.Connection);
                Mod.log.Info("[MP] Queued atomic initial world sync for " + peer + ".");
            }

            public override void OnPeerLeft(Peer peer, string reason) =>
                _leaves.Enqueue(peer.Connection);

            public override void OnResyncRequested(int playerId, ConnectionId connection)
            {
                _requests.Enqueue(connection);
                Mod.log.Info("[MP] Queued atomic world-sync request from player #" + playerId + ".");
            }

            public override void OnWorldSyncControl(WorldSyncStage stage, long epoch,
                float resumeSpeed, ConnectionId connection)
            {
                if (stage != WorldSyncStage.Quiesced && stage != WorldSyncStage.Loaded &&
                    stage != WorldSyncStage.Failed)
                    return;
                _controls.Enqueue(new ControlEvent
                {
                    Stage = stage,
                    Epoch = epoch,
                    Connection = connection,
                });
            }
        }
    }
}
