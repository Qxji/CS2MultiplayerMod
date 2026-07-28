using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player relocated this building." The old position identifies the local entity,
    /// the new transform is where it goes - see <see cref="MoveSyncSystem"/>.
    ///
    /// For anything with owned geometry (a building's lot, driveways, installed upgrades) these
    /// fields are also the complete input set the game's own definition generator needs, so the
    /// receiver re-derives the whole relocation locally instead of moving the root alone.
    /// </summary>
    public sealed class ObjectMoveCommand : ISimulationCommand
    {
        public const ushort Id = 8;

        public string PrefabName;
        public float OldX, OldY, OldZ;
        public float NewX, NewY, NewZ;
        public float RotX, RotY, RotZ, RotW;
        /// <summary>Control-point elevation: the height offset a bridge/elevated placement carries.</summary>
        public float Elevation;
        /// <summary>The moving tool's own seed; every per-definition seed is derived from it.</summary>
        public uint ToolRandomSeed;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(PrefabName);
            writer.WriteFloat(OldX); writer.WriteFloat(OldY); writer.WriteFloat(OldZ);
            writer.WriteFloat(NewX); writer.WriteFloat(NewY); writer.WriteFloat(NewZ);
            writer.WriteFloat(RotX); writer.WriteFloat(RotY); writer.WriteFloat(RotZ); writer.WriteFloat(RotW);
            writer.WriteFloat(Elevation);
            writer.WriteInt(unchecked((int)ToolRandomSeed));
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            OldX = WireGuard.ReadCoordinate(reader); OldY = WireGuard.ReadCoordinate(reader); OldZ = WireGuard.ReadCoordinate(reader);
            NewX = WireGuard.ReadCoordinate(reader); NewY = WireGuard.ReadCoordinate(reader); NewZ = WireGuard.ReadCoordinate(reader);
            RotX = WireGuard.ReadFinite(reader); RotY = WireGuard.ReadFinite(reader); RotZ = WireGuard.ReadFinite(reader); RotW = WireGuard.ReadFinite(reader);
            float rotationLengthSq = RotX * RotX + RotY * RotY + RotZ * RotZ + RotW * RotW;
            if (rotationLengthSq < 0.25f || rotationLengthSq > 2.25f)
                throw new ProtocolException("Implausible move rotation length " + rotationLengthSq + ".");
            Elevation = WireGuard.ReadCoordinate(reader);
            // The tool seed is opaque: every 32-bit value is legal input to the game's generator.
            ToolRandomSeed = unchecked((uint)reader.ReadInt());
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in object-move command: " + reader.Remaining + ".");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(96);
            Write(writer);
            return writer.ToArray();
        }

        public static ObjectMoveCommand Decode(byte[] body)
        {
            var command = new ObjectMoveCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
