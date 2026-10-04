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
using System;
using System.Collections.Generic;
using System.Net;
using System.Xml;

namespace Crystal.FFXILobbyServer
{
    public class FFXILobbyConfig
    {
        public readonly string ServerIp;

        public readonly string PolProNotiferId;
        public readonly string PolProNotiferPassword;
        public readonly string PolProNotiferIp;

        public readonly string PolDbHost;
        public readonly string PolDbPort;
        public readonly string PolDbName;
        public readonly string PolDbUsername;
        public readonly string PolDbPassword;

        public readonly List<WorldContainer> WorldList;

        public readonly bool SingleUseContentAuth;
        public readonly bool CheckClientIp;

        // <federation serverId="xi1..." kid="2026-10" signingKey="path to k4.secret file"/>: this lobby's xitoken
        // identity, for worlds that take characters through a federation gateway
        public readonly string FederationServerId;
        public readonly string FederationKeyId;
        public readonly string FederationSigningKey;
        // publicIp="...": the address remote worlds see this lobby's players come from, sent in their world-entry
        // tokens when a client reached the lobby from a private, CGNAT (Tailscale) or loopback address
        public readonly string FederationPublicIp;

        // <registry url="https://... or a file" trust="xi1..." pin="sha256:..."/>: a signed list of federated worlds to
        // offer besides the ones named under <worlds>
        public readonly string RegistryUrl;
        public readonly string RegistryTrust;
        public readonly string RegistryPin;

        public FFXILobbyConfig(string path) 
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Program.Log.Info($"Loading config: {path}");
            XmlDocument doc = new();

            doc.Load(path);

            // Load the server configs
            XmlNode cfgNode = doc.DocumentElement.SelectSingleNode("/lobbycfg");
            ServerIp = cfgNode.Attributes["serverIp"]?.InnerText;
            // Login hardening, both on unless set to "false": a contents-auth hash opens one lobby login, and the
            // lobby login must come from the address of the member's PlayOnline session.
            SingleUseContentAuth = !"false".Equals(cfgNode.Attributes["singleUseContentAuth"]?.InnerText, StringComparison.OrdinalIgnoreCase);
            CheckClientIp = !"false".Equals(cfgNode.Attributes["checkClientIp"]?.InnerText, StringComparison.OrdinalIgnoreCase);

            // Go through subsettings
            List<WorldContainer> tempWorldList = [];
            foreach (XmlNode cfgChildNode in doc.DocumentElement.ChildNodes)
            {
                if (cfgChildNode.Name.Equals("poldb"))
                {
                    PolDbHost = cfgChildNode.Attributes["host"]?.InnerText;
                    PolDbPort = cfgChildNode.Attributes["port"]?.InnerText;
                    PolDbName = cfgChildNode.Attributes["database"]?.InnerText;
                    PolDbUsername = cfgChildNode.Attributes["username"]?.InnerText;
                    PolDbPassword = cfgChildNode.Attributes["password"]?.InnerText;
                }
                if (cfgChildNode.Name.Equals("federation"))
                {
                    FederationServerId = cfgChildNode.Attributes["serverId"]?.InnerText;
                    FederationKeyId = cfgChildNode.Attributes["kid"]?.InnerText;
                    FederationSigningKey = cfgChildNode.Attributes["signingKey"]?.InnerText;
                    FederationPublicIp = cfgChildNode.Attributes["publicIp"]?.InnerText;
                }
                if (cfgChildNode.Name.Equals("registry"))
                {
                    RegistryUrl = cfgChildNode.Attributes["url"]?.InnerText;
                    RegistryTrust = cfgChildNode.Attributes["trust"]?.InnerText;
                    RegistryPin = cfgChildNode.Attributes["pin"]?.InnerText;
                }
                if (cfgChildNode.Name.Equals("worlds"))
                {
                    foreach (XmlNode worldNode in cfgChildNode.ChildNodes)
                    {
                        if (worldNode.Name.Equals("world"))
                        {
                            string Attr(string attr) => worldNode.Attributes[attr]?.InnerText;
                            uint Ip(string attr) => Attr(attr) is { } ip ? BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes()) : 0;
                            uint Port(string attr) => Attr(attr) is { } port ? uint.Parse(port) : 0;

                            ushort num = ushort.Parse(Attr("id"));
                            string name = Attr("name");

                            // A federated world: trust="<server id>" keyset="<its key set URL>" [pin="sha256:..."]
                            // [gateway="<URL overriding the key set's>"]; the name comes from the key set unless given.
                            // dbHost & co. may stay for --federate-accounts.
                            if (Attr("trust") is { Length: > 0 } trust)
                            {
                                WorldContainer federated = new(new World() { Num = num, Name = name ?? trust[4..12] },
                                    Attr("dbHost"), Attr("dbPort"), Attr("dbName"), Attr("dbUser"), Attr("dbPass"),
                                    0, 0, Ip("cacheIp"), Port("cachePort"))
                                {
                                    Trust = trust,
                                    KeySetUrl = Attr("keyset") ?? "",
                                    Pin = Attr("pin"),
                                    GatewayOverride = Attr("gateway"),
                                    NameFromKeySet = name == null,
                                };
                                tempWorldList.Add(federated);
                                continue;
                            }

                            tempWorldList.Add(new(
                                new World() { Num = num, Name = name },
                                Attr("dbHost"),
                                Attr("dbPort"),
                                Attr("dbName"),
                                Attr("dbUser"),
                                Attr("dbPass"),
                                Ip("ip"),
                                Port("port"),
                                Ip("cacheIp"),
                                Port("cachePort")
                            )
                            {
                                SettingsDir = Attr("settingsDir") ?? "",
                            });
                        }
                    }
                    WorldList = tempWorldList;
                }
            }
            Console.ForegroundColor = ConsoleColor.Gray;
        }
    }

}