using System;
using System.Net.Sockets;
using CS2MultiplayerMod.Core.Networking.Tcp;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Core.Session
{
    public sealed partial class MultiplayerSession
    {
        public void StartHost(MultiplayerConfig config)
        {
            if (Role != SessionRole.None) throw new InvalidOperationException("A session is already active.");

            // Nothing below may escape: an exception thrown after Role is set would
            // leave a half-started session ("a session is already active" forever) —
            // exactly what happened when TLS setup crashed on the game's runtime.
            try
            {
                StartHostCore(config);
            }
            catch (Exception ex)
            {
                Fault(DescribeStartupFailure("Failed to host", ex));
            }
        }

        private void StartHostCore(MultiplayerConfig config)
        {
            // Public exposure without a password lets anyone who finds the port walk
            // into the city. Said loudly, but allowed — private games with trusted
            // friends over a forwarded port are this mod's main use case.
            if (!config.LanOnly && string.IsNullOrEmpty(config.Password))
                _log.Warn("[security] Hosting PUBLICLY with NO PASSWORD: anyone who can reach port " +
                          config.Port + " can join and receive the city. Setting a password is strongly recommended.");

            _config = config;
            LocalPlayerName = WireGuard.SanitizePlayerName(config.PlayerName);
            LocalPlayerId = HostPlayerId;
            Role = SessionRole.Host;

            EncryptionActive = false;
            _certificate = null;
            if (config.UseEncryption)
            {
                string certError;
                _certificate = TlsCertificate.TryCreateEphemeral(out certError);
                if (_certificate == null)
                {
                    if (config.LanOnly)
                    {
                        _log.Warn("TLS unavailable on this runtime (" + certError +
                                  "); continuing without TLS because the session is LAN-only. " +
                                  "Clients must disable encryption too.");
                    }
                    else
                    {
                        Fault("Cannot host publicly: TLS is unavailable on this runtime (" + certError + ").");
                        return;
                    }
                }
                else
                {
                    EncryptionActive = true;
                }
            }

            if (!config.LanOnly)
                _log.Warn("PUBLIC HOSTING ENABLED: your machine accepts connections from the internet " +
                          "on port " + config.Port + ". Keep the password strong and private.");

            var server = new TcpServerTransport(_log);
            _transport = server;
            try
            {
                server.Start(config.Port, config.LanOnly, _certificate);
                SetStatus(SessionStatus.Connected, "Hosting on port " + config.Port +
                          (config.LanOnly ? " (LAN-only" : " (PUBLIC") +
                          (EncryptionActive ? ", TLS)" : ", PLAINTEXT)"));
            }
            catch (Exception ex)
            {
                Fault(DescribeStartupFailure("Failed to host", ex));
            }
        }

        public void Join(MultiplayerConfig config)
        {
            if (Role != SessionRole.None) throw new InvalidOperationException("A session is already active.");

            // Same containment as StartHost: a throw after Role is set must become a
            // clean Fault (which resets the session), never a stuck half-join.
            try
            {
                _config = config;
                LocalPlayerName = WireGuard.SanitizePlayerName(config.PlayerName);
                Role = SessionRole.Client;
                _challengeAnswered = false;
                EncryptionActive = config.UseEncryption;

                var client = new TcpClientTransport(_log);
                _transport = client;
                SetStatus(SessionStatus.Connecting, "Connecting to " + config.HostAddress + ":" + config.Port +
                                                    (config.UseEncryption ? " (TLS)" : " (PLAINTEXT)"));
                client.Connect(config.HostAddress, config.Port, config.UseEncryption);
            }
            catch (Exception ex)
            {
                Fault(DescribeStartupFailure("Failed to start joining", ex));
            }
        }

        private static string DescribeStartupFailure(string prefix, Exception ex)
        {
            var socket = ex as SocketException;
            return prefix + (socket != null ? " [" + socket.SocketErrorCode + "]" : "") +
                   ": " + ex.Message;
        }

        public void Stop()
        {
            if (_transport != null)
            {
                _transport.Shutdown();
                _transport.Dispose();
                _transport = null;
            }

            if (_certificate != null)
            {
                try { _certificate.Dispose(); } catch { /* ignore */ }
                _certificate = null;
            }

            _peers.Clear();
            _administrativeRemovals.Clear();
            _blobs.Clear();
            _blobTransferIds.Clear();
            ClearBlobProgress();
            _outgoingBlobActive = false;
            _outgoingBlobTotal = 0;
            _outgoingBlobSent = 0;
            Role = SessionRole.None;
            LocalPlayerId = 0;
            _nextPlayerId = HostPlayerId + 1;
            EncryptionActive = false;
            _worldSyncSuspended = false;
            _worldSyncEpoch = 0;
            SetStatus(SessionStatus.Offline, "Stopped");
        }

    }
}
