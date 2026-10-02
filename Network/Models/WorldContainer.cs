/*
===========================================================================
Copyright (C) 2019-2026 Project Crystal Dev Team

This file is part of Project Crystal Server.

Project Crystal Server is free software: you can redistribute it and/or modify
it under the terms of the GNU Affero General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

Project Crystal Server is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU Affero General Public License for more details.

You should have received a copy of the GNU Affero General Public License
along with Project Crystal Server. If not, see <https://www.gnu.org/licenses/>.
===========================================================================
*/

using System;
using XiToken;

namespace Crystal.FFXILobbyServer.Network.Models
{
    public class WorldContainer
    {
        public World World;
        public readonly string DbHost;
        public readonly string DbPort;
        public readonly string DbName;
        public readonly string DbUser;
        public readonly string DbPass;

        public readonly uint ServerIp;
        public readonly uint ServerPort;
        public uint CacheIp;   // the search server; a federated world may publish it in its key set
        public uint CachePort;

        // The world server's settings folder (LandSandBoat / Phoenix `settings`): the expansions it enables are
        // the ones the lobby reports (Utils.ServerExpansions). Empty: not configured.
        public string SettingsDir = "";

        // Federation (Federation.cs). A federated world is named by its xitoken server id; the lobby reaches it only
        // through its gateway, found in the world's signed key set. Its database settings, if any, are only used by
        // --federate-accounts.
        public string Trust = "";
        public string KeySetUrl = "";
        public string Pin;
        public string GatewayOverride;
        public bool NameFromKeySet;
        public GatewayClient Gateway;
        public uint? Expansions;
        public DateTime LastConnectAttempt = DateTime.MinValue;

        public bool IsFederated => Trust.Length > 0;

        public WorldContainer(World world, string host, string port, string name, string usr, string pass, uint srvIp, uint srvPort, uint cacheIp, uint cachePort)
        {
            World = world;
            DbHost = host;
            DbPort = port;
            DbName = name;
            DbUser = usr;
            DbPass = pass;

            ServerIp = srvIp;
            ServerPort = srvPort;
            CacheIp = cacheIp;
            CachePort = cachePort;
        }

        // A world reached only through its gateway. The cache (search) server address is not part of federation yet,
        // so it stays empty unless lobby.cfg gives one.
        public static WorldContainer Federated(ushort num, string name, string trust, string keySetUrl, string pin, string gateway, bool nameFromKeySet,
                                               uint cacheIp = 0, uint cachePort = 0)
        {
            return new WorldContainer(new World() { Num = num, Name = name }, null, null, null, null, null, 0, 0, cacheIp, cachePort)
            {
                Trust = trust,
                KeySetUrl = keySetUrl,
                Pin = pin,
                GatewayOverride = gateway,
                NameFromKeySet = nameFromKeySet,
            };
        }

        public void Rename(string name)
        {
            World = new World() { Num = World.Num, Name = name };
        }
    }
}
