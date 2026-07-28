using System;
using Colossal.Serialization.Entities;
using Game;
using Game.SceneFlow;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Leaving the shared city ends the session on this machine - quitting the game,
    /// returning to the main menu, or loading a different world. Without this a host that
    /// walked out to the menu kept its port open and its clients "connected" to a city
    /// nobody was playing, and a client that left kept receiving edits for a world it no
    /// longer had.
    ///
    /// Two independent signals drive it, because neither alone covers every exit:
    ///  - the game's own pre-load callback, which fires before the world it is about to
    ///    replace is torn down (see <see cref="MultiplayerSystem"/>);
    ///  - a per-frame check of the game's lifecycle state, which is the only warning a
    ///    process exit gives. Shutdown runs the frame loop for a while after the quit is
    ///    requested, which is the window this uses to say goodbye properly.
    /// </summary>
    public sealed partial class MultiplayerService
    {
        /// <summary>A world load we started ourselves stays "expected" for this long.</summary>
        private const long ExpectedWorldLoadWindowMs = 180000;

        private bool _inCityWorld;
        private bool _leavingSession;
        private long _expectedWorldLoadMs = long.MinValue;
        private bool _transientCleanupPending;

        /// <summary>
        /// Marks the world load this mod is about to start as its own, so the transition
        /// watcher does not read the joining client's incoming city as the player walking
        /// out of the session. Every load we trigger goes through <see cref="JoinMapLoader"/>,
        /// including the hand-loaded fallback when the save index misses - hence the window
        /// rather than a single-shot flag.
        /// </summary>
        internal void ExpectOwnWorldLoad() => _expectedWorldLoadMs = NowMs;

        private bool ConsumeExpectedWorldLoad()
        {
            bool expected = _expectedWorldLoadMs != long.MinValue &&
                            NowMs - _expectedWorldLoadMs < ExpectedWorldLoadWindowMs;
            _expectedWorldLoadMs = long.MinValue;
            return expected;
        }

        /// <summary>
        /// The game is about to load <paramref name="mode"/>; the world open right now is
        /// on its way out. Anything that is not our own incoming session world takes this
        /// machine out of the shared city.
        /// </summary>
        internal void HandleWorldTransition(Purpose purpose, GameMode mode)
        {
            if (_session.Role == SessionRole.None)
            {
                _expectedWorldLoadMs = long.MinValue;
                return;
            }

            if (mode.IsGame())
            {
                // The host's world arriving for a join or a /sync - the session continues.
                if (ConsumeExpectedWorldLoad()) return;

                LeaveSharedSession(
                    "Loading another world (" + purpose + ")",
                    "The host loaded a different city, so this session has ended.");
                return;
            }

            LeaveSharedSession(
                "Left the city for " + mode + " (" + purpose + ")",
                "The host returned to the main menu, so this session has ended.");
        }

        /// <summary>
        /// Watches the two states no callback announces: the game shutting down, and a
        /// world transition that never reached <see cref="HandleWorldTransition"/>.
        /// Called once per frame from the service pump.
        /// </summary>
        private void PumpGameExit()
        {
            GameManager manager;
            try { manager = GameManager.instance; }
            catch (Exception) { return; }
            if (manager == null) return;

            // Mirror the game mode unconditionally, so the flag describes where this machine
            // is rather than where it was when some past session ended. Only a transition
            // observed on this frame counts as leaving - a session started from the main
            // menu (a client joining) must never look like one that walked out of a city.
            bool inCity = manager.gameMode.IsGame();
            bool leftCity = _inCityWorld && !inCity;
            _inCityWorld = inCity;

            if (_session.Role != SessionRole.None)
            {
                GameManager.State state = manager.state;
                if (state == GameManager.State.Quitting || state == GameManager.State.Terminated)
                {
                    LeaveSharedSession("The game is closing",
                        "The host closed the game, so this session has ended.");
                    return;
                }

                if (leftCity)
                {
                    // Belt and braces for the pre-load callback: whatever happened, this
                    // machine is no longer in the city the session is played in.
                    LeaveSharedSession("No longer in a city world (" + manager.gameMode + ")",
                        "The host left the city, so this session has ended.");
                    return;
                }
            }

            PumpTransientCleanup(manager);
        }

        /// <summary>
        /// Close the session because this machine is leaving the shared city. The host tells
        /// its clients first - they would otherwise see nothing but a dropped socket.
        /// </summary>
        private void LeaveSharedSession(string logReason, string hostNotice)
        {
            if (_leavingSession || _session.Role == SessionRole.None) return;
            _leavingSession = true;
            try
            {
                bool host = _session.Role == SessionRole.Host;
                _log.Info("[MP] " + logReason + " - " +
                          (host ? "closing the session for every player." : "disconnecting from the host."));
                FlightRecorder.Note("session end: " + logReason + " role=" + _session.Role);

                // The world is being torn down or replaced: restoring the simulation speed
                // into it would write to a world that is on its way out.
                ResetWorldSyncState(restoreSpeed: false);
                _session.StopWithNotice(hostNotice);
                SetPhase(ClientWorldPhase.None);

                // The staged host world is dropped off the load path - see PumpTransientCleanup.
                _transientCleanupPending = true;
            }
            catch (Exception ex)
            {
                _log.Error("[MP] Failed to close the session while leaving the game: " + ex.Message);
                FlightRecorder.NoteException("session-leave", ex);
            }
            finally
            {
                _leavingSession = false;
                _expectedWorldLoadMs = long.MinValue;
            }
        }

        /// <summary>
        /// Deletes the joining client's copy of the host world once the game is idle again.
        /// It is deliberately not done from the transition callback: that runs inside the
        /// game's load pipeline, and the save index is exactly what is being rebuilt there.
        /// </summary>
        private void PumpTransientCleanup(GameManager manager)
        {
            if (!_transientCleanupPending) return;
            if (manager.isGameLoading) return;
            _transientCleanupPending = false;
            JoinMapLoader.DeleteTransient(_log);
        }
    }
}
