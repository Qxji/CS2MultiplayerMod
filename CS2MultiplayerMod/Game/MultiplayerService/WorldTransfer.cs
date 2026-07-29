using System;
using System.IO;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game
{
    public sealed partial class MultiplayerService
    {
        /// <summary>
        /// Read the authoritative save produced for one recovery epoch exactly once. Returning
        /// false is terminal for that epoch: callers must Abort instead of substituting a stale
        /// save whose contents do not match the established causal cut.
        /// </summary>
        internal bool TryReadWorldSnapshot(DateTime writtenAfterUtc, out byte[] data,
            out string saveName)
        {
            data = null;
            saveName = null;
            if (_session.Role != SessionRole.Host) return false;

            string save = FindNewestSave(writtenAfterUtc);
            if (save == null)
            {
                _log.Error("[MP] The recovery save task produced no fresh .cok; the epoch will " +
                           "be aborted rather than streaming stale world state.");
                return false;
            }

            try
            {
                var info = new FileInfo(save);
                data = File.ReadAllBytes(save);
                if (data.Length == 0 || data.Length > MaxSaveBlobBytes)
                {
                    _log.Error("[MP] Recovery save '" + info.Name + "' has implausible size " +
                               data.Length + "; aborting the epoch.");
                    data = null;
                    return false;
                }
                saveName = info.Name;
                _log.Info("[MP] Prepared recovery snapshot '" + saveName + "' (" +
                          (data.Length / 1024) + " KB, modified " +
                          info.LastWriteTimeUtc.ToString("O") + ").");
                return true;
            }
            catch (Exception ex)
            {
                _log.Error("[MP] Failed to read recovery snapshot: " + ex.Message);
                data = null;
                return false;
            }
        }

        /// <summary>Queue one already-read snapshot for one participant, tagged with its epoch.</summary>
        internal void StreamWorldSnapshot(ConnectionId target, long epoch, byte[] data,
            string saveName)
        {
            if (_session.Role != SessionRole.Host || target.IsNone || data == null || epoch <= 0)
                return;
            _session.SendBlobTo(target, MapChannel, epoch, data);
            _log.Info("[MP] Queued recovery snapshot '" + (saveName ?? "<save>") + "' (" +
                      (data.Length / 1024) + " KB) for " + DescribeWorldTarget(target) +
                      " in epoch " + epoch + ".");
        }

        private void LoadReceivedMap(long transferId, byte[] data)
        {
            if (!_worldSyncBarrierActive || transferId <= 0 || transferId != _activeWorldSyncEpoch)
            {
                _log.Warn("[MP] Ignoring map transfer " + transferId +
                          ": active world-sync epoch is " +
                          (_worldSyncBarrierActive ? _activeWorldSyncEpoch.ToString() : "none") + ".");
                return;
            }
            // The completed blob is the causal cut: commands received before it are represented by
            // the save, while every later command must survive the ECS world replacement.
            _log.Info("[MP] Map blob delivered to game layer (" +
                      (data != null ? data.Length / 1024 : 0) + " KB); staging and loading.");
            Diagnostics.FlightRecorder.Note("world blob received " + (data != null ? data.Length >> 10 : 0) + " KB; reloading world");
            // Purge every sync inbox before the reload: queued commands describe the pre-reload
            // world and would apply stale edits (or reference vanished entities) on the new one.
            Sync.Infrastructure.SyncInbox.DrainAll();
            SetPhase(ClientWorldPhase.LoadingMap);
            if (!JoinMapLoader.StageAndLoad(data, _log))
            {
                // Defined, recoverable state instead of a half-connected limbo.
                SetPhase(ClientWorldPhase.WaitingForMap);
                _session.SendWorldSyncStage(_activeWorldSyncEpoch, WorldSyncStage.Failed);
                _log.Warn("[MP] Could not auto-load the host world. Still connected - use /sync to " +
                          "request it again, or load '" + JoinMapLoader.TransientName + "' from Load Game.");
            }
            else
            {
                // From this point onward a disconnect must unload this disposable host
                // world. The preload callback normally marks it synchronously as well;
                // keeping the marker here covers runtimes which publish that callback later.
                MarkClientHostWorldActive();
            }
        }

        private static string FindNewestSave(DateTime writtenAfterUtc)
        {
            string dir = JoinMapLoader.SavesDirectory();
            if (dir == null || !Directory.Exists(dir)) return null;

            string newest = null;
            DateTime newestTime = writtenAfterUtc;
            foreach (string file in Directory.GetFiles(dir, "*.cok", SearchOption.AllDirectories))
            {
                // Never echo a transient join-world back out as the host's map.
                if (Path.GetFileNameWithoutExtension(file) == JoinMapLoader.TransientName) continue;
                DateTime t = File.GetLastWriteTimeUtc(file);
                if (t <= newestTime) continue;
                newestTime = t;
                newest = file;
            }
            return newest;
        }

        private string DescribeWorldTarget(ConnectionId target)
        {
            if (target.IsNone) return "all clients";

            foreach (Peer peer in _session.Peers)
            {
                if (peer.Connection != target) continue;
                return peer.ToString();
            }

            return target.ToString();
        }

        private void RecordRemotePlayer(PlayerStateMessage state)
        {
            // Ignore our own echo; we already know where we are.
            if (state.PlayerId == _session.LocalPlayerId) return;

            var player = _remotePlayers.GetOrAdd(state.PlayerId, id => new RemotePlayer { PlayerId = id });
            player.X = state.PosX;
            player.Y = state.PosY;
            player.Z = state.PosZ;
            player.EyeX = state.EyeX;
            player.EyeY = state.EyeY;
            player.EyeZ = state.EyeZ;
            player.Yaw = state.Yaw;
            player.LastUpdateMs = _clock.ElapsedMilliseconds;
        }

    }
}
