Project Crystal: FFXI Lobby Server for LandSandBoat
========
Welcome to the Project Crystal Server; a server emulator for PlayOnline and some of it's dead games.

This is part of [Project Crystal](https://github.com/filipmaj1/Project-Crystal).

This project is for a FFXI lobby server that interfaces with Project Crystal and [LandSandBoat](https://github.com/LandSandBoat/server) - a FFXI Emulator.

Federated worlds
========
A world can be reached only through its federation gateway, without the lobby having access to its database. The
lobby signs short-lived, single-use tokens ([xitoken](https://github.com/rubymatrix/xitoken), vendored in
`External/XiToken`) for every call: listing, creating, renaming and deleting characters, and entering the world. The
world checks each token, applies its own rules, and names the map server for the character's zone. In PhoenixPS2 the
gateway runs in `xi_world` (`/xi/v1/...`).

In `lobby.cfg`, give the lobby its identity, then name each federated world by its server id:

```xml
<lobbycfg serverIp="..." checkClientIp="true" singleUseContentAuth="true">
  <federation serverId="xi1.<this lobby's server id>" kid="2026-10" signingKey="C:\keys\crystal-signing-2026-10.key"/>
  <!-- optional: also offer every world a trusted registry lists -->
  <registry url="https://federation.example/registry" trust="xi1.<registry id>" pin="sha256:..."/>
  <worlds>
    <world id="101" trust="xi1.<the world's server id>" keyset="https://ps2.example.net:8088/xi/v1/keyset"
           pin="sha256:<the pin xi_world logs>"/>
  </worlds>
</lobbycfg>
```

* The world's name, gateway, expansions and search server come from its signed key set. `name`, `gateway`,
  `cacheIp` and `cachePort` on the `<world>` element override them.
* `pin` trusts a self-signed certificate by its fingerprint. Without it, the certificate must validate normally.
* Worlds a registry adds get lobby world numbers that are kept in `federation-worlds.json`, next to `lobby.cfg`.
* A `<world>` without `trust` is a classic world: the lobby reads and writes its database (`dbHost` & co.).

Make the keys with `xitoken-cli` from the xitoken repo:

1. `keygen --out crystal-identity.key` creates the identity key and prints the server id. Keep this key offline.
2. `keygen --kid 2026-10 --out crystal-signing-2026-10.key` creates the signing key.
3. `keyset --identity crystal-identity.key --name "Crystal" 2026-10=crystal-signing-2026-10.key > crystal.keyset`
   writes the key set.

A world trusts the lobby by keeping that key set as `<lobby server id>.keyset` in its trust folder (PhoenixPS2:
`settings/federation`).

To move a world with existing characters onto federation, keep its `dbHost` & co. on the `<world>` element next to
`trust`, and run the lobby once with `--federate-accounts`. It maps each PlayOnline member to the world account that
owns their characters, then exits. After that the database settings are no longer used.

Login hardening (both on by default):

* `singleUseContentAuth`: the contents-auth hash from the profile server opens one lobby login only.
* `checkClientIp`: the lobby login must come from the address the member's PlayOnline session was opened from.

Set either to `false` on `<lobbycfg>` if your network setup needs it.

Pull Requests
========
Commits should contain a descriptive name for what you are modifying

Remember to check back for any feedback, and drop a comment once requested changes have been made (if there are any).

Please *test your code* before committing changes/submitting a pull request.

