package dev.mycraft.bridge;

/** A screen belongs to this bridge only while it exposes the live local player's exact current menu. */
final class VanillaInventoryScreenPolicy {
    static boolean mayAdopt(boolean activeSession, boolean livePlayer, boolean spectator, boolean containerScreen, boolean currentMenu) {
        return activeSession && livePlayer && !spectator && containerScreen && currentMenu;
    }
    static boolean inputMatchesScreen(long displayedFrame, long firstScreenFrame, long latestCapturedFrame) {
        return displayedFrame > 0 && firstScreenFrame > 0 && displayedFrame >= firstScreenFrame && displayedFrame <= latestCapturedFrame;
    }
    private VanillaInventoryScreenPolicy() {}
}
