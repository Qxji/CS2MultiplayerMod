using System;
using CS2MultiplayerMod.Core.Diagnostics;
using Steamworks;

namespace CS2MultiplayerMod.Core.Networking.Steam
{
    /// <summary>
    /// Registers Steam as the relay backend. The game already runs the Steam API and
    /// pumps its callbacks every frame, so this only has to report whether that is
    /// true right now and hand out transports when it is.
    /// </summary>
    public sealed class SteamRelayProvider : IRelayProvider
    {
        /// <summary>
        /// Virtual port inside the relay, not a network port: nothing is opened on the
        /// machine and nothing needs forwarding. Both sides must simply agree, so it is
        /// a constant rather than a setting.
        /// </summary>
        public const int VirtualPort = 25001;

        /// <summary>
        /// Probe Steam and register only if it answered. The probe has to run before the
        /// assignment: a copy of the game that ships no Steam library at all (Game Pass,
        /// Epic) fails when the first Steam-touching method body is compiled, not inside
        /// it, so a provider registered up front would keep throwing from every later
        /// call. Leaving <see cref="RelayProvider.Current"/> null instead reads as
        /// "relay unavailable" and the direct path carries on untouched.
        /// </summary>
        public static void Register(IModLogger log)
        {
            try
            {
                var provider = new SteamRelayProvider();
                string reason = provider.UnavailableReason;
                RelayProvider.Current = provider;

                if (reason == null)
                    log.Info("Steam relay available; the join code for this machine is " + LocalSteamId() + ".");
                else
                    log.Info("Steam relay not usable yet (" + reason + "). Hosting can still use a direct connection.");
            }
            catch (Exception ex)
            {
                log.Info("Steam is not present in this build (" + ex.Message +
                         "); multiplayer will use direct connections only.");
            }
        }

        public string UnavailableReason
        {
            get
            {
                try
                {
                    if (!SteamAPI.IsSteamRunning())
                        return "Steam is not running.";
                    if (LocalSteamId() == 0)
                        return "Steam is not signed in.";
                    return null;
                }
                catch (Exception ex)
                {
                    // A non-Steam copy of the game has no native Steam library at all, which
                    // surfaces here as a load failure rather than a false return.
                    return "Steam is not available (" + ex.Message + ").";
                }
            }
        }

        public string LocalJoinCode
        {
            get
            {
                ulong id = LocalSteamId();
                return id == 0 ? "" : id.ToString();
            }
        }

        public ITransport CreateHost(IModLogger log)
        {
            return SteamRelayTransport.StartHost(log, VirtualPort);
        }

        public ITransport CreateClient(IModLogger log, string joinCode)
        {
            return SteamRelayTransport.Connect(log, joinCode, VirtualPort);
        }

        internal static ulong LocalSteamId()
        {
            try { return SteamUser.GetSteamID().m_SteamID; }
            catch (Exception) { return 0; }
        }
    }
}
