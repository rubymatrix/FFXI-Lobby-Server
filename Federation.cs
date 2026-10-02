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
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Crystal.FFXILobbyServer.Network.Models;
using XiToken;

namespace Crystal.FFXILobbyServer
{
    // Admits players to worlds through the world's federation gateway: the lobby signs a short-lived, single-use
    // xi.world-entry/1 token (External/XiToken, SPEC.md) and the world writes its own session row. A world takes this
    // path when lobby.cfg gives it `gateway` and `worldId`; other worlds keep the direct accounts_sessions insert.
    public static class Federation
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

        public static TokenIssuer Issuer { get; private set; }

        public static void Configure(FFXILobbyConfig config)
        {
            if (string.IsNullOrEmpty(config.FederationServerId))
                return;
            try
            {
                SigningKey key = SigningKey.FromPaserk(config.FederationKeyId, File.ReadAllText(config.FederationSigningKey).Trim());
                Issuer = new TokenIssuer(config.FederationServerId, key);
                Program.Log.Info($"Federation: issuing world-entry tokens as {Issuer.ServerId} (key {key.KeyId})");
            }
            catch (Exception e) when (e is IOException or FormatException or ArgumentException or UnauthorizedAccessException)
            {
                Program.Log.Error($"Federation: cannot load the signing key ({e.Message}); gateway worlds will refuse logins");
            }
        }

        public record Admission(uint MapIp, uint MapPort);

        // Sends the world a world-entry token for this character. Returns the map server the world chose for it, or
        // null when the world refused (the reason is logged).
        public static Admission Admit(WorldContainer world, string polId, WorldEntry entry)
        {
            if (Issuer == null)
            {
                Program.Log.Error($"{polId} - World {world.World.Name} uses a gateway, but no federation key is loaded");
                return null;
            }

            string token = Issuer.Issue(WorldEntry.Type, world.FederationWorldId, polId, WorldEntry.DefaultLifetime, entry.ToClaims());
            try
            {
                using var content = new StringContent(token, Encoding.ASCII, "text/plain");
                using HttpResponseMessage response = Http.PostAsync(world.GatewayUrl.TrimEnd('/') + "/xi/v1/world-entry", content).GetAwaiter().GetResult();
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                JsonObject reply = JsonNode.Parse(body) as JsonObject;

                if (response.IsSuccessStatusCode && reply?["ok"]?.GetValue<bool>() == true &&
                    IPAddress.TryParse(reply["map"]?["ip"]?.GetValue<string>(), out IPAddress mapIp))
                {
                    uint mapPort = reply["map"]["port"].GetValue<uint>();
                    return new(BitConverter.ToUInt32(mapIp.GetAddressBytes()), mapPort);
                }
                Program.Log.Warn($"{polId} - World {world.World.Name} refused character {entry.CharId}: {(int)response.StatusCode} {reply?["error"] ?? body}");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
                Program.Log.Error($"{polId} - World {world.World.Name} gateway {world.GatewayUrl} failed: {e.Message}");
            }
            return null;
        }
    }
}
