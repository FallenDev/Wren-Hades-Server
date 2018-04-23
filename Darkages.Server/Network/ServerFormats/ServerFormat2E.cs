﻿///************************************************************************
//Project Lorule: A Dark Ages Server (http://darkages.creatorlink.net/index/)
//Copyright(C) 2018 TrippyInc Pty Ltd
//
//This program is free software: you can redistribute it and/or modify
//it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or
//(at your option) any later version.
//
//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
//GNU General Public License for more details.
//
//You should have received a copy of the GNU General Public License
//along with this program.If not, see<http://www.gnu.org/licenses/>.
//*************************************************************************/
namespace Darkages.Network.ServerFormats
{
    public class ServerFormat2E : NetworkFormat
    {
        public ServerFormat2E(Aisling user)
        {
            User = user;
        }

        public override bool Secured => true;
        public override byte Command => 0x2E;
        private Aisling User { get; }

        public override void Serialize(NetworkPacketReader reader)
        {
        }

        public override void Serialize(NetworkPacketWriter writer)
        {
            var portal = ServerContext.GlobalWorldMapTemplateCache[User.PortalSession.FieldNumber];
            var name = string.Format("field{0:000}", portal.FieldNumber);

            writer.WriteStringA(name);
            writer.Write((byte)portal.Portals.Count);
            writer.Write((byte)0x09);

            foreach (var warps in portal.Portals)
            {
                if (warps == null || warps.Destination == null)
                    continue;

                //silly americans!
                writer.Write(warps.PointY);
                writer.Write(warps.PointX);

                writer.WriteStringA(warps.DisplayName);
                writer.Write(warps.Destination.AreaID);
                writer.Write((short)warps.Destination.Location.X);
                writer.Write((short)warps.Destination.Location.Y);
            }
        }
    }
}