/**
 * Wobkey Crush 80 (Wireless/2.4G Dongle) - SignalRGB Plugin V2
 *
 * Requires patched firmware (firmware_patched.bin) to enable hue control
 * via VIA USB. The stock firmware has a bug where the H byte is ignored.
 *
 * This plugin targets the 2.4G wireless dongle (PID 0x5088).
 * For wired USB mode, use WobkeyCrush80.js (PID 0x5055).
 *
 * Unlike V1, this plugin does NOT override the keyboard's active effect.
 * It only pushes color state; whatever effect mode the keyboard is already
 * in continues to run.
 *
 * VIA commands are transparently forwarded by the dongle to the keyboard
 * over the 2.4G link; same protocol, same channel/ID scheme.
 */

// ---------------------------------------------------------------------------
// Device identity
// ---------------------------------------------------------------------------

export function Name() {
  return "Wobkey Crush 80 (Wired) V2";
}
export function VendorId() {
  return 0x320f;
}
export function ProductId() {
  return 0x5055;
}
export function Publisher() {
  return "Community";
}

// Canvas grid: 22x7 with LEDs spread across it so SignalRGB's effect engine
// has positions to render onto. The keyboard only supports a single whole-
// board color, so we collapse the canvas to one representative hue.
const COLS = 22;
const ROWS = 7;

let vLedNames = [];
let vLedPositions = [];
for (let y = 0; y < ROWS; y++) {
  for (let x = 0; x < COLS; x++) {
    vLedNames.push(`Key ${y * COLS + x}`);
    vLedPositions.push([x, y]);
  }
}

export function Size() {
  return [COLS, ROWS];
}
export function DefaultLayout() {
  return "Default";
}
export function LedNames() {
  return vLedNames;
}
export function LedPositions() {
  return vLedPositions;
}

// ---------------------------------------------------------------------------
// Endpoint selection
// ---------------------------------------------------------------------------

export function Validate(endpoint) {
  return (
    endpoint.interface === 1 &&
    endpoint.usage_page === 0xff60 &&
    endpoint.usage === 0x61
  );
}

// ---------------------------------------------------------------------------
// VIA protocol constants
// ---------------------------------------------------------------------------

const VIA_REPORT_SIZE = 32;
const ID_CUSTOM_SET_VALUE = 0x07;

const RGB_CHANNEL = 3;
const RGB_ID_BRIGHTNESS = 1; // 0-9
const RGB_ID_COLOR = 4; // H, S (0-255 each)
const MAX_BRIGHTNESS = 9;

// The keyboard appears to ignore S for the active mode, but the packet still
// requires a second byte. Keep it stable and only vary hue.
const FIXED_SATURATION = 0xff;

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

let lastH = -1;
let lastBrightness = -1;

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------

export function Initialize() {
  lastH = -1;
  lastBrightness = -1;
}

export function Render() {
  const [r, g, b] = getDominantCanvasColor();

  // Ignore empty/black frames so the board keeps its last visible color
  // instead of being driven toward a washed-out neutral state.
  if (r === 0 && g === 0 && b === 0) {
    return;
  }

  const h = rgbToHue(r, g, b);
  const brightness = rgbToBrightness(r, g, b);

  if (h !== lastH) {
    lastH = h;
    sendVIA([
      ID_CUSTOM_SET_VALUE,
      RGB_CHANNEL,
      RGB_ID_COLOR,
      h,
      FIXED_SATURATION,
    ]);
  }

  if (brightness !== lastBrightness) {
    lastBrightness = brightness;
    sendVIA([ID_CUSTOM_SET_VALUE, RGB_CHANNEL, RGB_ID_BRIGHTNESS, brightness]);
  }
}

export function Shutdown() {
  sendVIA([
    ID_CUSTOM_SET_VALUE,
    RGB_CHANNEL,
    RGB_ID_BRIGHTNESS,
    MAX_BRIGHTNESS,
  ]);
}

function getDominantCanvasColor() {
  const buckets = new Map();

  for (let y = 0; y < ROWS; y++) {
    for (let x = 0; x < COLS; x++) {
      const [r, g, b] = device.color(x, y);

      // Ignore pure black so background cells do not overpower the
      // actual effect color.
      if (r === 0 && g === 0 && b === 0) {
        continue;
      }

      const qr = Math.round(r / 16) * 16;
      const qg = Math.round(g / 16) * 16;
      const qb = Math.round(b / 16) * 16;
      const key = `${qr},${qg},${qb}`;
      const bucket = buckets.get(key);

      if (bucket) {
        bucket.count++;
        bucket.r += r;
        bucket.g += g;
        bucket.b += b;
      } else {
        buckets.set(key, { count: 1, r, g, b });
      }
    }
  }

  let dominant = null;

  for (const bucket of buckets.values()) {
    if (!dominant || bucket.count > dominant.count) {
      dominant = bucket;
    }
  }

  return dominant
    ? [
        Math.round(dominant.r / dominant.count),
        Math.round(dominant.g / dominant.count),
        Math.round(dominant.b / dominant.count),
      ]
    : [0, 0, 0];
}

// ---------------------------------------------------------------------------
// RGB -> H  (0-255 to match VIA/QMK hue convention)
// ---------------------------------------------------------------------------

function rgbToHue(r, g, b) {
  r /= 255;
  g /= 255;
  b /= 255;

  const max = Math.max(r, g, b);
  const min = Math.min(r, g, b);
  const d = max - min;

  if (d === 0) {
    return 0;
  }

  let h;
  switch (max) {
    case r:
      h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
      break;
    case g:
      h = ((b - r) / d + 2) / 6;
      break;
    default:
      h = ((r - g) / d + 4) / 6;
      break;
  }

  return Math.round(h * 255) & 0xff;
}

function rgbToBrightness(r, g, b) {
  const v = Math.max(r, g, b);
  return Math.round((v / 255) * MAX_BRIGHTNESS);
}

// ---------------------------------------------------------------------------
// HID write
// ---------------------------------------------------------------------------

function sendVIA(data) {
  // SignalRGB device.write() uses Windows HID API which requires report ID
  // as the first byte. VIA interface has no report ID, so prepend 0x00.
  let buf = new Array(1 + VIA_REPORT_SIZE).fill(0x00);
  buf[0] = 0x00;
  for (let i = 0; i < data.length && i < VIA_REPORT_SIZE; i++) {
    buf[i + 1] = data[i];
  }
  device.write(buf, buf.length);
}
