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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Crystal.FFXILobbyServer.Network.Models;
using XiToken;

namespace Crystal.FFXILobbyServer
{
    // Federated worlds (External/XiToken, SPEC.md): worlds this lobby reaches only through their gateway, never their
    // database. A world is federated when lobby.cfg names it by server id (`trust`), or when a trusted registry lists
    // it. The lobby signs a short-lived token for every call: the character list, creation, deletion and renaming
    // (xi.account/1), and entering the world (xi.world-entry/1).
    public static class Federation
    {
        private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

        public static TokenIssuer Issuer { get; private set; }

        // Loads this lobby's signing key, adds the worlds of a trusted registry, and fetches every federated world's
        // key set. A world that cannot be reached now is retried when a player needs it.
        public static void Configure(FFXILobbyConfig config, string configPath)
        {
            if (!string.IsNullOrEmpty(config.FederationServerId))
            {
                try
                {
                    SigningKey key = SigningKey.FromPaserk(config.FederationKeyId, File.ReadAllText(config.FederationSigningKey).Trim());
                    Issuer = new TokenIssuer(config.FederationServerId, key);
                    Program.Log.Info($"Federation: signing as {Issuer.ServerId} (key {key.KeyId})");
                }
                catch (Exception e) when (e is IOException or FormatException or ArgumentException or UnauthorizedAccessException)
                {
                    Program.Log.Error($"Federation: cannot load the signing key ({e.Message}); federated worlds will refuse logins");
                }
            }

            if (!string.IsNullOrEmpty(config.RegistryUrl))
                AddRegistryWorlds(config, configPath);

            foreach (WorldContainer world in config.WorldList.Where(w => w.IsFederated))
                Connect(world);
        }

        // The gateway of a federated world, connecting first if its key set has not been fetched yet.
        public static GatewayClient GatewayFor(WorldContainer world)
        {
            if (world.Gateway == null && DateTime.UtcNow - world.LastConnectAttempt >= RetryInterval)
                Connect(world);
            if (world.Gateway == null)
                Program.Log.Error($"Federation: world {world.World.Name} ({world.Trust}) is not reachable");
            else if (Issuer == null)
                Program.Log.Error($"Federation: no signing key is loaded, so world {world.World.Name} cannot be used");
            return Issuer == null ? null : world.Gateway;
        }

        private static void Connect(WorldContainer world)
        {
            world.LastConnectAttempt = DateTime.UtcNow;
            if (Issuer == null)
                return;
            try
            {
                KeySet set = GatewayClient.FetchKeySetAsync(world.KeySetUrl, world.Trust, world.Pin).GetAwaiter().GetResult();
                if (set.World == null)
                {
                    Program.Log.Error($"Federation: {world.KeySetUrl} is the key set of {set.ServerId}, which does not describe a world");
                    return;
                }
                string gateway = string.IsNullOrEmpty(world.GatewayOverride) ? set.World.Gateway : world.GatewayOverride;
                world.Gateway?.Dispose();
                world.Gateway = new GatewayClient(gateway, world.Trust, Issuer, world.Pin);
                Uri gatewayUri = new(gateway);
                world.GatewayIsRemote = !gatewayUri.IsLoopback &&
                                        !(IPAddress.TryParse(gatewayUri.Host, out IPAddress gatewayIp) && !IsPublic(gatewayIp));
                world.Expansions = set.World.Expansions;
                // The search server the world publishes, unless lobby.cfg names one
                if (world.CacheIp == 0 && set.World.Search is { } search && IPEndPoint.TryParse(search, out IPEndPoint searchAt) &&
                    searchAt.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    world.CacheIp = BitConverter.ToUInt32(searchAt.Address.GetAddressBytes());
                    world.CachePort = (uint)searchAt.Port;
                }
                if (world.NameFromKeySet && set.Name != null)
                    world.Rename(set.Name.Length > 15 ? set.Name[..15] : set.Name);
                Program.Log.Info($"Federation: world {world.World.Num} \"{world.World.Name}\" is {world.Trust} at {gateway} (expansions 0x{world.Expansions:X})");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or FormatException or IOException or InvalidOperationException)
            {
                Program.Log.Warn($"Federation: cannot load the key set of {world.Trust} from {world.KeySetUrl}: {e.Message}");
            }
        }

        // A registry lists worlds by server id and key set URL. Worlds lobby.cfg already names keep their entry; the
        // others get a lobby world number that is kept in federation-worlds.json, so their characters' content ids
        // stay valid across restarts.
        private static void AddRegistryWorlds(FFXILobbyConfig config, string configPath)
        {
            Registry registry;
            try
            {
                string token = config.RegistryUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? GatewayClient.CreateHttpClient(config.RegistryPin, TimeSpan.FromSeconds(10)).GetStringAsync(config.RegistryUrl).GetAwaiter().GetResult()
                    : File.ReadAllText(config.RegistryUrl);
                registry = Registry.Open(token, config.RegistryTrust, DateTimeOffset.UtcNow);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or FormatException or IOException)
            {
                Program.Log.Error($"Federation: cannot load the registry {config.RegistryUrl}: {e.Message}");
                return;
            }

            string numbersPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "federation-worlds.json");
            Dictionary<string, ushort> numbers = LoadWorldNumbers(numbersPath);
            bool changed = false;
            foreach (RegistryEntry entry in registry.Servers.Where(s => s.Role == RegistryEntry.World))
            {
                if (config.WorldList.Any(w => w.Trust == entry.Id))
                    continue;
                if (!numbers.TryGetValue(entry.Id, out ushort num))
                {
                    num = (ushort)(Math.Max(config.WorldList.Select(w => (int)w.World.Num).DefaultIfEmpty(100).Max(),
                                            numbers.Values.Select(v => (int)v).DefaultIfEmpty(100).Max()) + 1);
                    numbers[entry.Id] = num;
                    changed = true;
                }
                config.WorldList.Add(WorldContainer.Federated(num, entry.Id[4..12], entry.Id, entry.KeySetUrl, entry.Pin, gateway: null, nameFromKeySet: true));
            }
            if (changed)
                File.WriteAllText(numbersPath, JsonSerializer.Serialize(numbers, new JsonSerializerOptions { WriteIndented = true }));
            Program.Log.Info($"Federation: registry {registry.Name ?? registry.ServerId} lists {registry.Servers.Count} server(s)");
        }

        private static Dictionary<string, ushort> LoadWorldNumbers(string path)
        {
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, ushort>>(File.ReadAllText(path)) ?? [];
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
                return [];
            }
        }

        // The characters of this member on a federated world; null when the world could not answer.
        public static IReadOnlyList<GatewayCharacter> ListCharacters(WorldContainer world, string polId)
        {
            GatewayClient gateway = GatewayFor(world);
            if (gateway == null)
                return null;
            var result = gateway.ListCharactersAsync(polId).GetAwaiter().GetResult();
            if (!result.Ok)
                Program.Log.Warn($"{polId} - World {world.World.Name} would not list characters: {result.Error}");
            return result.Ok ? result.Value : null;
        }

        // The new character's id on the world, or 0 with the world's reason in error (null when it could not be asked)
        public static uint CreateCharacter(WorldContainer world, string polId, NewCharacter character, out string error)
        {
            error = null;
            GatewayClient gateway = GatewayFor(world);
            if (gateway == null)
                return 0;
            var result = gateway.CreateCharacterAsync(polId, character).GetAwaiter().GetResult();
            if (!result.Ok)
            {
                error = result.Error;
                Program.Log.Warn($"{polId} - World {world.World.Name} refused to create {character.Name}: {result.Error}");
            }
            return result.Ok ? result.Value : 0;
        }

        public static bool DeleteCharacter(WorldContainer world, string polId, uint charId)
        {
            GatewayClient gateway = GatewayFor(world);
            var result = gateway?.DeleteCharacterAsync(polId, charId).GetAwaiter().GetResult();
            if (result is { Ok: false } r)
                Program.Log.Warn($"{polId} - World {world.World.Name} refused to delete character {charId}: {r.Error}");
            return result is { Ok: true };
        }

        public static bool RenameCharacter(WorldContainer world, string polId, uint charId, string name)
        {
            GatewayClient gateway = GatewayFor(world);
            var result = gateway?.RenameCharacterAsync(polId, charId, name).GetAwaiter().GetResult();
            if (result is { Ok: false } r)
                Program.Log.Warn($"{polId} - World {world.World.Name} refused to rename character {charId}: {r.Error}");
            return result is { Ok: true };
        }

        // Not loopback, private (10/8, 172.16/12, 192.168/16), CGNAT (100.64/10, Tailscale) or link-local
        public static bool IsPublic(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
                return false;
            byte[] b = address.MapToIPv4().GetAddressBytes();
            return !(b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) ||
                     (b[0] == 100 && (b[1] & 0xC0) == 64) || (b[0] == 169 && b[1] == 254));
        }

        public record Admission(uint MapIp, uint MapPort);

        // Sends the world a world-entry token for this character. Returns the map server the world chose for it, or
        // null when the world refused (the reason is logged).
        public static Admission Admit(WorldContainer world, string polId, WorldEntry entry)
        {
            GatewayClient gateway = GatewayFor(world);
            if (gateway == null)
                return null;
            var result = gateway.EnterAsync(polId, entry).GetAwaiter().GetResult();
            if (!result.Ok || !IPAddress.TryParse(result.Value.Ip, out IPAddress mapIp))
            {
                Program.Log.Warn($"{polId} - World {world.World.Name} refused character {entry.CharId}: {result.Status} {result.Error}");
                return null;
            }
            Program.Log.Info($"{polId} - World {world.World.Name} admitted character {entry.CharId} from {entry.ClientIp} to map server {mapIp}:{result.Value.Port}");
            return new(BitConverter.ToUInt32(mapIp.GetAddressBytes()), result.Value.Port);
        }

        // One-time move of existing characters onto federation: for every member with characters on a federated world
        // that lobby.cfg still gives database settings, map the member to the world account that owns them
        // (accounts_federated), so the world's gateway recognises them. Run with --federate-accounts.
        public static int MapExistingAccounts(FFXILobbyConfig config)
        {
            if (Issuer == null)
            {
                Program.Log.Error("Federation: --federate-accounts needs <federation> with a signing key in lobby.cfg");
                return 1;
            }
            int failures = 0;
            foreach (WorldContainer world in config.WorldList.Where(w => w.IsFederated && !string.IsNullOrEmpty(w.DbHost)))
            {
                foreach (var (polId, accids) in Database.GetMemberAccounts(world))
                {
                    if (accids.Count != 1)
                    {
                        Program.Log.Error($"{polId} - characters on world {world.World.Name} belong to {accids.Count} accounts ({string.Join(", ", accids)}); move them to one first");
                        failures++;
                        continue;
                    }
                    string outcome = Database.MapFederatedAccount(world, Issuer.ServerId, polId, accids[0]);
                    Program.Log.Info($"{polId} - world {world.World.Name}: account {accids[0]} {outcome}");
                    if (outcome.StartsWith("conflict"))
                        failures++;
                }
            }
            return failures == 0 ? 0 : 2;
        }
    }
}
