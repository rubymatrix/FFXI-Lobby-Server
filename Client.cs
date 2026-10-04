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

using Crystal.FFXILobbyServer.Network;
using Crystal.FFXILobbyServer.Network.Receive;
using Crystal.FFXILobbyServer.Network.Send;
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Crystal.FFXILobbyServer.Network.Models;
using Crystal.POLProfile.DataObjects.Pol.Character;
using Org.BouncyCastle.Bcpg;

namespace Crystal.FFXILobbyServer
{
    class Client
    {
        // Connection stuff
        public Server Server;
        public Socket ClientSocket;
        private IPAddress ClientIp;
        public int ClientPort;
        public byte[] Buffer = new byte[0xffff];
        public int LastPartialSize = 0;
        private bool Disconnected = false;

        // PolPro
        private bool IsLoggedIn = false;

        private string PolProData = "";
        private string ClientVersion = "";   // version string of the lobby login (the client's patch.ver)
        private uint   ClientExpansions = 0; // expansions the client has installed
        private byte[] Password;
        public uint Md5Key;

        // Session
        private Character[] CachedCharaList = null;
        private string RequestedNewCharName = "";
        private WorldContainer RequestedNewCharWorld; // the world the client named when it reserved the name

        // Lobby error codes the client shows for a new character (LandSandBoat login_errors.h)
        public const uint ERR_NAME_UNAVAILABLE = 313; // "The character name you entered is unavailable."
        public const uint ERR_NAME_SERVER      = 314; // "Failed to register with the name server."

        public bool IsDisconnected() => Disconnected;

        public string GetPolProData() => PolProData;

        public string GetAddress() => $"{ClientIp}:{ClientPort}";

        public Client(Server server, Socket socket)
        {
            var endpoint = ((IPEndPoint)socket.RemoteEndPoint);

            Server = server;
            ClientSocket = socket;
            ClientIp = endpoint.Address;
            ClientPort = endpoint.Port;
        }

        public void SendPacket(uint opcode, byte[] data)
        {
            FFXIPacket packet = new(opcode, data);
            ClientSocket.Send(packet.GetPacketBytes());

            //Program.Log.Debug("\n" + Utils.ByteArrayToHex(packet.GetPacketBytes()));
        }

        public void SendBytes(byte[] packetBytes)
        {
            ClientSocket.Send(packetBytes);
        }

        public void Disconnect()
        {
            if (Disconnected)
                return;

            Disconnected = true;

            try
            {
                ClientSocket.Shutdown(SocketShutdown.Both);
                ClientSocket.Close();
            }
            catch (Exception) { }
        }

        public override string ToString()
        {
            return IsLoggedIn ? PolProData : ClientIp.ToString();
        }

        // Request info from PolPro
        public bool Login(LobbyLoginPkt loginPkt, out uint key, out ulong serverExpCode)
        {
            if (!Utils.DecryptAuthPassword(loginPkt.AuthCode, out byte[] authHash, out uint clientIp, out ushort clientPort))
            {
                key = 0;
                serverExpCode = 0;
                return false;
            }

            // The member's PlayOnline session. The login must come from the address that session was opened from
            // (both servers see the same public address), and its hash opens one lobby login only.
            var auth = Database.ClaimContentAuth(authHash, Server.SingleUseContentAuth);
            if (auth == null)
            {
                Program.Log.Warn($"Lobby login from {ClientIp}: unknown or already used contents-auth hash");
                key = 0;
                serverExpCode = 0;
                return false;
            }
            if (Server.CheckClientIp && auth.ClientIp != ClientIp.MapToIPv4().ToString())
            {
                Program.Log.Warn($"{auth.PolId} - Lobby login from {ClientIp.MapToIPv4()}, but the PlayOnline session is from {auth.ClientIp}; refused (lobby.cfg checkClientIp)");
                key = 0;
                serverExpCode = 0;
                return false;
            }

            Password = auth.RandomValue;
            PolProData = auth.PolId;

            if (Password == null)
            {
                key = 0;
                serverExpCode = 0;
                return false;
            }

            Md5Key = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));

            key = Md5Key;

            // The expansions the account has: the ones the server enables (the ENABLE_* settings of every world that
            // names its settings folder in lobby.cfg; the client's own installed set when none does)
            uint? enabled = null;
            foreach (var world in Server.WorldList)
            {
                uint? w = world.IsFederated ? world.Expansions : Utils.ServerExpansions(world.SettingsDir);
                if (w != null)
                    enabled = (enabled ?? 0) | w.Value;
            }
            serverExpCode = enabled ?? loginPkt.ClientExpCode;
            // "20100904_2" followed by padding and a trailing marker: keep the version itself
            ClientVersion    = System.Text.RegularExpressions.Regex.Match(System.Text.Encoding.ASCII.GetString(loginPkt.VersionCode), "^[0-9A-Za-z_]*").Value;
            ClientExpansions = loginPkt.ClientExpCode;
            Program.Log.Info($"Lobby login: server expansions 0x{serverExpCode:X}, client installed 0x{loginPkt.ClientExpCode:X}, version {System.Text.Encoding.ASCII.GetString(loginPkt.VersionCode).TrimEnd('\0')}");

            IsLoggedIn = true;
            return true;
        }

        // Verify the received pwd is right
        public bool VerifyPassword(byte[] incomingPassword)
        {
            Md5Key++;
            if (!IsLoggedIn)
                return false;

            byte[] digest = new byte[0x14];
            Array.Copy(Password, digest, 0x10);
            Array.Copy(BitConverter.GetBytes(Md5Key), 0, digest, 0x10, 4);
            return incomingPassword.SequenceEqual(MD5.HashData(digest));
        }

        public Character[] GetCharacters(bool invalidate)
        {
            if (!invalidate && CachedCharaList != null)
                return [.. CachedCharaList];

            // Get all the ids
            CharacterPrimitive[] contentIds = Database.GetFFXIContentIds(PolProData);

            // Grab chara data from each server
            CachedCharaList = Database.GetCharacters(Server.WorldList, contentIds, PolProData);
            return CachedCharaList;
        }

        public void ClearCharacters()
        {
            CachedCharaList = null;
        }

        // 0 when the character was made, else the lobby error to send the client
        public uint CreateCharacter(uint contentId, byte[] password, CharaInfo charaInfo)
        {
            // Check Race

            // Set starting zone
            uint[] bastokStartingZones = { 0xEA, 0xEB, 0xEC };
            uint[] sandoriaStartingZones = { 0xE6, 0xE7, 0xE8 };
            uint[] windurstStartingZones = { 0xEE, 0xF0, 0xF1 };
            Random random = new();
            uint startZone = 0;

            switch (charaInfo.TownNum)
            {
                case 0x02: // windy start
                    {
                        startZone = windurstStartingZones[random.Next(3)];
                        break;
                    }
                case 0x01: // bastok start
                    {
                        startZone = bastokStartingZones[random.Next(3)];
                        break;
                    }
                case 0x00: // sandy start
                    {
                        startZone = sandoriaStartingZones[random.Next(3)];
                        break;
                    }
            }

            // Create a new character on the world the client named before (the world field of the character info
            // is not a lobby.cfg position) and update the content id
            WorldContainer world = RequestedNewCharWorld;
            if (world == null)
                return ERR_NAME_SERVER;
            string name = RequestedNewCharName.TrimEnd('\0');
            uint newSubId;
            if (world.IsFederated)
            {
                // The world picks the starting zone and enforces its own name and account rules
                uint charId = Federation.CreateCharacter(world, PolProData, new XiToken.NewCharacter(
                    name, (byte)charaInfo.RaceNum, (byte)charaInfo.FaceNum, charaInfo.Size, charaInfo.MJobNum, charaInfo.TownNum), out string error);
                if (error is "name_taken" or "name_invalid")
                    return ERR_NAME_UNAVAILABLE;
                newSubId = charId is > 0 and <= 0xFFFF ? (world.World.Num << 16) | charId : 0;
            }
            else
                newSubId = Database.CreateCharacter(world, charaInfo, RequestedNewCharName, startZone, PolProData);
            if (newSubId != 0 && Database.UpdateFFXISubContentId(contentId, newSubId, RequestedNewCharName))
                return 0;
            return ERR_NAME_SERVER;
        }

        public WorldServerInfo? DoSelect(uint contentId, uint ffxiIdWorld, uint serverAddress, uint port)
        {
            byte[] key = new byte[0x14];
            Array.Copy(Password, key, 0x10);
            Array.Copy(BitConverter.GetBytes(Md5Key + 4), 0, key, 0x10, 4);

            // This shit is stupid but we gotta find the world id to delete from the correct work.
            CharacterPrimitive[] contentIds = Database.GetFFXIContentIds(PolProData);
            foreach (CharacterPrimitive chara in contentIds)
            {
                if (chara.ContentsId == contentId && (chara.ContentsSubUserId & 0xFFFF) == ffxiIdWorld)
                {
                    WorldContainer world = Server.GetWorldFromSubContentId(chara.ContentsSubUserId);
                    IPAddress clientAddress = ((IPEndPoint)ClientSocket.RemoteEndPoint).Address.MapToIPv4();

                    // A federated world writes its own session from a signed world-entry token, and names the map
                    // server for the character's zone.
                    if (world.IsFederated)
                    {
                        string version = ClientVersion.Length > 16 ? ClientVersion[..16] : ClientVersion;
                        // A remote world's map server sees the client come from this network's public address,
                        // not the private or Tailscale one it reached the lobby on
                        IPAddress entryAddress = world.GatewayIsRemote && !Federation.IsPublic(clientAddress) && Server.FederationPublicIp != null
                            ? Server.FederationPublicIp
                            : clientAddress;
                        var entry = new XiToken.WorldEntry(ffxiIdWorld, null, entryAddress.ToString(), version, ClientExpansions, key);
                        Federation.Admission admission = Federation.Admit(world, PolProData, entry);
                        if (admission == null)
                            return null;
                        return new(world.World.Num, admission.MapIp, admission.MapPort, world.CacheIp, world.CachePort);
                    }

                    uint myIp = BitConverter.ToUInt32(clientAddress.GetAddressBytes());
                    // The session names the map server the client is sent to (lobby.cfg's world ip/port), not
                    // the address Server.cs passes in, which is hardcoded.
                    // No session (already logged in, deleted, database error): the map server would refuse
                    // the character anyway, so fail here and the lobby sends an error instead.
                    if (!Database.AddSession(world, ffxiIdWorld, PolProData, key, world.ServerIp, world.ServerPort, myIp, ClientVersion, ClientExpansions))
                        return null;
                    return new(world.World.Num, world.ServerIp, world.ServerPort, world.CacheIp, world.CachePort);
                }
            }

            return null;
        }

        public void DoDelete(uint contentId, uint ffxiIdWorld)
        {
            byte[] key = new byte[0x14];
            Array.Copy(Password, key, 0x10);
            Array.Copy(BitConverter.GetBytes(Md5Key + 4), 0, key, 0x10, 4);

            // This shit is stupid but we gotta find the world id to delete from the correct work.
            CharacterPrimitive[] contentIds = Database.GetFFXIContentIds(PolProData);
            foreach (CharacterPrimitive chara in contentIds)
            {
                if (chara.ContentsId == contentId && (chara.ContentsSubUserId & 0xFFFF) == ffxiIdWorld)
                {
                    WorldContainer world = Server.GetWorldFromSubContentId(chara.ContentsSubUserId);
                    bool deleted = world.IsFederated
                        ? Federation.DeleteCharacter(world, PolProData, ffxiIdWorld)
                        : Database.DeleteCharacter(world, ffxiIdWorld);
                    if (deleted)
                        Database.UpdateFFXISubContentId(contentId, 0, "");
                    return;
                }
            }
        }

        public void DoRename(uint contentId, uint ffxiIdWorld, string newName)
        {
            byte[] key = new byte[0x14];
            Array.Copy(Password, key, 0x10);
            Array.Copy(BitConverter.GetBytes(Md5Key + 4), 0, key, 0x10, 4);

            // This shit is stupid but we gotta find the world id to delete from the correct work.
            CharacterPrimitive[] contentIds = Database.GetFFXIContentIds(PolProData);
            foreach (CharacterPrimitive chara in contentIds)
            {
                if (chara.ContentsId == contentId && (chara.ContentsSubUserId & 0xFFFF) == ffxiIdWorld)
                {
                    WorldContainer world = Server.GetWorldFromSubContentId(chara.ContentsSubUserId);
                    bool renamed = world.IsFederated
                        ? Federation.RenameCharacter(world, PolProData, ffxiIdWorld, newName.TrimEnd('\0'))
                        : Database.RenameCharacter(world, ffxiIdWorld, newName);
                    if (renamed)
                        Database.UpdateFFXISubContentId(contentId, chara.ContentsSubUserId, newName);
                    return;
                }
            }
        }

        public void SendError(uint errCode)
        {
            SendPacket(ErrorPkt.OPCODE, new ErrorPkt() { ErrCode = errCode }.Bytes);
        }

        public void SetRequestedCharaName(string charaName, WorldContainer world)
        {
            RequestedNewCharName = charaName;
            RequestedNewCharWorld = world;
        }
    }
}
