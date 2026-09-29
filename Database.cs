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

using Crystal.FFXILobbyServer.Network.Models;
using Crystal.POLProfile.DataObjects.Pol.Character;
using MySqlConnector;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace Crystal.FFXILobbyServer
{
    class Database
    {
        public static string POL_DB_HOST = "127.0.0.1";
        public static string POL_DB_PORT = "3306";
        public static string POL_DB_NAME = "playonline";
        public static string POL_DB_USERNAME = "root";
        public static string POL_DB_PASSWORD = "";

        public static Tuple<byte[], string> GetPlayonlineRandomValue(byte[] authHash)
        {
            using MySqlConnection conn = new($"Server={POL_DB_HOST}; Port={POL_DB_PORT}; Database={POL_DB_NAME}; UID={POL_DB_USERNAME}; Password={POL_DB_PASSWORD}");
            try
            {
                conn.Open();
                MySqlCommand cmd = new("SELECT polRandomValueBinary, polId FROM sessions WHERE polContentAuthHash = @authHash", conn);
                cmd.Parameters.AddWithValue("@authHash", authHash);

                using MySqlDataReader Reader = cmd.ExecuteReader();
                while (Reader.Read())
                {
                    byte[] randomValue = new byte[0x10];
                    long bytesRead = Reader.GetBytes("polRandomValueBinary", 0, randomValue, 0, 0x10);
                    if (bytesRead == 0x10)
                    {
                        string polProData = Reader.GetString("polId");
                        return new(randomValue, polProData);
                    }
                }
            }
            catch (MySqlException e)
            {
                Program.Log.Error(e.ToString());
            }
            finally
            {
                conn.Dispose();
            }
            return null;
        }

        public static CharacterPrimitive[] GetFFXIContentIds(string polProData)
        {
            List<CharacterPrimitive> charaPrims = new();

            using MySqlConnection conn = new($"Server={POL_DB_HOST}; Port={POL_DB_PORT}; Database={POL_DB_NAME}; UID={POL_DB_USERNAME}; Password={POL_DB_PASSWORD}");
            {
                try
                {
                    conn.Open();
                    string query = @"
                        SELECT id, subId FROM characters 
                        WHERE 
                            polId = @polPro AND 
                            contentClass = 1
                        ORDER BY linkPosition
                        ";

                    MySqlCommand cmd = new(query, conn);
                    cmd.Parameters.AddWithValue("@polPro", polProData);
                    using MySqlDataReader reader = cmd.ExecuteReader();
                    byte i = 0;
                    while (reader.Read())
                    {
                        ulong cId = reader.GetUInt64("id");
                        uint cSubId = reader.GetUInt32("subId");

                        CharacterPrimitive characterPrimitive = new()
                        {
                            IsValid = 1,
                            AttachOrder = i++,
                            ContentsClass = 1,
                            ContentsSubUserId = cSubId,
                            ContentsId = cId
                        };

                        charaPrims.Add(characterPrimitive);
                    }
                    return [.. charaPrims];
                }
                catch (MySqlException e)
                {
                    Program.Log.Error(e.ToString());
                }
                finally
                {
                    conn.Dispose();
                }
            }
            return [];
        }

        public static bool UpdateFFXISubContentId(ulong contentId, uint subContentId, string name)
        {
            using MySqlConnection conn = new($"Server={POL_DB_HOST}; Port={POL_DB_PORT}; Database={POL_DB_NAME}; UID={POL_DB_USERNAME}; Password={POL_DB_PASSWORD}");
            {
                try
                {
                    conn.Open();
                    string query = @"
                        UPDATE characters
                        SET subId = @subContentId, name = @name
                        WHERE id = @contentId
                        ";

                    MySqlCommand cmd = new(query, conn);
                    cmd.Parameters.AddWithValue("@contentId", contentId);
                    cmd.Parameters.AddWithValue("@subContentId", subContentId);
                    cmd.Parameters.AddWithValue("@name", name);
                    cmd.ExecuteNonQuery();
                }
                catch (MySqlException e)
                {
                    Program.Log.Error(e.ToString());
                    return false;
                }
                finally
                {
                    conn.Dispose();
                }
            }
            return true;
        }

        public static Character[] GetCharacters(List<WorldContainer> worldList, CharacterPrimitive[] contentIdList)
        {
            // Go through each content id. If there is a server id, grab chara data, otherwise set to blank.
            int indx = 0;
            Character[] characters = new Character[contentIdList.Length];
            foreach (CharacterPrimitive polChar in contentIdList)
            {
                // This content id does not have a character
                if (polChar.ContentsSubUserId == 0)
                {
                    characters[indx].FFXiId = (uint)(polChar.ContentsId & 0xFFFFFFFFL);
                    characters[indx].FFXiIdWorld = 0;
                    characters[indx].WorldId = 0;
                    characters[indx].Status = 1;
                    characters[indx].Name = " ";
                    indx++;
                    continue;
                }

                // This content id has a character, grab data. World id is high 32bits of subid.
                ushort worldNum = (ushort)((polChar.ContentsSubUserId >> 16) & 0xFFFF);
                WorldContainer world = worldList.Where(container => container.World.Num == worldNum).FirstOrDefault();
                using MySqlConnection conn = new($"Server={world.DbHost}; Port={world.DbPort}; Database={world.DbName}; UID={world.DbUser}; Password={world.DbPass}");
                try
                {
                    conn.Open();
                    MySqlCommand cmd = new(
                        @"
                    SELECT charid, charname, doRename, pos_zone, pos_prevzone, mjob,
                    race, face, head, body, hands, legs, feet, main, sub,
                    war, mnk, whm, blm, rdm, thf, pld, drk, bst, brd, rng,
                    sam, nin, drg, smn, blu, cor, pup, dnc, sch, geo, run,
                    gmlevel, nation, size, sjob
                    FROM chars
                    INNER JOIN char_stats USING(charId)
                    INNER JOIN char_look  USING(charId)
                    INNER JOIN char_jobs  USING(charId)
                    WHERE charId = @charId
                    LIMIT 16", conn);
                    cmd.Parameters.AddWithValue("@charId", polChar.ContentsSubUserId & 0xFFFF);

                    using MySqlDataReader reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        CharaInfo characterInfo = new();

                        characters[indx].FFXiId = (uint) (polChar.ContentsId & 0xFFFFFFFFL); // ContentId is 64bit but FFXI truncates it to 32bit.
                        characters[indx].FFXiIdWorld = (ushort) (polChar.ContentsSubUserId & 0xFFFF); // This should match, char id + world id. If 0 it's deleted.
                        characters[indx].WorldId = (ushort) ((polChar.ContentsSubUserId >> 16) & 0xFFFF);
                        //character.FfxiIdWorldTbl = charIdExtra; //Doesn't exist in 2010
                        characters[indx].Status = 1;
                        characters[indx].Rename = (ushort) (reader.GetByte("doRename") == 0 ? 0 : 1);
                        characters[indx].Name = reader.GetString("charname").PadRight(16, '\0')[..16];
                        characters[indx].WorldName = world.World.Name;

                        ushort zone = reader.GetUInt16("pos_zone");
                        byte mainJob = reader.GetByte("mjob");
                        characterInfo.RaceNum = reader.GetUInt16("race");
                        characterInfo.MJobNum = reader.GetByte("mjob");
                        characterInfo.MJobLevel = reader.GetByte(14 + mainJob); // Index-based lookup from C++ logic
                        characterInfo.SJobNum = reader.GetByte("sjob");
                        characterInfo.FaceNum = reader.GetUInt16("face");
                        characterInfo.TownNum = reader.GetByte("nation");

                        characterInfo.ZoneNumLow = (byte)zone;
                        characterInfo.ZoneNumHigh = (byte)((zone >> 8) & 1);

                        characterInfo.HairNum = reader.GetByte("face");
                        characterInfo.Size = reader.GetByte("size");

                        characterInfo.FaceModelId = reader.GetUInt16("face");
                        characterInfo.HeadModelId = reader.GetUInt16("head");
                        characterInfo.BodyModelId = reader.GetUInt16("body");
                        characterInfo.HandsModelId = reader.GetUInt16("hands");
                        characterInfo.LegsModelId = reader.GetUInt16("legs");
                        characterInfo.FeetModelId = reader.GetUInt16("feet");
                        characterInfo.MainWeaponModelId = reader.GetUInt16("main");
                        characterInfo.SubWeaponModelId = reader.GetUInt16("sub");

                        characterInfo.GenFlag = 0;
                        characterInfo.AnonStatusFlag = 0;
                        characterInfo.WorldNum = (ushort) world.World.Num;

                        characters[indx].CharaInfo = characterInfo;

                        indx++;
                    }
                }
                catch (MySqlException e)
                {
                    Program.Log.Error(e.ToString());
                    return null;
                }
                finally
                {
                    conn.Dispose();
                }
            }

            return characters;
        }

        // ---- LandSandBoat accounts ----------------------------------------------------------------------
        // The world database is LandSandBoat's, which ties every character and session to a row of its
        // `accounts` table (chars.accid, accounts_sessions.accid with a UNIQUE key, account-wide state on
        // the map server). PlayOnline members have no such row, so each PlayOnline ID gets a shadow
        // account: login "pol:<id>", and a password no input can match (LSB checks non-bcrypt passwords
        // with PASSWORD(), whose output always starts with '*'), so it cannot be used through xiloader.
        // xi_connect keeps serving its own accounts next to this.

        private const string LSB_POL_LOGIN_PREFIX = "pol:";
        private const string LSB_UNUSABLE_PASSWORD = "!pol";
        private const uint LSB_FIRST_ACCOUNT_ID = 1000;
        private const uint MAX_CHARID = 0xFFFF; // the charid is the low 16 bits of the PlayOnline sub id

        private static uint GetOrCreateLsbAccount(MySqlConnection conn, string polId)
        {
            string login = (LSB_POL_LOGIN_PREFIX + polId);
            if (login.Length > 16)
                login = login[..16];

            MySqlCommand find = new("SELECT id FROM accounts WHERE login = @login LIMIT 1", conn);
            find.Parameters.AddWithValue("@login", login);
            object found = find.ExecuteScalar();
            if (found != null && found != DBNull.Value)
                return Convert.ToUInt32(found);

            MySqlCommand create = new(@"
                INSERT INTO accounts(id, login, password, timecreate, timelastmodify, status, priv)
                SELECT GREATEST(COALESCE(MAX(id), 0) + 1, @firstId), @login, @password, NOW(), NOW(), 1, 1 FROM accounts;
                SELECT id FROM accounts WHERE login = @login LIMIT 1;
            ", conn);
            create.Parameters.AddWithValue("@firstId", LSB_FIRST_ACCOUNT_ID);
            create.Parameters.AddWithValue("@login", login);
            create.Parameters.AddWithValue("@password", LSB_UNUSABLE_PASSWORD);
            uint accid = Convert.ToUInt32(create.ExecuteScalar());
            Program.Log.Info($"{polId} - Created LandSandBoat account {accid} ({login})");
            return accid;
        }

        // Characters made before this code have accid 0; give them to the member's shadow account.
        private static uint GetCharacterAccount(MySqlConnection conn, uint charId, string polId)
        {
            MySqlCommand get = new("SELECT accid, original_accid FROM chars WHERE charid = @charId", conn);
            get.Parameters.AddWithValue("@charId", charId);
            uint accid = 0, originalAccid = 0;
            using (MySqlDataReader reader = get.ExecuteReader())
            {
                if (!reader.Read())
                    return 0;
                accid = reader.GetUInt32("accid");
                originalAccid = reader.GetUInt32("original_accid");
            }

            if (accid != 0)
                return accid;
            if (originalAccid != 0)
                return 0; // deleted (LandSandBoat keeps the row with accid 0)

            accid = GetOrCreateLsbAccount(conn, polId);
            MySqlCommand adopt = new("UPDATE chars SET accid = @accid WHERE charid = @charId AND accid = 0", conn);
            adopt.Parameters.AddWithValue("@accid", accid);
            adopt.Parameters.AddWithValue("@charId", charId);
            adopt.ExecuteNonQuery();
            return accid;
        }

        public static uint CreateCharacter(WorldContainer world, CharaInfo charaInfo, string name, uint startZone, string polId)
        {
            using MySqlConnection conn = new($"Server={world.DbHost}; Port={world.DbPort}; Database={world.DbName}; UID={world.DbUser}; Password={world.DbPass}");
            try
            {
                conn.Open();

                uint accid = GetOrCreateLsbAccount(conn, polId);

                // The next free charid that fits the 16 bits of the sub id. xi_connect allocates from the
                // same table (MAX + 1), so the two only differ once ids pass 0xFFFF: then find a gap.
                uint charId = 0;
                MySqlCommand getCharIdCmd = new("SELECT COALESCE(MAX(charid), 0) + 1 FROM chars WHERE charid <= @max", conn);
                getCharIdCmd.Parameters.AddWithValue("@max", MAX_CHARID);
                charId = Convert.ToUInt32(getCharIdCmd.ExecuteScalar());
                if (charId > MAX_CHARID)
                {
                    MySqlCommand gapCmd = new(@"
                        SELECT MIN(c.charid) + 1 FROM chars c
                        WHERE c.charid < @max AND NOT EXISTS (SELECT 1 FROM chars n WHERE n.charid = c.charid + 1)
                    ", conn);
                    gapCmd.Parameters.AddWithValue("@max", MAX_CHARID);
                    object gap = gapCmd.ExecuteScalar();
                    if (gap == null || gap == DBNull.Value)
                    {
                        Program.Log.Error($"{polId} - No free character id below 0x10000");
                        return 0;
                    }
                    charId = Convert.ToUInt32(gap);
                }

                // We have a new subid!
                uint newSubId = (world.World.Num << 16) | charId;

                // Create character
                MySqlCommand cmd = new(@"
                    INSERT INTO chars(charid,accid,charname,pos_zone,nation) VALUES(@charId, @accid, @charName, @startZone, @nation);
                    INSERT INTO char_look(charid,face,race,size) VALUES(@charId, @face, @race, @size);
                    INSERT INTO char_stats(charid,mjob) VALUES(@charId, @job);
                    INSERT INTO char_exp(charid) VALUES(@charId) ON DUPLICATE KEY UPDATE charid = charid;
                    INSERT INTO char_flags(charid) VALUES(@charId) ON DUPLICATE KEY UPDATE disconnecting = disconnecting;
                    INSERT INTO char_jobs(charid) VALUES(@charId) ON DUPLICATE KEY UPDATE charid = charid;
                    INSERT INTO char_points(charid) VALUES(@charId) ON DUPLICATE KEY UPDATE charid = charid;
                    INSERT INTO char_unlocks(charid) VALUES(@charId) ON DUPLICATE KEY UPDATE charid = charid;
                    INSERT INTO char_profile(charid) VALUES(@charId) ON DUPLICATE KEY UPDATE charid = charid;
                    INSERT INTO char_storage(charid) VALUES(@charId) ON DUPLICATE KEY UPDATE charid = charid;
                    DELETE FROM char_inventory WHERE charid = @charId;
                    INSERT INTO char_inventory(charid) VALUES(@charId);
                    INSERT INTO char_vars(charid, varname, value) VALUES(@charId, @cutsceneVar, 1);
                ", conn);

                cmd.Parameters.AddWithValue("@charId", charId);
                cmd.Parameters.AddWithValue("@accid", accid);
                cmd.Parameters.AddWithValue("@charName", name);
                cmd.Parameters.AddWithValue("@startZone", startZone);
                cmd.Parameters.AddWithValue("@nation", charaInfo.TownNum);
                cmd.Parameters.AddWithValue("@face", charaInfo.FaceNum);
                cmd.Parameters.AddWithValue("@race", charaInfo.RaceNum);
                cmd.Parameters.AddWithValue("@size", charaInfo.Size);
                cmd.Parameters.AddWithValue("@job", charaInfo.MJobNum);
                cmd.Parameters.AddWithValue("@cutsceneVar", "HQuest[newCharacterCS]notSeen");

                cmd.ExecuteNonQuery();

                return newSubId;
            }
            catch (MySqlException e)
            {
                Program.Log.Error(e.ToString());
                return 0;
            }
            finally
            {
                conn.Dispose();
            }
        }

        public static bool DeleteCharacter(WorldContainer world, uint ffxiWorldId)
        {
            using MySqlConnection conn = new($"Server={world.DbHost}; Port={world.DbPort}; Database={world.DbName}; UID={world.DbUser}; Password={world.DbPass}");
            try
            {
                conn.Open();
                // As xi_connect does: keep the row (and every char_* row) and detach it from the account.
                MySqlCommand cmd = new(@"
                    UPDATE chars SET original_accid = accid, accid = 0 WHERE charid = @ffxiWorldId AND accid <> 0;
                    DELETE FROM accounts_sessions WHERE charid = @ffxiWorldId;
                ", conn);
                cmd.Parameters.AddWithValue("@ffxiWorldId", ffxiWorldId);

                cmd.ExecuteNonQuery();
                return true;
            }
            catch (MySqlException e)
            {
                Program.Log.Error(e.ToString());
            }
            finally
            {
                conn.Dispose();
            }
            return false;
        }

        public static bool RenameCharacter(WorldContainer world, uint ffxiWorldId, string newName)
        {
            using MySqlConnection conn = new($"Server={world.DbHost}; Port={world.DbPort}; Database={world.DbName}; UID={world.DbUser}; Password={world.DbPass}");
            try
            {
                conn.Open();
                string query = @"
                        UPDATE chars
                        SET charname = @newName, doRename = 0
                        WHERE charid = @ffxiWorldId
                        ";

                MySqlCommand cmd = new(query, conn);
                cmd.Parameters.AddWithValue("@ffxiWorldId", ffxiWorldId);
                cmd.Parameters.AddWithValue("@newName", newName);
                cmd.ExecuteNonQuery();
                return true;
            }
            catch (MySqlException e)
            {
                Program.Log.Error(e.ToString());
            }
            finally
            {
                conn.Dispose();
            }
            return false;
        }

        public static bool AddSession(WorldContainer world, uint ffxiWorldId, string polId, byte[] key, uint serverAddress, uint serverPort, uint clientAddress)
        {
            using MySqlConnection conn = new($"Server={world.DbHost}; Port={world.DbPort}; Database={world.DbName}; UID={world.DbUser}; Password={world.DbPass}");
            try
            {
                conn.Open();

                uint accid = GetCharacterAccount(conn, ffxiWorldId, polId);
                if (accid == 0)
                {
                    Program.Log.Error($"{polId} - Character {ffxiWorldId} has no account (deleted?)");
                    return false;
                }

                // Mirror xi_connect (data_session.cpp): a session left behind by a zone-out the other map
                // server never saw goes after 2 minutes; a character still logged in is refused.
                MySqlCommand stale = new(@"
                    DELETE FROM accounts_sessions
                    WHERE accid = @accid AND client_port = 0 AND last_zoneout_time <= SUBTIME(NOW(), '00:02:00')
                ", conn);
                stale.Parameters.AddWithValue("@accid", accid);
                stale.ExecuteNonQuery();

                MySqlCommand active = new("SELECT charid FROM accounts_sessions WHERE accid = @accid LIMIT 1", conn);
                active.Parameters.AddWithValue("@accid", accid);
                object activeChar = active.ExecuteScalar();
                if (activeChar != null && activeChar != DBNull.Value)
                {
                    Program.Log.Warn($"{polId} - Account {accid} already has character {activeChar} logged in");
                    return false;
                }

                MySqlCommand cmd = new(@"
                    INSERT INTO accounts_sessions(accid, charid, session_key, server_addr, server_port, client_addr, version_mismatch)
                    VALUES(@accid, @charid, @session_key, @server_addr, @server_port, @client_addr, @version_mismatch)
                ", conn);
                cmd.Parameters.AddWithValue("@accid", accid);
                cmd.Parameters.AddWithValue("@session_key", key);
                cmd.Parameters.AddWithValue("@charid", ffxiWorldId);
                cmd.Parameters.AddWithValue("@server_addr", serverAddress);
                cmd.Parameters.AddWithValue("@server_port", serverPort);
                cmd.Parameters.AddWithValue("@client_addr", clientAddress);
                cmd.Parameters.AddWithValue("@version_mismatch", false);

                cmd.ExecuteNonQuery();
                return true;
            }
            catch (MySqlException e)
            {
                Program.Log.Error(e.ToString());
            }
            finally
            {
                conn.Dispose();
            }
            return false;
        }
    }
}
