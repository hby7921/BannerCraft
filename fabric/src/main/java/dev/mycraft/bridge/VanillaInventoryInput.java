package dev.mycraft.bridge;

import com.mojang.blaze3d.platform.InputConstants;
import net.minecraft.client.input.KeyEvent;

/** Host uses stable logical key values; 26.3 Minecraft uses SDL scancodes/modifier masks. */
final class VanillaInventoryInput {
    static int modifiers(int host) {
        return ((host & 1) != 0 ? InputConstants.MOD_SHIFT : 0)
                | ((host & 2) != 0 ? InputConstants.MOD_CONTROL : 0)
                | ((host & 4) != 0 ? InputConstants.MOD_ALT : 0);
    }
    static int button(int host) {
        return switch(host) {
            case 0 -> InputConstants.MOUSE_BUTTON_LEFT;
            case 1 -> InputConstants.MOUSE_BUTTON_RIGHT;
            case 2 -> InputConstants.MOUSE_BUTTON_MIDDLE;
            default -> throw new IllegalArgumentException("mouse button");
        };
    }
    static KeyEvent key(int host, int mods) {
        int scan, symbol = host;
        if(host >= 65 && host <= 90) { scan = InputConstants.KEY_A + host - 65; symbol = host + 32; }
        else if(host >= 49 && host <= 57) scan = InputConstants.KEY_1 + host - 49;
        else scan = switch(host) {
            case 48 -> InputConstants.KEY_0;
            case 32 -> InputConstants.KEY_SPACE;
            case 256 -> InputConstants.KEY_ESCAPE;
            case 257 -> InputConstants.KEY_RETURN;
            case 258 -> InputConstants.KEY_TAB;
            case 259 -> InputConstants.KEY_BACKSPACE;
            case 260 -> InputConstants.KEY_INSERT;
            case 261 -> InputConstants.KEY_DELETE;
            case 262 -> InputConstants.KEY_RIGHT;
            case 263 -> InputConstants.KEY_LEFT;
            case 264 -> InputConstants.KEY_DOWN;
            case 265 -> InputConstants.KEY_UP;
            case 266 -> InputConstants.KEY_PAGEUP;
            case 267 -> InputConstants.KEY_PAGEDOWN;
            case 268 -> InputConstants.KEY_HOME;
            case 269 -> InputConstants.KEY_END;
            case 39 -> InputConstants.KEY_APOSTROPHE;
            case 44 -> InputConstants.KEY_COMMA;
            case 45 -> InputConstants.KEY_MINUS;
            case 46 -> InputConstants.KEY_PERIOD;
            case 47 -> InputConstants.KEY_SLASH;
            case 59 -> InputConstants.KEY_SEMICOLON;
            case 61 -> InputConstants.KEY_EQUALS;
            case 91 -> InputConstants.KEY_LBRACKET;
            case 92 -> InputConstants.KEY_BACKSLASH;
            case 93 -> InputConstants.KEY_RBRACKET;
            case 96 -> InputConstants.KEY_GRAVE;
            default -> throw new IllegalArgumentException("key");
        };
        return new KeyEvent(scan, symbol, mods);
    }
    private VanillaInventoryInput() {}
}
