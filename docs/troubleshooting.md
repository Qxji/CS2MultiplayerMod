Updated `2026-08-06` for version `v0.1.3`. [Current mod version](./CS2MultiplayerMod/Properties/PublishConfiguration.xml#L23).
# Troubleshooting Guide
Having Issues? This is a comprehensive troubleshooting guide for the CS2MultiplayerMod. This guide will try to solve common issues with port forwarding, connection issues, CGNAT and mismatching DLC. 

## Contents & Quicklinks

- [Connection Issues](#connection-issues)
- [Mod Version Issues](#mod-version-issues)
- [Game Version Issues](#game-version-issues)
- [DLC Mismatch Issues](#dlc-mismatch-issues)
- ["Join Game" does not show up in Menu](#menu-issues)
- [Troubleshooting by Error Message](#troubleshooting-by-error-message)

--- 

- [Disable DLC](./docs/disable_dlc.md)
- [Mod Support](./docs/mods.md)
- [Port Forwarding](./docs/forwarding.md)
- [Troubleshoot Port Forwarding](./docs/forwarding_troubleshoot.md)
- [Verify Game Files](./docs/verify_files.md)
- [What is my IP?](https://api.ipify.org/)

---

## Connection Issues

### Host

Make sure that you have set up port forwarding. [Learn how to set up port forwarding.](./docs/forwarding.md).

[Issues with Port Forwarding?](./docs/forwarding_troubleshoot.md)

If you still have issues, even though you have port forwarding enabled, check that your firewall/anti-virus allows opening ports on your local machine. 

If you still cannot connect, check that you are not under Carrier-Grade NAT (CGNAT). Open your router settings, check the displayed public IP address (WAN IP) and compare to the IP shown [here](https://api.ipify.org/). If they are different, you are likely behind CGNAT. You might have luck by letting someone else host.

### People connecting to the host (Clients)

Check that your connection is not blocked by antivirus or your local firewall. Check that you are connected to the Internet. 

## Mod Version Issues

Check that you have the same mod version as the people you are trying to play with. Update the mod via Paradox Mods (PDXMods) to the newest version.

Still having issues? Remove the mod on PDXMods. Restart the game. Reinstall the mod on PDXMods. Restart the game.

## Game Version Issues

Check that you have the same game version as the people you are trying to play with. You can find the game version in the bottom left of the Main Menu when you start the game. The beginning should look similar to this: `1.6.0f1`. If not, update your game through Steam or XBox/Gamepass.

Still having issues? [Verify Game Files](./docs/verify_files.md) (Steam: Right-click Game => Properties => Installed Files => Verify Integrity of game files; [XBox/Gamepass (click)](./docs/verify_files.md)).

## DLC Mismatch Issues

Check that you have the same DLC enabled as the people you are trying to play with. [Learn how to disable DLC.](./docs/disable_dlc.md)

Cannot disable CS1TreasureHunt DLC? This is a known issue. 

## Menu issues

Go to options. Check that CS2MultiplayerMod appears in settings. If not:

Remove the mod on PDXMods. Restart the game. Reinstall the mod on PDXMods. Restart the game.

Check that you do not have any [launch options](https://cs2.paradoxwikis.com/Launch_Parameters) preventing the mod from working. Check that the mod is in your active playset on PDXMods.

## Troubleshooting by Error message
*WIP*
