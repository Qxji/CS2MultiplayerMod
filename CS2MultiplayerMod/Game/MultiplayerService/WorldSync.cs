using System;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;

namespace CS2MultiplayerMod.Game
{
    public sealed partial class MultiplayerService
    {
        private World _currentWorld;
        private bool _worldSyncBarrierActive;
        private bool _worldSyncHadUsableWorld;
        private long _activeWorldSyncEpoch;
        private float _worldSyncResumeSpeed = 1f;

        /// <summary>True while all gameplay traffic and local tools are quiesced for a snapshot.</summary>
        public bool WorldSyncBarrierActive => _worldSyncBarrierActive;
        public long ActiveWorldSyncEpoch => _activeWorldSyncEpoch;

        /// <summary>
        /// Host-side half of Begin. Captures the shared speed, pauses the local simulation, drops
        /// every pre-cut inbox, and closes <see cref="GameplaySyncReady"/> synchronously.
        /// </summary>
        internal bool TryBeginHostWorldSync(long epoch, out float resumeSpeed)
        {
            resumeSpeed = 0f;
            if (_session.Role != SessionRole.Host || _worldSyncBarrierActive || epoch <= 0)
                return false;

            _worldSyncResumeSpeed = ReadSimulationSpeed();
            _worldSyncHadUsableWorld = true;
            _activeWorldSyncEpoch = epoch;
            _worldSyncBarrierActive = true;
            SyncInbox.DrainAll();
            MaintainWorldSyncBarrier();
            resumeSpeed = _worldSyncResumeSpeed;
            _log.Info("[MP] World sync epoch " + epoch +
                      " entered the local quiescence barrier (resume speed " + resumeSpeed + ").");
            Diagnostics.FlightRecorder.Note("world-sync begin epoch=" + epoch +
                                              " resumeSpeed=" + resumeSpeed);
            return true;
        }

        internal void CompleteHostWorldSync(long epoch, float resumeSpeed)
        {
            if (!_worldSyncBarrierActive || epoch != _activeWorldSyncEpoch) return;
            _worldSyncResumeSpeed = SanitizeSpeed(resumeSpeed);
            ResetWorldSyncState(restoreSpeed: true);
            _log.Info("[MP] World sync epoch " + epoch + " completed; gameplay resumed.");
            Diagnostics.FlightRecorder.Note("world-sync resume epoch=" + epoch);
        }

        internal void AbortHostWorldSync(long epoch, float resumeSpeed)
        {
            if (!_worldSyncBarrierActive || epoch != _activeWorldSyncEpoch) return;
            _worldSyncResumeSpeed = SanitizeSpeed(resumeSpeed);
            ResetWorldSyncState(restoreSpeed: true);
            _log.Warn("[MP] World sync epoch " + epoch +
                      " aborted before a snapshot was installed; previous world resumed.");
            Diagnostics.FlightRecorder.Note("world-sync abort epoch=" + epoch);
        }

        private void HandleWorldSyncControl(WorldSyncStage stage, long epoch, float resumeSpeed)
        {
            if (_session.Role != SessionRole.Client) return;

            if (stage == WorldSyncStage.Begin)
            {
                if (_worldSyncBarrierActive && epoch < _activeWorldSyncEpoch) return;
                if (!_worldSyncBarrierActive || epoch != _activeWorldSyncEpoch)
                {
                    _worldSyncHadUsableWorld = _phase == ClientWorldPhase.InSession;
                    _activeWorldSyncEpoch = epoch;
                    _worldSyncResumeSpeed = SanitizeSpeed(resumeSpeed);
                    _worldSyncBarrierActive = true;
                    SyncInbox.DrainAll();
                    SetPhase(ClientWorldPhase.WaitingForMap);
                    _log.Info("[MP] World sync epoch " + epoch +
                              " began; local gameplay is quiesced before snapshot transfer.");
                    Diagnostics.FlightRecorder.Note("world-sync client quiesced epoch=" + epoch);
                }
                MaintainWorldSyncBarrier();
                _session.SendWorldSyncStage(epoch, WorldSyncStage.Quiesced);
                return;
            }

            if (!_worldSyncBarrierActive || epoch != _activeWorldSyncEpoch) return;

            if (stage == WorldSyncStage.Resume)
            {
                _worldSyncResumeSpeed = SanitizeSpeed(resumeSpeed);
                if (_phase != ClientWorldPhase.WaitingForResume)
                {
                    _log.Error("[MP] Host resumed world-sync epoch " + epoch +
                               " before this client installed its snapshot; requesting a new epoch.");
                    ResetWorldSyncState(restoreSpeed: false);
                    SetPhase(ClientWorldPhase.WaitingForMap);
                    SyncInbox.RequestResync("resume arrived before snapshot load completed");
                    return;
                }

                ResetWorldSyncState(restoreSpeed: true);
                SetPhase(ClientWorldPhase.InSession);
                _log.Info("[MP] World sync epoch " + epoch +
                          " resumed after the authoritative snapshot was installed.");
                Diagnostics.FlightRecorder.Note("world-sync client resumed epoch=" + epoch);
                return;
            }

            if (stage == WorldSyncStage.Abort)
            {
                bool canResumeOldWorld = _worldSyncHadUsableWorld;
                _worldSyncResumeSpeed = SanitizeSpeed(resumeSpeed);
                ResetWorldSyncState(restoreSpeed: canResumeOldWorld);
                SetPhase(canResumeOldWorld ? ClientWorldPhase.InSession : ClientWorldPhase.WaitingForMap);
                _log.Warn("[MP] Host aborted world-sync epoch " + epoch + ".");
            }
        }

        /// <summary>Keep pause/tool quiescence enforced even if a state apply or map load resets it.</summary>
        private void MaintainWorldSyncBarrier()
        {
            if (!_worldSyncBarrierActive || _currentWorld == null) return;
            try
            {
                SimulationSystem simulation =
                    _currentWorld.GetOrCreateSystemManaged<SimulationSystem>();
                if (simulation != null && !simulation.selectedSpeed.Equals(0f))
                    simulation.selectedSpeed = 0f;

                ToolSystem tools = _currentWorld.GetOrCreateSystemManaged<ToolSystem>();
                DefaultToolSystem defaultTool =
                    _currentWorld.GetOrCreateSystemManaged<DefaultToolSystem>();
                if (tools != null && defaultTool != null && tools.activeTool != defaultTool)
                    tools.activeTool = defaultTool;
            }
            catch (Exception ex)
            {
                // Worlds are replaced between UI frames. A stale World reference is expected for
                // that one boundary frame; the next MultiplayerSystem supplies the new instance.
                Mod.Verbose("[MP] Could not enforce world-sync pause on this frame: " + ex.Message);
            }
        }

        private float ReadSimulationSpeed()
        {
            if (_currentWorld == null) return 1f;
            try
            {
                SimulationSystem simulation =
                    _currentWorld.GetOrCreateSystemManaged<SimulationSystem>();
                return simulation != null ? SanitizeSpeed(simulation.selectedSpeed) : 1f;
            }
            catch { return 1f; }
        }

        private void ResetWorldSyncState(bool restoreSpeed)
        {
            float speed = _worldSyncResumeSpeed;
            _worldSyncBarrierActive = false;
            _activeWorldSyncEpoch = 0;
            _worldSyncHadUsableWorld = false;

            if (!restoreSpeed || _currentWorld == null) return;
            try
            {
                SimulationSystem simulation =
                    _currentWorld.GetOrCreateSystemManaged<SimulationSystem>();
                if (simulation != null) simulation.selectedSpeed = SanitizeSpeed(speed);
            }
            catch (Exception ex)
            {
                Mod.Verbose("[MP] Could not restore simulation speed after world sync: " + ex.Message);
            }
        }

        private static float SanitizeSpeed(float speed) =>
            float.IsNaN(speed) || float.IsInfinity(speed) || speed < 0f ? 0f : speed;
    }
}
