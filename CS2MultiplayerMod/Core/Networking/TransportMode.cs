namespace CS2MultiplayerMod.Core.Networking
{
    /// <summary>How the peers of a session reach each other.</summary>
    public enum TransportMode
    {
        /// <summary>
        /// A TCP socket straight to the host's address and port. Only reachable over a
        /// LAN or through a forwarded port.
        /// </summary>
        Direct = 0,

        /// <summary>
        /// Steam's relay network, addressed by the host's join code instead of an
        /// address. Nothing listens on a public port, so no forwarding is involved.
        /// </summary>
        SteamRelay = 1,
    }
}
