package dev.mycraft.bridge;

import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

/** Incremental UTF-8 framing, bounded before a peer sends a newline. */
final class LineFrames {
    private final int limit;
    private final ByteArrayOutputStream partial = new ByteArrayOutputStream();
    LineFrames(int limit) { this.limit = limit; }
    List<String> accept(byte[] bytes, int length) throws java.io.IOException {
        List<String> result = new ArrayList<>();
        for (int i = 0; i < length; i++) {
            if (partial.size() + 1 > limit) throw new java.io.IOException("line overflow");
            if (bytes[i] == '\n') {
                var decoder = StandardCharsets.UTF_8.newDecoder()
                        .onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT);
                String line = decoder.decode(ByteBuffer.wrap(partial.toByteArray())).toString();
                partial.reset();
                if (line.isBlank()) throw new java.io.IOException("empty frame");
                result.add(line);
            } else {
                partial.write(bytes[i]);
            }
        }
        return result;
    }
}
