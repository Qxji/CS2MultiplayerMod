# CS2 Multiplayer Mod

## Introduction

Feel free to join the development Discord server [here](https://discord.gg/KFZTW2YSJt).

This is a **multiplayer mod** for Cities: Skylines II. Join your friends and build a city together!

The mod is **experimental**. Back up your saves before hosting or joining, and expect bugs while development continues. 

## Requirements

- Cities: Skylines II (PC Version: Steam, XBox, Gamepass)
- **All players must run the same version of the mod.** Players will not be able to connect with mismatched versions. 
- Players should also have **matching gameplay DLC**. Radio Station DLC are unaffected. Learn how to disable DLC: [Disabling DLC](docs/disable_dlc.md)
- Most mods do not work currently ([List of working mods](docs/mods.md)).
- (Internet play only: Set up TCP Port Forwarding)

## Installation

The easiest way is through **Paradox Mods**: find the mod, add it to an empty playset, enable it, and restart the game if Cities: Skylines II asks you to.

[**PDXMods**](https://mods.paradoxplaza.com/mods/150432/Windows)

## Hosting a game

1. Create a new city or open an existing one. If you use an existing save, **make a backup first**.
2. Open the in-game Multiplayer panel (or the mod settings).
3. Set your player name.
4. In the Host tab, choose the port, password, max players, LAN-only mode, and world re-sync interval.
5. Click **Host Session**.

### Playing through the Internet
For playing with friends that are not connected to your local network, you need to setup TCP port forwarding. Only do this with people you **absolutely trust**.

- [Written Tutorial](docs/forwarding.md) (recommended)
- [Video Tutorial](https://www.youtube.com/watch?v=WOZQppVNGvA)

---

1. Set up port forwarding (Default port: `25001`) like described in the tutorial above.
2. Find out your IP Address ([Link](https://api.ipify.org/))
3. Share the IP Address and the port you forwarded with your friends.


**Issues? Check [Troubleshooting](docs/forwarding_troubleshoot.md).**

## Joining a game

1. Click **Join Game** from the main menu, or open the Join tab in the mod settings.
2. Enter the host address, port, your player name, and the password.
3. Click **Join Session**.
4. Wait while the host's city downloads and loads — larger cities take longer. The dialog closes itself once you're in.

## Troubleshooting

- **City looks out of sync?** Run `/sync` in chat, or click **Sync World Now** in the mod settings. Clients pull a fresh save from the host; the host refreshes every connected player.
- **Can't join (protocol mismatch)?** You and the host are on different mod versions. Update to the same build.

Check out **[Troubleshooting](docs/troubleshooting.md)** for more issues.

## Technical Details

- The host is authoritative, and clients download the host's world when they join.
- The mod checks for matching DLC and version numbers before letting users connect. Find a list of white-listed DLC [here](CS2MultiplayerMod/Game/DlcCheck.cs#L21-L32). 

## Contributing & License

This mod and its source code are licensed under the [CS2 Multiplayer Mod Non-Commercial License](LICENSE). The license allows personal use, modification, and contributions to this project, but it does not allow commercial use, paid redistribution, or publishing clones/rebranded forks as someone else's project.

Contributions are welcome as long as they follow this repository's license. Keep attribution intact, submit changes through this project, and do not publish paid, monetized, or rebranded copies of the mod.

This project is not affiliated with, endorsed by, or sponsored by Colossal Order, Paradox Interactive or Iceflake Studios.
