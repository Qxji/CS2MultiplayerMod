import { trigger } from "cs2/api";
import { ModRegistrar } from "cs2/modding";
import { extendCreditsScreen, MultiplayerMenuButton } from "mods/join-game";
import { MultiplayerRightMenuButton } from "mods/mp-hub";
import { JoinLoadingScreen } from "mods/loading-screen";

// Vanilla-internal module hosting the main-menu button column. Game updates can
// rename it, so registration falls back to the official "Menu" append hook.
const MAIN_MENU_MODULE = "game-ui/menu/components/main-menu-screen/main-menu-screen.tsx";
const CREDITS_SCREEN_MODULE = "game-ui/menu/components/credits-screen/credits-screen.tsx";

const register: ModRegistrar = (moduleRegistry) => {
    // Tell the C# side this module survived the game's UI-module load chain.
    // MultiplayerUISystem warns in the log if this never arrives (e.g. another
    // mod's broken .mjs crashed the chain before reaching us — see Gooee).
    try {
        trigger("cs2mp", "uiReady");
    } catch {
        // Binding not reachable; the C# watchdog will report the module missing.
    }

    // In-game multiplayer hub: the right-menu column renders the official
    // "GameBottomRight" modding hook directly above the notification/Chirper
    // buttons, so this lands exactly on top of the bird icon in vanilla style.
    // The full-screen join loading overlay also mounts here so it can appear
    // when a join is started in-game (Options > Join). It renders nothing inline
    // (just a Portal), so it adds no visible right-menu item.
    try {
        moduleRegistry.append("GameBottomRight", MultiplayerRightMenuButton);
        moduleRegistry.append("GameBottomRight", JoinLoadingScreen);
    } catch (e) {
        console.warn("[cs2mp] GameBottomRight append failed; in-game hub button unavailable.", e);
    }

    // The Credits entry is a native menu-screen slot that is otherwise inactive
    // when Multiplayer is opened. Replacing its component conditionally puts our
    // screen inside the exact same focus and transition stack as New/Load Game.
    let multiplayerScreenRegistered = false;
    try {
        if (moduleRegistry.registry.has(CREDITS_SCREEN_MODULE)) {
            moduleRegistry.extend(CREDITS_SCREEN_MODULE, "CreditsScreen", extendCreditsScreen);
            multiplayerScreenRegistered = true;
        }
    } catch (e) {
        console.warn("[cs2mp] native Credits screen extension failed.", e);
    }
    if (!multiplayerScreenRegistered) {
        console.warn("[cs2mp] Multiplayer screen unavailable: native Credits module was not found.");
    }

    // Insert a "Multiplayer" button into the vanilla main-menu button column,
    // after Continue / New Game / Load Game (index 3). The loading overlay is
    // appended here too (Portal-only, renders nothing in the column) so it covers
    // the connect + world-download phase that runs while still in the main menu.
    try {
        if (moduleRegistry.registry.has(MAIN_MENU_MODULE)) {
            if (multiplayerScreenRegistered) {
                moduleRegistry.append(MAIN_MENU_MODULE, "MainMenuNavigation", MultiplayerMenuButton, 3);
            }
            moduleRegistry.append(MAIN_MENU_MODULE, "MainMenuNavigation", JoinLoadingScreen);
            return;
        }
        console.warn("[cs2mp] " + MAIN_MENU_MODULE + " not in module registry; using generic Menu hook.");
    } catch (e) {
        console.warn("[cs2mp] main-menu append failed; using generic Menu hook.", e);
    }
    if (multiplayerScreenRegistered) {
        moduleRegistry.append("Menu", MultiplayerMenuButton);
    }
    moduleRegistry.append("Menu", JoinLoadingScreen);
};

export default register;
