﻿namespace Darkages.Network.ServerFormats
{
    public class ServerFormat49 : NetworkFormat
    {
        public ServerFormat49()
        {
            Secured = true;
            Command = 0x49;
        }

        public override void Serialize(NetworkPacketReader reader)
        {
        }

        public override void Serialize(NetworkPacketWriter writer)
        {
            writer.Write(byte.MinValue);
        }
    }
}