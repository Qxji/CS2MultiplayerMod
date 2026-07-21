import { bindValue, trigger, useValue } from "cs2/api";
import { InputActionBarrier } from "cs2/input";
import { useLocalization } from "cs2/l10n";
import { Button, Portal } from "cs2/ui";
import { CSSProperties, useEffect, useState } from "react";
import { currentMenuBackdropImage } from "mods/join-game";

// Binding group shared with MultiplayerUISystem on the C# side.
const GROUP = "cs2mp";

const LOC = {
    joiningTitle: "CS2MP.UI.JoiningTitle",
    multiplayer: "CS2MP.UI.Multiplayer",
    worldTransfer: "CS2MP.UI.WorldTransfer",
    loadingHint: "CS2MP.UI.LoadingHint",
    connectionFailed: "CS2MP.Status.ConnectionFailed",
    cancel: "CS2MP.UI.Cancel",
    close: "CS2MP.UI.Close",
};

const useT = () => {
    const { translate } = useLocalization();
    return (id: string, fallback: string) => translate(id, fallback) ?? fallback;
};

const statusKind$ = bindValue<string>(GROUP, "statusKind", "offline");
const statusTitle$ = bindValue<string>(GROUP, "statusTitle", "");
const statusDetail$ = bindValue<string>(GROUP, "statusDetail", "");
const mapTransferPercent$ = bindValue<number>(GROUP, "mapTransferPercent", -1);
const worldSendPercent$ = bindValue<number>(GROUP, "worldSendPercent", -1);
const isHost$ = bindValue<boolean>(GROUP, "isHost", false);

// rem behaves like resolution-independent pixels (the game scales root font size).
const styles: Record<string, CSSProperties> = {
    overlay: {
        position: "fixed",
        top: 0,
        left: 0,
        right: 0,
        bottom: 0,
        // Above every mod panel and dialog so it reads as a real loading screen.
        zIndex: 99999,
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        justifyContent: "center",
        // Opaque so the main menu / game behind it is fully hidden (also the
        // fallback if the backdrop image cannot be resolved).
        backgroundColor: "rgb(11, 16, 27)",
        pointerEvents: "auto",
    },
    // Menu artwork behind the loading content, same image the join screen shows.
    // zIndex -1 keeps it above the overlay background but below the in-flow text
    // (the same layering the vanilla menu backdrop uses).
    backdrop: {
        position: "absolute",
        top: 0,
        left: 0,
        right: 0,
        bottom: 0,
        zIndex: -1,
        backgroundPosition: "center",
        backgroundSize: "cover",
        backgroundRepeat: "no-repeat",
    },
    backdropDim: {
        position: "absolute",
        top: 0,
        left: 0,
        right: 0,
        bottom: 0,
        zIndex: -1,
        backgroundColor: "rgba(11, 16, 27, 0.60)",
    },
    title: {
        fontSize: "44rem",
        fontWeight: "bold",
        letterSpacing: "2rem",
        textTransform: "uppercase",
        color: "#ffffff",
        marginBottom: "40rem",
        textShadow: "0 2rem 16rem rgba(114, 200, 240, 0.35)",
    },
    barOuter: {
        width: "640rem",
        maxWidth: "85%",
    },
    barHeader: {
        display: "flex",
        justifyContent: "space-between",
        alignItems: "baseline",
        marginBottom: "8rem",
    },
    phase: {
        fontSize: "17rem",
        textTransform: "uppercase",
        letterSpacing: "1rem",
        color: "#9dc1de",
    },
    percent: {
        fontSize: "17rem",
        color: "#72c8f0",
        fontWeight: "bold",
    },
    track: {
        position: "relative",
        height: "12rem",
        backgroundColor: "rgba(0, 0, 0, 0.5)",
        border: "1rem solid rgba(157, 193, 222, 0.30)",
        borderRadius: "3rem",
        overflow: "hidden",
    },
    fill: {
        height: "100%",
        backgroundColor: "#72c8f0",
        boxShadow: "0 0 14rem rgba(114, 200, 240, 0.6)",
        transition: "width 180ms linear",
    },
    // Indeterminate sweep: a highlight slides across the empty track.
    sweep: {
        position: "absolute",
        top: 0,
        bottom: 0,
        width: "30%",
        background:
            "linear-gradient(90deg, rgba(114,200,240,0) 0%, rgba(114,200,240,0.85) 50%, rgba(114,200,240,0) 100%)",
    },
    detail: {
        marginTop: "14rem",
        fontSize: "14rem",
        color: "rgba(255, 255, 255, 0.6)",
        minHeight: "18rem",
        textAlign: "center",
    },
    hint: {
        marginTop: "6rem",
        fontSize: "13rem",
        color: "rgba(255, 255, 255, 0.4)",
        textAlign: "center",
    },
    error: {
        fontSize: "16rem",
        color: "#ff8a7a",
        marginBottom: "28rem",
        maxWidth: "640rem",
        textAlign: "center",
        lineHeight: "1.45",
    },
    cancel: {
        marginTop: "40rem",
        padding: "9rem 28rem",
    },
};

// Animated indeterminate bar (connecting / loading, before a byte count exists).
// The game's UI runtime has no inline @keyframes, so the sweep is positioned from
// requestAnimationFrame, like the join dialog's spinner.
const IndeterminateBar = () => {
    const [pos, setPos] = useState(-30);

    useEffect(() => {
        let raf = 0;
        const tick = (time: number) => {
            // 0..130 then wrap, so the 30%-wide sweep travels fully off both ends.
            setPos(((time * 0.06) % 160) - 30);
            raf = requestAnimationFrame(tick);
        };
        raf = requestAnimationFrame(tick);
        return () => cancelAnimationFrame(raf);
    }, []);

    return (
        <div style={styles.track}>
            <div style={{ ...styles.sweep, left: `${pos}%` }} />
        </div>
    );
};

// Full-screen, branded loading screen shown to a joining client from the moment
// "Join" is pressed until the host's world is live (or the attempt fails). It
// covers connecting + the world download — the long part — after which the game's
// own native loading screen takes over for the final map load.
export const JoinLoadingScreen = () => {
    const t = useT();
    const statusKind = useValue(statusKind$);
    const statusTitle = useValue(statusTitle$);
    const statusDetail = useValue(statusDetail$);
    const mapTransferPercent = useValue(mapTransferPercent$);
    const worldSendPercent = useValue(worldSendPercent$);
    const isHost = useValue(isHost$);
    const percent = isHost ? worldSendPercent : mapTransferPercent;

    // Shown from the first "connecting" until connected/offline. An error keeps it
    // up (so the failure is visible) until the player dismisses it.
    const [active, setActive] = useState(false);
    // Backdrop snapshot taken when the screen activates, while the menu's
    // backdrop element is still there to read; static for the whole attempt.
    const [backdropImage, setBackdropImage] = useState<string | null>(null);
    useEffect(() => {
        if (statusKind === "syncing") {
            setActive(true);
            setBackdropImage((current) => current ?? currentMenuBackdropImage());
        } else if (isHost) {
            setActive(false);
            setBackdropImage(null);
        } else if (statusKind === "connecting") {
            setActive(true);
            setBackdropImage((current) => current ?? currentMenuBackdropImage());
        } else if (statusKind === "connected" || statusKind === "offline" || statusKind === "disabled") {
            setActive(false);
            setBackdropImage(null);
        }
        // "error": leave active unchanged so the error state stays on screen.
    }, [statusKind, isHost]);

    if (!active) return null;

    const failed = statusKind === "error";
    const synchronizing = statusKind === "syncing";
    const dismiss = () => {
        setActive(false);
        // Clear the faulted session so the next attempt starts clean.
        trigger(GROUP, "disconnect");
    };

    const phaseTitle = statusTitle || t(LOC.joiningTitle, "Joining Multiplayer Game");
    const clamped = Math.max(0, Math.min(100, Math.floor(percent)));

    return (
        <Portal>
            <InputActionBarrier>
                <div style={styles.overlay}>
                    {backdropImage ? (
                        <>
                            <div style={{ ...styles.backdrop, backgroundImage: backdropImage }} />
                            <div style={styles.backdropDim} />
                        </>
                    ) : null}
                    <div style={styles.title}>{t(LOC.multiplayer, "Multiplayer")}</div>

                    {failed ? (
                        <>
                            <div style={styles.error}>
                                {statusTitle || t(LOC.connectionFailed, "Connection failed")}
                                {statusDetail ? " - " + statusDetail : ""}
                            </div>
                            <Button variant="primary" style={styles.cancel} onSelect={dismiss}>
                                {t(LOC.close, "Close")}
                            </Button>
                        </>
                    ) : (
                        <>
                            <div style={styles.barOuter}>
                                <div style={styles.barHeader}>
                                    <span style={styles.phase}>{phaseTitle}</span>
                                    {percent >= 0 ? <span style={styles.percent}>{clamped}%</span> : null}
                                </div>
                                {percent >= 0 ? (
                                    <div style={styles.track}>
                                        <div style={{ ...styles.fill, width: `${clamped}%` }} />
                                    </div>
                                ) : (
                                    <IndeterminateBar />
                                )}
                                <div style={styles.detail}>
                                    {percent >= 0 ? t(LOC.worldTransfer, "World Transfer") : statusDetail}
                                </div>
                                <div style={styles.hint}>
                                    {t(LOC.loadingHint, "Keep this window open while the host's city is transferred.")}
                                </div>
                            </div>
                            {!synchronizing || !isHost ? (
                                <Button variant="flat" style={styles.cancel} onSelect={dismiss}>
                                    {t(LOC.cancel, "Cancel")}
                                </Button>
                            ) : null}
                        </>
                    )}
                </div>
            </InputActionBarrier>
        </Portal>
    );
};
