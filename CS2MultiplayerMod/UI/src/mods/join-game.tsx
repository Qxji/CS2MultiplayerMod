import { bindValue, trigger, useValue } from "cs2/api";
import { AutoNavigationScope, BackConsumer, InputActionBarrier, NavigationDirection } from "cs2/input";
import { useLocalization } from "cs2/l10n";
import { getModule } from "cs2/modding";
import { Button, DialogContext, DialogStack, MenuButton, Portal } from "cs2/ui";
import { CSSProperties, useContext, useEffect, useRef, useState } from "react";
import { DisclaimerModal, disclaimerAccepted$ } from "mods/disclaimer";
import { VersionWarningBanner } from "mods/version-banner";

// Binding group shared with MultiplayerUISystem on the C# side. The field values
// live in the mod's Setting object, so this dialog and the options screen share
// the same player/join data.
const GROUP = "cs2mp";

// Locale keys served by the mod's LocaleEN/LocaleDE dictionary sources (constants
// in L10n.Key on the C# side). The game resolves them against its active language;
// the inline fallbacks only cover a dictionary that has not loaded yet.
const LOC = {
    joinGame: "CS2MP.UI.JoinGame",
    dialogTitle: "CS2MP.UI.DialogTitle",
    playerName: "CS2MP.UI.PlayerName",
    hostAddress: "CS2MP.UI.HostAddress",
    port: "CS2MP.UI.Port",
    password: "CS2MP.UI.Password",
    worldTransfer: "CS2MP.UI.WorldTransfer",
    join: "CS2MP.UI.Join",
    disconnect: "CS2MP.UI.Disconnect",
};

// translate() is typed string | null; this narrows it to the English fallback so
// JSX/props that require a string stay clean.
const useT = () => {
    const { translate } = useLocalization();
    return (id: string, fallback: string) => translate(id, fallback) ?? fallback;
};

const playerName$ = bindValue<string>(GROUP, "playerName", "Player");
const address$ = bindValue<string>(GROUP, "joinAddress", "127.0.0.1");
const port$ = bindValue<string>(GROUP, "joinPort", "25001");
const password$ = bindValue<string>(GROUP, "joinPassword", "");
const statusKind$ = bindValue<string>(GROUP, "statusKind", "offline");
const mapTransferPercent$ = bindValue<number>(GROUP, "mapTransferPercent", -1);
const inSession$ = bindValue<boolean>(GROUP, "inSession", false);

// ---- Vanilla menu-screen chrome ------------------------------------------------
// The Load Game / New Game screens are built from shared modules in the game's UI
// module registry: a centered 1760x980rem content container ("menu-ui") holding a
// sub-screen (back arrow + large title + content). Reusing them keeps this screen's
// sizing and layout identical to those screens. The paths are vanilla-internal and
// can move on a game update, hence the inline fallbacks that replicate the same
// geometry.
const tryModule = (path: string, exportName: string): any => {
    try {
        return getModule(path, exportName);
    } catch {
        return null;
    }
};
const VanillaSubScreen = tryModule("game-ui/menu/components/shared/sub-screen/sub-screen.tsx", "SubScreen");
const menuClasses: Record<string, string> | null =
    tryModule("game-ui/menu/components/menu-ui.module.scss", "classes");
const backdropClasses: Record<string, string> | null =
    tryModule("game-ui/menu/components/menu-ui-backdrops/menu-ui-backdrops.module.scss", "classes");

// Same backdrop pool the vanilla menu rotates through ("Backgound" spelling is
// the game's own file naming). Only used if the live backdrop cannot be read.
const FALLBACK_BACKDROPS = [1, 2, 3, 4, 5, 6, 7].map((n) => `Media/Menu/Backdrops/Backgound0${n}.png`);

// The menu's backdrop layer keeps running behind this screen. Reusing the image
// it is showing right now means opening the screen changes nothing visually
// behind the content — exactly how the vanilla sub-screens behave. Resolved at
// open time (not module load) so the menu has rendered its backdrop already.
export const currentMenuBackdropImage = (): string => {
    try {
        if (backdropClasses && backdropClasses.backdropImage) {
            const els = document.getElementsByClassName(backdropClasses.backdropImage.split(" ")[0]);
            if (els.length > 0) {
                // Newest element wins if a vanilla cross-fade is mid-flight.
                const bg = (els[els.length - 1] as HTMLElement).style.backgroundImage;
                if (bg) return bg;
            }
        }
    } catch {
        // Fall through to the static pool.
    }
    const list: string[] =
        tryModule("game-ui/menu/components/menu-ui-backdrops/menu-ui-backdrops.tsx", "BACKDROPS_LIST") ||
        FALLBACK_BACKDROPS;
    return `url('${list[Math.floor(Math.random() * list.length)]}')`;
};

// The game scales its UI by adjusting the root font size, so rem behaves like
// resolution-independent pixels; all sizes below follow that convention.
const styles: Record<string, CSSProperties> = {
    // Full-screen wrapper. The vanilla menu swaps its screens in place (the button
    // column unmounts while a sub-screen shows); this overlay achieves the same
    // look by covering the main menu with the vanilla backdrop artwork.
    screen: {
        position: "fixed",
        top: 0,
        left: 0,
        right: 0,
        bottom: 0,
        zIndex: 10000,
        backgroundColor: "rgb(11, 16, 27)",
        pointerEvents: "auto",
    },
    backdrop: {
        position: "absolute",
        top: 0,
        left: 0,
        right: 0,
        bottom: 0,
        backgroundPosition: "center",
        backgroundSize: "cover",
        backgroundRepeat: "no-repeat",
    },
    // Fallback geometry matching the vanilla menu screen container (used only if
    // the vanilla classes are unavailable after a game update).
    menuUiFallback: {
        position: "absolute",
        top: 0,
        left: 0,
        right: 0,
        bottom: 0,
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        pointerEvents: "auto",
    },
    containerFallback: {
        width: "1760rem",
        height: "980rem",
        display: "flex",
        flexDirection: "column",
        alignItems: "stretch",
    },
    contentFallback: {
        flexGrow: 1,
        flexShrink: 1,
        flexBasis: "0%",
        position: "relative",
    },
    // Fallback sub-screen chrome (back arrow + large title), same metrics as the
    // vanilla sub-screen header.
    fallbackScreenRoot: {
        position: "absolute",
        top: 0,
        left: 0,
        right: 0,
        bottom: 0,
        display: "flex",
        flexDirection: "column",
        alignItems: "stretch",
    },
    fallbackHeader: {
        display: "flex",
        flexDirection: "row",
        alignItems: "center",
        marginBottom: "8rem",
    },
    fallbackBackButton: {
        width: "40rem",
        height: "40rem",
        marginRight: "12rem",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
    },
    fallbackBackIcon: {
        width: "24rem",
        height: "24rem",
    },
    fallbackTitle: {
        fontSize: "40rem",
        lineHeight: "1.2",
        fontWeight: "bold",
        color: "var(--menuTitleNormal, #ffffff)",
        textTransform: "uppercase",
    },
    fallbackContent: {
        flexGrow: 1,
        flexShrink: 1,
        flexBasis: "0%",
        minHeight: 0,
        position: "relative",
    },
    // The form panel inside the screen's content area.
    contentArea: {
        height: "100%",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
    },
    panel: {
        width: "760rem",
        maxWidth: "85%",
        backgroundColor: "rgba(24, 33, 51, 0.90)",
        borderRadius: "4rem",
        padding: "32rem",
        boxShadow: "0 16rem 48rem rgba(0, 0, 0, 0.45)",
        pointerEvents: "auto",
    },
    row: {
        display: "flex",
        alignItems: "center",
        marginBottom: "16rem",
    },
    label: {
        width: "200rem",
        fontSize: "17rem",
        color: "#9dc1de",
        textTransform: "uppercase",
    },
    input: {
        flex: 1,
        fontSize: "18rem",
        color: "#ffffff",
        backgroundColor: "rgba(0, 0, 0, 0.35)",
        border: "1rem solid rgba(157, 193, 222, 0.35)",
        borderRadius: "3rem",
        padding: "9rem 12rem",
    },
    inputDisabled: {
        opacity: 0.55,
        cursor: "not-allowed",
    },
    progress: {
        margin: "0 0 16rem 0",
    },
    progressHeader: {
        display: "flex",
        justifyContent: "space-between",
        alignItems: "center",
        fontSize: "14rem",
        color: "rgba(157, 193, 222, 0.9)",
        textTransform: "uppercase",
        marginBottom: "5rem",
    },
    progressTrack: {
        position: "relative",
        height: "9rem",
        backgroundColor: "rgba(0, 0, 0, 0.4)",
        border: "1rem solid rgba(157, 193, 222, 0.25)",
        borderRadius: "2rem",
        overflow: "hidden",
    },
    progressFill: {
        height: "100%",
        backgroundColor: "#72c8f0",
        boxShadow: "0 0 8rem rgba(114, 200, 240, 0.45)",
        transition: "width 160ms linear",
    },
    progressSweep: {
        position: "absolute",
        top: 0,
        bottom: 0,
        width: "30%",
        background:
            "linear-gradient(90deg, rgba(114,200,240,0) 0%, rgba(114,200,240,0.9) 50%, rgba(114,200,240,0) 100%)",
    },
    buttons: {
        display: "flex",
        justifyContent: "flex-end",
        marginTop: "24rem",
    },
    button: {
        marginLeft: "12rem",
        padding: "10rem 28rem",
        fontSize: "17rem",
    },
};

interface FieldProps {
    label: string;
    value: string;
    secret?: boolean;
    disabled?: boolean;
    onChange: (value: string) => void;
}

const Field = ({ label, value, secret, disabled, onChange }: FieldProps) => {
    const [draft, setDraft] = useState(value);
    const [editing, setEditing] = useState(false);

    useEffect(() => {
        if (!editing) setDraft(value);
    }, [value]);

    const updateValue = (next: string) => {
        setDraft(next);
        onChange(next);
    };

    return (
        <div style={styles.row}>
            <div style={styles.label}>{label}</div>
            <input
                type={secret ? "password" : "text"}
                style={disabled ? { ...styles.input, ...styles.inputDisabled } : styles.input}
                value={draft}
                disabled={disabled}
                spellCheck={false}
                autoComplete="off"
                onFocus={() => setEditing(true)}
                onBlur={() => {
                    setEditing(false);
                    if (draft !== value) onChange(draft);
                }}
                onMouseDown={(e) => e.stopPropagation()}
                onKeyDown={(e) => e.stopPropagation()}
                onChange={(e) => updateValue((e.target as HTMLInputElement).value)}
            />
        </div>
    );
};

const ProgressSweep = () => {
    const [position, setPosition] = useState(-30);

    useEffect(() => {
        let raf = 0;
        const tick = (time: number) => {
            setPosition(((time * 0.06) % 160) - 30);
            raf = requestAnimationFrame(tick);
        };
        raf = requestAnimationFrame(tick);
        return () => cancelAnimationFrame(raf);
    }, []);

    return <div style={{ ...styles.progressSweep, left: `${position}%` }} />;
};

export const TransferProgress = ({
    percent,
    label,
    indeterminate = false,
}: {
    percent: number;
    label?: string;
    indeterminate?: boolean;
}) => {
    // Hook must run unconditionally (before the early return) to keep hook order stable.
    const t = useT();
    if (percent < 0 && !indeterminate) return null;

    const clamped = Math.max(0, Math.min(100, Math.floor(percent)));
    return (
        <div style={styles.progress}>
            <div style={styles.progressHeader}>
                <span>{label ?? t(LOC.worldTransfer, "World Transfer")}</span>
                {!indeterminate ? <span>{clamped}%</span> : null}
            </div>
            <div style={styles.progressTrack}>
                {indeterminate
                    ? <ProgressSweep />
                    : <div style={{ ...styles.progressFill, width: `${clamped}%` }} />}
            </div>
        </div>
    );
};

export const JoinGameDialog = () => {
    const { onClose } = useContext(DialogContext);
    const t = useT();
    const playerName = useValue(playerName$);
    const address = useValue(address$);
    const port = useValue(port$);
    const password = useValue(password$);
    const statusKind = useValue(statusKind$);
    const mapTransferPercent = useValue(mapTransferPercent$);
    const inSession = useValue(inSession$);
    const accepted = useValue(disclaimerAccepted$);

    // Snapshot of the backdrop the menu is showing at open time (lazy initializer
    // runs once). Kept static while open, so nothing swaps behind the form.
    const [backdropImage] = useState(currentMenuBackdropImage);

    // Auto-close once a join we started here actually completes (the world has loaded
    // and gameplay is live → statusKind flips "connecting" → "connected"). Guarded by
    // a "did we go through connecting?" flag so opening the dialog while already in a
    // session (to disconnect) does not instantly close it.
    const sawConnecting = useRef(false);
    useEffect(() => {
        if (statusKind === "connecting") {
            sawConnecting.current = true;
        } else if (statusKind === "connected" && sawConnecting.current) {
            sawConnecting.current = false;
            onClose();
        }
    }, [statusKind, onClose]);

    // First use: show the one-time disclaimer instead of the form. Accepting flips
    // the binding and this re-renders into the real dialog; Cancel closes it.
    if (!accepted) {
        return <DisclaimerModal onAccept={() => {}} onDecline={onClose} />;
    }

    const title = t(LOC.dialogTitle, "Join Multiplayer Game");

    const form = (
        <div style={styles.contentArea}>
            <AutoNavigationScope
                focusKey="cs2mp-join-dialog"
                debugName="CS2MP Join Game Screen"
                direction={NavigationDirection.Both}
                initialFocused={inSession ? "disconnect" : "join"}
                allowLooping>
                <div style={styles.panel} onMouseDown={(e) => e.stopPropagation()}>
                    <VersionWarningBanner />
                    <Field
                        label={t(LOC.playerName, "Player Name")}
                        value={playerName}
                        onChange={(v) => trigger(GROUP, "setPlayerName", v)}
                    />
                    <Field
                        label={t(LOC.hostAddress, "Host Address")}
                        value={address}
                        onChange={(v) => trigger(GROUP, "setJoinAddress", v)}
                    />
                    <Field
                        label={t(LOC.port, "Port")}
                        value={port}
                        onChange={(v) => trigger(GROUP, "setJoinPort", v)}
                    />
                    <Field
                        label={t(LOC.password, "Password")}
                        secret
                        disabled={inSession}
                        value={password}
                        onChange={(v) => trigger(GROUP, "setJoinPassword", v)}
                    />
                    <TransferProgress percent={mapTransferPercent} />
                    <div style={styles.buttons}>
                        {inSession ? (
                            <Button
                                variant="primary"
                                style={styles.button}
                                focusKey="disconnect"
                                onSelect={() => trigger(GROUP, "disconnect")}>
                                {t(LOC.disconnect, "Disconnect")}
                            </Button>
                        ) : (
                            <Button
                                variant="primary"
                                style={styles.button}
                                focusKey="join"
                                onSelect={() => trigger(GROUP, "join")}>
                                {t(LOC.join, "Join")}
                            </Button>
                        )}
                    </div>
                </div>
            </AutoNavigationScope>
        </div>
    );

    return (
        <Portal>
            <InputActionBarrier>
                <div style={styles.screen}>
                    <div style={{ ...styles.backdrop, backgroundImage: backdropImage }} />
                    <div
                        className={menuClasses ? menuClasses.menuUi : undefined}
                        style={menuClasses ? undefined : styles.menuUiFallback}>
                        <div
                            className={menuClasses ? menuClasses.contentContainer : undefined}
                            style={menuClasses ? undefined : styles.containerFallback}>
                            <div
                                className={menuClasses ? menuClasses.content : undefined}
                                style={menuClasses ? undefined : styles.contentFallback}>
                                {VanillaSubScreen ? (
                                    // The vanilla sub-screen brings its own back button,
                                    // title bar and back-action handling.
                                    <VanillaSubScreen
                                        focusKey="cs2mp-join-screen"
                                        title={title}
                                        onClose={onClose}>
                                        {form}
                                    </VanillaSubScreen>
                                ) : (
                                    <BackConsumer onAction={onClose}>
                                        <div style={styles.fallbackScreenRoot}>
                                            <div style={styles.fallbackHeader}>
                                                <Button
                                                    variant="icon"
                                                    style={styles.fallbackBackButton}
                                                    onSelect={onClose}>
                                                    <img
                                                        src="Media/Glyphs/TriangleArrowLeft.svg"
                                                        style={styles.fallbackBackIcon}
                                                    />
                                                </Button>
                                                <div style={styles.fallbackTitle}>{title}</div>
                                            </div>
                                            <div style={styles.fallbackContent}>{form}</div>
                                        </div>
                                    </BackConsumer>
                                )}
                            </div>
                        </div>
                    </div>
                </div>
            </InputActionBarrier>
        </Portal>
    );
};

export const JoinGameMenuButton = () => {
    const { showDialog } = useContext(DialogStack);
    const t = useT();
    return (
        <MenuButton tinted src="Media/Glyphs/Passenger.svg"
                    onSelect={() => showDialog(<JoinGameDialog />)}>
            {t(LOC.joinGame, "Join Game")}
        </MenuButton>
    );
};
