namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// A final, reliable explanation sent by the host before it deliberately closes a
    /// client's connection. This keeps administrative disconnects distinct from network
    /// failures and gives the client a useful message to display.
    /// </summary>
    public sealed class DisconnectNoticeMessage : INetMessage
    {
        public string Reason;

        public DisconnectNoticeMessage() { }

        public DisconnectNoticeMessage(string reason)
        {
            Reason = reason;
        }

        public MessageType Type => MessageType.DisconnectNotice;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(Reason);
        }

        public void Read(NetworkReader reader)
        {
            Reason = reader.ReadString();
        }
    }
}
