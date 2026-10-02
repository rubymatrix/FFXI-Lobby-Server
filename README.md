Project Crystal: FFXI Lobby Server for LandSandBoat
========
Welcome to the Project Crystal Server; a server emulator for PlayOnline and some of it's dead games.

This is part of [Project Crystal](https://github.com/filipmaj1/Project-Crystal).

This project is for a FFXI lobby server that interfaces with Project Crystal and [LandSandBoat](https://github.com/LandSandBoat/server) - a FFXI Emulator.

Federation gateway
========
A world can take characters through its federation gateway instead of having the lobby write `accounts_sessions`
into its database. The lobby signs a short-lived, single-use world-entry token
([xitoken](https://github.com/rubymatrix/xitoken), vendored in `External/XiToken`). The world checks it, writes the
session itself, and names the map server for the character's zone. In PhoenixPS2 the gateway is `xi_world`'s
`POST /xi/v1/world-entry`.

In `lobby.cfg`, give the lobby its identity and give the world its gateway:

```xml
<lobbycfg serverIp="...">
  <federation serverId="xi1.<this lobby's server id>" kid="2026-10" signingKey="C:\keys\crystal-signing-2026-10.key"/>
  <worlds>
    <world id="100" name="..." ... gateway="http://127.0.0.1:8088" worldId="xi1.<the world's server id>"/>
  </worlds>
</lobbycfg>
```

Make the keys with `xitoken-cli` from the xitoken repo:

1. `keygen --out crystal-identity.key` creates the identity key and prints the server id.
2. `keygen --kid 2026-10 --out crystal-signing-2026-10.key` creates a signing key.
3. `keyset --identity crystal-identity.key --name "Crystal" 2026-10=crystal-signing-2026-10.key` prints the key set.

The world trusts the lobby by keeping that key set as `<lobby server id>.keyset` in its trust folder
(PhoenixPS2: `settings/federation`).

Worlds without `gateway` and `worldId` keep the direct insert. While the lobby still creates characters in the
world database, it also records the player's account in the world's `accounts_federated` table, which the
gateway checks.

Pull Requests
========
Commits should contain a descriptive name for what you are modifying

Remember to check back for any feedback, and drop a comment once requested changes have been made (if there are any).

Please *test your code* before committing changes/submitting a pull request.

