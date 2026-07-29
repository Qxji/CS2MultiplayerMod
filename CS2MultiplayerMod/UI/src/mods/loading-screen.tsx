import { bindValue, trigger, useValue } from "cs2/api";
import { InputActionBarrier } from "cs2/input";
import { useLocalization } from "cs2/l10n";
import { Button, Portal } from "cs2/ui";
import { CSSProperties, useEffect, useState } from "react";
import { MULTIPLAYER_BLUE } from "mods/multiplayer-theme";

// Binding group shared with MultiplayerUISystem on the C# side.
const GROUP = "cs2mp";

const LOC = {
    joiningTitle: "CS2MP.UI.JoiningTitle",
    multiplayer: "CS2MP.UI.Multiplayer",
    worldTransfer: "CS2MP.UI.WorldTransfer",
    loadingHint: "CS2MP.UI.LoadingHint",
    hostLoadingHint: "CS2MP.UI.HostLoadingHint",
    connectionFailed: "CS2MP.Status.ConnectionFailed",
    tryThis: "CS2MP.UI.TryThis",
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
const statusHelp$ = bindValue<string>(GROUP, "statusHelp", "");
const progressMode$ = bindValue<string>(GROUP, "progressMode", "none");
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
        // Host sync and every client join phase use the same opaque blue, so no
        // stale menu artwork or underlying screen can bleed through.
        backgroundColor: MULTIPLAYER_BLUE,
        pointerEvents: "auto",
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
        width: "640rem",
        maxWidth: "85%",
        padding: "22rem 26rem",
        backgroundColor: "rgba(24, 33, 51, 0.92)",
        borderLeft: "4rem solid #ff8a7a",
        borderRadius: "4rem",
        marginBottom: "10rem",
        textAlign: "left",
    },
    errorTitle: {
        fontSize: "22rem",
        color: "#ff9c8f",
        fontWeight: "bold",
        marginBottom: "10rem",
    },
    errorSummary: {
        fontSize: "16rem",
        color: "#ffffff",
        lineHeight: "1.45",
    },
    helpTitle: {
        marginTop: "18rem",
        marginBottom: "5rem",
        color: "#9dc1de",
        fontSize: "14rem",
        fontWeight: "bold",
        textTransform: "uppercase",
    },
    errorHelp: {
        fontSize: "14rem",
        color: "rgba(255, 255, 255, 0.76)",
        maxWidth: "640rem",
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

// Blocking full-screen state shared by host world synchronization and every
// client join phase. A client sees it immediately after pressing Join, including
// the time spent waiting for manual host approval, through world transfer/load.
export const JoinLoadingScreen = () => {
    const t = useT();
    const statusKind = useValue(statusKind$);
    const statusTitle = useValue(statusTitle$);
    const statusDetail = useValue(statusDetail$);
    const statusHelp = useValue(statusHelp$);
    const progressMode = useValue(progressMode$);
    const mapTransferPercent = useValue(mapTransferPercent$);
    const worldSendPercent = useValue(worldSendPercent$);
    const isHost = useValue(isHost$);
    const percent = isHost ? worldSendPercent : mapTransferPercent;

    // Shown from the first "connecting" until connected/offline. An error keeps it
    // up (so the failure is visible) until the player dismisses it.
    const [active, setActive] = useState(false);
    useEffect(() => {
        if (statusKind === "error") {
            setActive(true);
        } else if (statusKind === "syncing") {
            setActive(true);
        } else if (isHost) {
            setActive(false);
        } else if (statusKind === "connecting") {
            setActive(true);
        } else if (statusKind === "connected" || statusKind === "offline" || statusKind === "disabled") {
            setActive(false);
        }
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
    const determinate = progressMode === "determinate" && percent >= 0;

    return (
        <Portal>
            <InputActionBarrier>
                <div style={styles.overlay}>
                    <div style={styles.title}>{t(LOC.multiplayer, "Multiplayer")}</div>

                    {failed ? (
                        <>
                            <div style={styles.error}>
                                <div style={styles.errorTitle}>
                                    {statusTitle || t(LOC.connectionFailed, "Connection failed")}
                                </div>
                                {statusDetail ? <div style={styles.errorSummary}>{statusDetail}</div> : null}
                                {statusHelp ? (
                                    <>
                                        <div style={styles.helpTitle}>{t(LOC.tryThis, "Try this")}</div>
                                        <div style={styles.errorHelp}>{statusHelp}</div>
                                    </>
                                ) : null}
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
                                    {determinate ? <span style={styles.percent}>{clamped}%</span> : null}
                                </div>
                                {determinate ? (
                                    <div style={styles.track}>
                                        <div style={{ ...styles.fill, width: `${clamped}%` }} />
                                    </div>
                                ) : (
                                    <IndeterminateBar />
                                )}
                                <div style={styles.detail}>{statusDetail}</div>
                                <div style={styles.hint}>
                                    {isHost
                                        ? t(LOC.hostLoadingHint, "The city will resume when every player is ready.")
                                        : t(LOC.loadingHint, "Keep this window open while the host's city is transferred.")}
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
