/**
 * Wobkey Crush 80 wired per-key RGB, V3.
 * Requires `firmware/releases/v1.06-per-key/firmware_per_key_v2.bin` (PKRG protocol version 1).
 * No firmware flashing, wireless-mode commands, or EEPROM saves.
 *
 * LED indices: v1.06 matrix table at 0x1BC74. Geometry is an ANSI TKL layout;
 * alternate Enter/Space emitters share their physical key's canvas region.
 * Esc=0, F1=1, Caps Lock=52 were physically confirmed. See `docs/user/PER-KEY-RGB.md`.
 */
export function Name() { return "Wobkey Crush 80 (Wired) V3 Per-Key"; }
export function VendorId() { return 0x320F; }
export function ProductId() { return 0x5055; }
export function Publisher() { return "Community"; }
export function Type() { return "hid"; }
export function Size() { return [37, 12]; }
export function DefaultLayout() { return "Default"; }
export function DeviceMessage() {
    return ["Requires the per-key v1.06 firmware; wired USB only.",
        "V3 uses individual RGB colors. Remove older wired custom plugins before installing it."];
}
export function ControllableParameters() {
    return [{ property: "logFrameTiming", label: "Log frame timing (diagnostic)",
        type: "boolean", default: false }];
}
export function Validate(endpoint) {
    return endpoint.interface === 1 && endpoint.usage_page === 0xFF60 && endpoint.usage === 0x61;
}

// Indexed by the firmware's raw LED slot, not VIA matrix order. SignalRGB
// canvas coordinates are integers. Adjacent emitters under the same large
// key intentionally sample its region; a duplicate name is not a new key.
const LEDS = [
    ["Esc", 0, 0], ["F1", 3, 0], ["F2", 5, 0], ["F3", 7, 0],
    ["F4", 9, 0], ["F5", 11, 0], ["F6", 13, 0], ["F7", 15, 0],
    ["F8", 17, 0], ["F9", 20, 0], ["F10", 22, 0], ["F11", 24, 0],
    ["F12", 26, 0], ["AudioMute", 28, 0], ["Print Screen", 31, 0],
    ["Scroll Lock", 33, 0], ["Pause Break", 35, 0],
    ["`", 0, 3], ["1", 2, 3], ["2", 4, 3], ["3", 6, 3],
    ["4", 8, 3], ["5", 10, 3], ["6", 12, 3], ["7", 14, 3],
    ["8", 16, 3], ["9", 18, 3], ["0", 20, 3], ["-", 22, 3],
    ["=", 24, 3], ["Backspace", 27, 3], ["Insert", 31, 3],
    ["Home", 33, 3], ["Page Up", 35, 3],
    ["Tab", 1, 5], ["Q", 3, 5], ["W", 5, 5], ["E", 7, 5],
    ["R", 9, 5], ["T", 11, 5], ["Y", 13, 5], ["U", 15, 5],
    ["I", 17, 5], ["O", 19, 5], ["P", 21, 5], ["[", 23, 5],
    ["]", 25, 5], ["\\", 28, 5], ["Del", 31, 5], ["End", 33, 5],
    ["Page Down", 35, 5],
    ["Caps Lock", 1, 7], ["Caps Lock", 1, 7],
    ["A", 4, 7], ["S", 6, 7], ["D", 8, 7], ["F", 10, 7],
    ["G", 12, 7], ["H", 14, 7], ["J", 16, 7], ["K", 18, 7],
    ["L", 20, 7], [";", 22, 7], ["'", 24, 7], ["ISO_#", 26, 7],
    ["Enter", 27, 7],
    ["Left Shift", 1, 9], ["Z", 5, 9], ["X", 7, 9], ["C", 9, 9],
    ["V", 11, 9], ["B", 13, 9], ["N", 15, 9], ["M", 17, 9],
    [",", 19, 9], [".", 21, 9], ["/", 23, 9], ["Right Shift", 26, 9],
    ["Up Arrow", 33, 9],
    ["Left Ctrl", 0, 11], ["Left Win", 3, 11], ["Left Alt", 5, 11],
    ["Space", 9, 11], ["Space", 13, 11], ["Space", 17, 11],
    ["Right Alt", 20, 11], ["Fn", 23, 11], ["Right Win", 25, 11],
    ["Right Ctrl", 28, 11], ["Left Arrow", 31, 11],
    ["Down Arrow", 33, 11], ["Right Arrow", 35, 11]
];
const names = LEDS.map(led => led[0]);
const positions = LEDS.map(led => [led[1], led[2]]);
export function LedNames() { return names; }
export function LedPositions() { return positions; }

const LED_COUNT = 92;
const CHUNK_LEDS = 8;
const CHANNEL = 0x7F;
const TIMEOUT_MS = 100;
const frame = new Array(LED_COUNT * 3).fill(0);
const previousFrame = new Array(LED_COUNT * 3).fill(-1);
const packet = new Array(33).fill(0);
const chunkPayload = new Array(6 + CHUNK_LEDS * 3).fill(0);
const readReport = [0x00];
const changedStarts = new Array(Math.ceil(LED_COUNT / CHUNK_LEDS)).fill(0);
const chunkHeader = [7, CHANNEL, 2];
let pendingReplies = 0;
const timing = { frames: 0, chunks: 0, prepare: 0, writes: 0, reads: 0,
    gaps: 0, gapCount: 0, previousEnd: 0 };
let saved = null;
let streaming = false;

// SignalRGB's HID write needs report ID zero. Its input path can return
// either 32 payload bytes or a zero-prefixed 33-byte report; use the actual
// read size rather than assuming padding is a received acknowledgement.
function send(payload) {
    packet.fill(0);
    for (let i = 0; i < payload.length; i++) packet[i + 1] = payload[i];
    if (device.write(packet, packet.length) === -1) throw new Error("Keyboard write failed");
    pendingReplies++;
}

function receive(payload) {
    const raw = device.read(readReport, 33, TIMEOUT_MS);
    const size = device.getLastReadSize();
    if (size === 0) throw new Error("Keyboard response timed out");
    pendingReplies--;
    const offset = size === 33 && raw[0] === 0 ? 1 : 0;
    if ((size !== 32 && size !== 33) || raw.length < offset + 32) {
        throw new Error("Unexpected HID response size");
    }
    const reply = offset === 1 ? raw.slice(1, 33) : raw;
    if (reply[0] !== payload[0] || reply[1] !== payload[1] || reply[2] !== payload[2]) {
        throw new Error("Unexpected VIA response; close other keyboard-control software");
    }
    if (payload[1] === CHANNEL && reply[3] !== 0) {
        throw new Error(`Per-key firmware rejected the request (status ${reply[3]})`);
    }
    return reply;
}

function exchange(payload) {
    send(payload);
    return receive(payload);
}

function drainReplies() {
    // A timed-out reply can still arrive later. Do not issue restoration
    // commands while it or the rest of a failed frame is outstanding.
    while (pendingReplies > 0) {
        device.read(readReport, 33, TIMEOUT_MS);
        if (device.getLastReadSize() === 0) throw new Error("Keyboard replies still outstanding");
        pendingReplies--;
    }
    device.clearReadBuffer();
}

function capabilities() {
    const reply = exchange([8, CHANNEL, 0]);
    const expected = [80, 75, 82, 71, 1, LED_COUNT, CHUNK_LEDS]; // PKRG
    for (let i = 0; i < expected.length; i++) {
        if (reply[i + 4] !== expected[i]) {
            throw new Error("Compatible per-key firmware required; no lighting writes sent");
        }
    }
    if (reply[11] !== 0 && reply[11] !== 1) throw new Error("Invalid firmware mode flag");
    return reply[11];
}

function getOEM(id) {
    const value = exchange([8, 3, id])[3];
    if (value > (id === 1 ? 9 : 18)) throw new Error("Invalid OEM lighting state");
    return value;
}

function setOEM(id, value) {
    if (exchange([7, 3, id, value])[3] !== value) throw new Error("OEM setting was not acknowledged");
}

function setMode(enabled) {
    if (exchange([7, CHANNEL, 1, 0, enabled])[4] !== enabled) {
        throw new Error("Per-key mode was not acknowledged");
    }
}

function readFrame() {
    const colors = new Array(LED_COUNT * 3);
    for (let start = 0; start < LED_COUNT; start += CHUNK_LEDS) {
        const count = Math.min(CHUNK_LEDS, LED_COUNT - start);
        const reply = exchange([8, CHANNEL, 2, 0, start, count]);
        if (reply[4] !== start || reply[5] !== count) throw new Error("RGB readback range mismatch");
        for (let i = 0; i < count * 3; i++) colors[start * 3 + i] = reply[6 + i];
    }
    return colors;
}

function sendChunk(colors, start, count) {
    chunkPayload[0] = 7;
    chunkPayload[1] = CHANNEL;
    chunkPayload[2] = 2;
    chunkPayload[3] = 0;
    chunkPayload[4] = start;
    chunkPayload[5] = count;
    for (let i = 0; i < CHUNK_LEDS * 3; i++) {
        chunkPayload[6 + i] = i < count * 3 ? colors[start * 3 + i] : 0;
    }
    send(chunkPayload);
}

function acknowledgeChunk(colors, start, count) {
    const reply = receive(chunkHeader);
    if (reply[4] !== start || reply[5] !== count) throw new Error("RGB acknowledgement range mismatch");
    for (let i = 0; i < count * 3; i++) {
        if (reply[6 + i] !== colors[start * 3 + i]) throw new Error("RGB write acknowledgement mismatch");
    }
}

function writeFrame(colors) {
    for (let start = 0; start < LED_COUNT; start += CHUNK_LEDS) {
        const count = Math.min(CHUNK_LEDS, LED_COUNT - start);
        sendChunk(colors, start, count);
        acknowledgeChunk(colors, start, count);
    }
}

function restoreState() {
    if (saved === null) return;
    drainReplies();
    setMode(0);
    writeFrame(saved.colors);
    setOEM(1, saved.brightness);
    setOEM(2, saved.effect);
    setMode(saved.enabled);
    saved = null;
}

export function onlogFrameTimingChanged() {
    for (const key in timing) timing[key] = 0;
}

function recordTiming(start, prepared, written, finished, chunks) {
    if (timing.previousEnd !== 0) {
        timing.gaps += start - timing.previousEnd;
        timing.gapCount++;
    }
    timing.frames++;
    timing.chunks += chunks;
    timing.prepare += prepared - start;
    timing.writes += written - prepared;
    timing.reads += finished - written;
    timing.previousEnd = finished;
    if (timing.frames < 120) return;
    const frames = timing.frames;
    const work = (timing.prepare + timing.writes + timing.reads) / frames;
    const gap = timing.gapCount === 0 ? 0 : timing.gaps / timing.gapCount;
    device.log(`Wobkey V3 timing: ${frames} frames; chunks/frame=${(timing.chunks / frames).toFixed(2)}; ` +
        `prepare=${(timing.prepare / frames).toFixed(2)}ms; write=${(timing.writes / frames).toFixed(2)}ms; ` +
        `ack=${(timing.reads / frames).toFixed(2)}ms; work=${work.toFixed(2)}ms; gap=${gap.toFixed(2)}ms`);
    onlogFrameTimingChanged();
}

export function Initialize() {
    streaming = false;
    saved = null;
    previousFrame.fill(-1);
    pendingReplies = 0;
    onlogFrameTimingChanged();
    try {
        device.clearReadBuffer();
        const enabled = capabilities();
        const brightness = getOEM(1);
        const effect = getOEM(2);
        const colors = readFrame();
        // No mutation occurs until firmware compatibility and the full snapshot
        // have been read successfully.
        saved = { enabled, brightness, effect, colors };
        setMode(0);
        frame.fill(0);
        writeFrame(frame);
        previousFrame.fill(0);
        // SignalRGB already scales device.color() by its brightness slider.
        // Keep the keyboard's own brightness at its maximum, not dynamic HSV V.
        setOEM(1, 9);
        setOEM(2, 6);
        setMode(1);
        streaming = true;
    } catch (error) {
        device.log(`Wobkey V3 initialization failed: ${error.message}`);
        try { restoreState(); }
        catch (restoreError) { device.log(`Wobkey V3 restore failed: ${restoreError.message}`); }
    }
}

export function Render() {
    if (!streaming) return;
    try {
        const measuring = logFrameTiming;
        const started = measuring ? Date.now() : 0;
        for (let index = 0; index < LED_COUNT; index++) {
            const color = device.color(positions[index][0], positions[index][1]);
            frame[index * 3] = color[0];
            frame[index * 3 + 1] = color[1];
            frame[index * 3 + 2] = color[2];
        }
        const prepared = measuring ? Date.now() : 0;
        let changedCount = 0;
        // The wired firmware waits for its IN FIFO before replying; Windows
        // queues input reports independently of these reads. Bound the batch
        // to one frame (12 reports), then consume and verify every reply.
        for (let start = 0; start < LED_COUNT; start += CHUNK_LEDS) {
            const count = Math.min(CHUNK_LEDS, LED_COUNT - start);
            const first = start * 3, end = (start + count) * 3;
            let changed = false;
            for (let i = first; i < end; i++) {
                if (frame[i] !== previousFrame[i]) { changed = true; break; }
            }
            if (!changed) continue;
            sendChunk(frame, start, count);
            changedStarts[changedCount++] = start;
        }
        const written = measuring ? Date.now() : 0;
        for (let chunk = 0; chunk < changedCount; chunk++) {
            const start = changedStarts[chunk];
            acknowledgeChunk(frame, start, Math.min(CHUNK_LEDS, LED_COUNT - start));
        }
        // Commit the cache only after the entire batch has been acknowledged.
        for (let chunk = 0; chunk < changedCount; chunk++) {
            const first = changedStarts[chunk] * 3;
            const end = Math.min(first + CHUNK_LEDS * 3, frame.length);
            for (let i = first; i < end; i++) previousFrame[i] = frame[i];
        }
        if (measuring) recordTiming(started, prepared, written, Date.now(), changedCount);
    } catch (error) {
        streaming = false;
        device.log(`Wobkey V3 streaming stopped: ${error.message}. Toggle streaming to reconnect.`);
        try { restoreState(); }
        catch (restoreError) { device.log(`Wobkey V3 restore failed: ${restoreError.message}`); }
    }
}

export function Shutdown() {
    streaming = false;
    if (saved === null) return;
    try {
        restoreState();
    } catch (error) {
        device.log(`Wobkey V3 shutdown restore failed: ${error.message}`);
    }
}
