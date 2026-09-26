import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';
import test from 'node:test';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const pluginPath = process.env.SIGNALRGB_TEST_PLUGIN || path.join(root, 'plugins/signalrgb/wired/WobkeyCrush80_v3.js');
const expectedProductId = path.basename(pluginPath).includes('Wireless') ? 0x5088 : 0x5055;

// Stateful USB endpoint model: assertions concern rendered colors, ownership,
// and restored state, not the presence of a mock or copies of request headers.
class Keyboard {
  constructor({ compatible = true, prefix = false, enabled = false } = {}) {
    this.compatible = compatible;
    this.prefix = prefix;
    this.enabled = enabled;
    this.protocol = 2;
    this.staged = [];
    this.nextFragment = -1;
    this.frameSequence = -1;
    this.effect = 4;
    this.brightness = 5;
    this.rgb = Array.from({ length: 92 }, (_, i) => [i, 255-i, i ^ 85]);
    this.pending = [];
    this.lastRead = 0;
    this.writes = 0;
    this.rgbWrites = 0;
    this.rejectNextChunk = false;
    this.logs = [];
    this.canvas = () => [12, 34, 56];
    this.device = {
      write: (data, length) => this.write(data, length),
      read: () => this.read(),
      getLastReadSize: () => this.lastRead,
      clearReadBuffer: () => { this.pending = []; },
      color: (x, y) => this.canvas(x, y),
      log: (message) => this.logs.push(String(message)),
    };
  }

  snapshot() {
    return structuredClone({ enabled: this.enabled, effect: this.effect,
      brightness: this.brightness, rgb: this.rgb });
  }

  write(data, length) {
    assert.equal(length, 33);
    assert.equal(data.length, 33);
    assert.equal(data[0], 0);
    const response = Array.from(data).slice(1);
    const [command, channel, operation] = response;
    assert.ok(command === 7 || command === 8);
    if (command === 7) this.writes++;
    if (channel === 127 && this.compatible) {
      response[3] = 0;
      if (operation === 0 && command === 8) {
        response.splice(4, 10, 80, 75, 82, 71, this.protocol, 92, 8, Number(this.enabled), 9, 11);
      } else if (operation === 1) {
        if (command === 7) {
          if (response[4] > 1) response[3] = 3;
          else this.enabled = Boolean(response[4]);
        } else response[4] = Number(this.enabled);
      } else if (operation === 2) {
        const start = response[4], count = response[5];
        if (count < 1 || count > 8 || start + count > 92) response[3] = 2;
        else if (command === 7 && this.rejectNextChunk) {
          this.rejectNextChunk = false;
          response[3] = 2;
        } else {
          for (let i = 0; i < count; i++) {
            if (command === 7) this.rgb[start+i] = response.slice(6+i*3, 9+i*3);
            else response.splice(6+i*3, 3, ...this.rgb[start+i]);
          }
          if (command === 7) this.rgbWrites++;
        }
      } else if (this.protocol === 2 && command === 7 && operation === 3) {
        const sequence = data[4], index = data[5];
        if (index === 0) { this.frameSequence = sequence; this.nextFragment = 0; this.staged = []; }
        if (sequence !== this.frameSequence || index !== this.nextFragment || index >= 11 || this.rejectNextChunk) {
          this.rejectNextChunk = false;
          this.nextFragment = -1;
        } else {
          this.staged.push(...Array.from(data).slice(6, 6 + (index === 10 ? 6 : 27)));
          this.nextFragment++;
          this.rgbWrites++;
        }
        return;
      } else if (this.protocol === 2 && command === 7 && operation === 4) {
        if (response[4] !== this.frameSequence || this.nextFragment !== 11) response[3] = 4;
        else {
          this.rgb = Array.from({ length: 92 }, (_, i) => this.staged.slice(i*3, i*3+3));
          this.nextFragment = -1;
        }
      } else response[3] = 1;
    } else if (channel === 3 && (operation === 1 || operation === 2)) {
      const field = operation === 1 ? 'brightness' : 'effect';
      if (command === 7) this[field] = response[3];
      else response[3] = this[field];
    }
    this.pending.push(this.prefix ? [0, ...response] : response);
  }

  read() {
    const result = this.pending.shift() || [];
    this.lastRead = result.length;
    return result;
  }
}

async function loadPlugin(keyboard, settings = {}) {
  const context = vm.createContext({ device: keyboard.device, logFrameTiming: false, ...settings });
  const module = new vm.SourceTextModule(await readFile(pluginPath, 'utf8'), { context });
  await module.link(() => { throw new Error('Plugin must be a standalone file'); });
  await module.evaluate();
  return module.namespace;
}

test('canvas colors reach independent LED slots, including black and white', async () => {
  const keyboard = new Keyboard();
  keyboard.canvas = (x, y) => {
    if (x === 0 && y === 0) return [0, 255, 0];
    if (x === 3 && y === 0) return [0, 0, 255];
    if (x === 5 && y === 0) return [255, 255, 255];
    return [0, 0, 0];
  };
  const plugin = await loadPlugin(keyboard);
  plugin.Initialize();
  plugin.Render();
  assert.equal(keyboard.enabled, true);
  assert.equal(keyboard.effect, 6);
  assert.equal(keyboard.brightness, 9);
  assert.deepEqual(keyboard.rgb.slice(0, 4), [[0,255,0], [0,0,255], [255,255,255], [0,0,0]]);
  assert.deepEqual(keyboard.rgb[91], [0,0,0]);
});

test('incompatible firmware cannot receive lighting writes', async () => {
  const keyboard = new Keyboard({ compatible: false });
  const before = keyboard.snapshot();
  const plugin = await loadPlugin(keyboard);
  plugin.Initialize();
  plugin.Render();
  plugin.Shutdown(false);
  assert.deepEqual(keyboard.snapshot(), before);
  assert.equal(keyboard.writes, 0);
  assert.ok(keyboard.logs.some(message => /firmware/i.test(message)));
});

test('shutdown restores OEM lighting and an existing per-key profile', async () => {
  for (const enabled of [false, true]) {
    const keyboard = new Keyboard({ enabled, prefix: true });
    const before = keyboard.snapshot();
    const plugin = await loadPlugin(keyboard);
    plugin.Initialize();
    plugin.Render();
    plugin.Shutdown(false);
    assert.deepEqual(keyboard.snapshot(), before);
  }
});

test('unchanged frames do not consume USB bandwidth; changes still render', async () => {
  const keyboard = new Keyboard();
  const plugin = await loadPlugin(keyboard);
  plugin.Initialize();
  plugin.Render();
  keyboard.rgbWrites = 0;
  plugin.Render();
  assert.equal(keyboard.rgbWrites, 0);
  keyboard.canvas = (x, y) => x === 3 && y === 0 ? [99,88,77] : [12,34,56];
  plugin.Render();
  assert.deepEqual(keyboard.rgb[1], [99,88,77]);
  assert.deepEqual(keyboard.rgb[91], [12,34,56]);
});

test('a rejected frame stops streaming and shutdown can recover prior lighting', async () => {
  const keyboard = new Keyboard();
  const before = keyboard.snapshot();
  const plugin = await loadPlugin(keyboard);
  plugin.Initialize();
  keyboard.rejectNextChunk = true;
  plugin.Render();
  const stopped = keyboard.snapshot();
  plugin.Render();
  assert.deepEqual(keyboard.snapshot(), stopped);
  assert.ok(keyboard.logs.some(message => /stopped|failed|error/i.test(message)));
  plugin.Shutdown(false);
  assert.deepEqual(keyboard.snapshot(), before);
});

test('firmware LED indices retain their functional keypress bindings', async () => {
  const plugin = await loadPlugin(new Keyboard());
  const names = Array.from(plugin.LedNames());
  const positions = Array.from(plugin.LedPositions(), point => Array.from(point));
  assert.equal(names.length, 92);
  assert.equal(positions.length, 92);
  for (const [index, name] of [[0,'Esc'], [1,'F1'], [13,'AudioMute'],
    [31,'Insert'], [34,'Tab'], [51,'Caps Lock'], [52,'Caps Lock'],
    [53,'A'], [66,'Left Shift'], [79,'Left Ctrl'], [91,'Right Arrow']]) {
    assert.equal(names[index], name);
  }
  const [width, height] = plugin.Size();
  for (const [x, y] of positions) {
    assert.ok(Number.isInteger(x) && x >= 0 && x < width);
    assert.ok(Number.isInteger(y) && y >= 0 && y < height);
  }
  assert.equal(plugin.Validate({interface: 1, usage_page: 0xFF60, usage: 0x61}), true);
  assert.equal(plugin.Validate({interface: 3, usage_page: 0xFF1C, usage: 0x61}), false);
  assert.equal(plugin.Validate({interface: 2, usage_page: 0xFFEF, usage: 0x61}), false);
});

test('device selection uses the intended USB transport identity', async () => {
  const plugin = await loadPlugin(new Keyboard());
  assert.equal(plugin.VendorId(), 0x320F);
  assert.equal(plugin.ProductId(), expectedProductId);
  assert.equal(plugin.Validate({interface: 0, usage_page: 0x0001, usage: 0x06}), false);
});

test('a missing capability reply leaves the keyboard unchanged', async () => {
  const keyboard = new Keyboard();
  keyboard.device.read = () => { keyboard.lastRead = 0; return []; };
  const before = keyboard.snapshot();
  const plugin = await loadPlugin(keyboard);
  plugin.Initialize();
  plugin.Render();
  plugin.Shutdown(false);
  assert.deepEqual(keyboard.snapshot(), before);
  assert.equal(keyboard.writes, 0);
  assert.ok(keyboard.logs.some(message => /timed out/i.test(message)));
});

test('a queued rejected frame is drained before restoring the previous lighting',
  { skip: expectedProductId !== 0x5055 }, async () => {
  const keyboard = new Keyboard({ enabled: true, prefix: true });
  const before = keyboard.snapshot();
  const plugin = await loadPlugin(keyboard);
  plugin.Initialize();
  const write = keyboard.device.write;
  let injected = false;
  keyboard.device.write = (data, length) => {
    write(data, length);
    if (!injected && data[1] === 7 && data[2] === 127 && data[3] === 4) {
      injected = true;
      // A stale commit reply must not be accepted for the submitted frame.
      keyboard.pending.unshift([0, 7, 127, 4, 0, data[5] ^ 255, ...new Array(27).fill(0)]);
    }
  };
  plugin.Render();
  assert.deepEqual(keyboard.snapshot(), before);
  const writesAfterFailure = keyboard.writes;
  plugin.Render();
  assert.equal(keyboard.writes, writesAfterFailure);
});

test('bad chunk acknowledgements stop rendering and preserve recoverable state', async () => {
  for (const corrupt of [
    reply => { reply[3] = 4; },
    reply => { reply[4] ^= 255; },
    reply => { reply[2] = 255; },
    reply => { reply.splice(12); },
  ]) {
    const keyboard = new Keyboard({ enabled: true });
    const before = keyboard.snapshot();
    const plugin = await loadPlugin(keyboard);
    plugin.Initialize();
    const read = keyboard.device.read;
    let injected = false;
    keyboard.device.read = () => {
      const reply = read();
      if (!injected && reply[0] === 7 && reply[1] === 127 && reply[2] === (expectedProductId === 0x5055 ? 4 : 2)) {
        injected = true;
        if (expectedProductId === 0x5055) keyboard.pending.unshift(reply.slice());
        corrupt(reply);
        keyboard.lastRead = reply.length;
      }
      return reply;
    };
    plugin.Render();
    assert.deepEqual(keyboard.snapshot(), before);
    const writesAfterFailure = keyboard.writes;
    plugin.Render();
    assert.equal(keyboard.writes, writesAfterFailure);
  }
});

test('a read timeout cannot let a late RGB reply acknowledge restoration',
  { skip: expectedProductId !== 0x5055 }, async () => {
  const keyboard = new Keyboard({ enabled: true });
  const before = keyboard.snapshot();
  const plugin = await loadPlugin(keyboard);
  plugin.Initialize();
  const read = keyboard.device.read;
  let timedOut = false;
  keyboard.device.read = () => {
    if (!timedOut) {
      timedOut = true;
      keyboard.lastRead = 0;
      return [];
    }
    return read();
  };
  plugin.Render();
  assert.deepEqual(keyboard.snapshot(), before);
  const writesAfterFailure = keyboard.writes;
  plugin.Render();
  assert.equal(keyboard.writes, writesAfterFailure);
});

test('unavailable acknowledgements defer restoration until the queue can be drained',
  { skip: expectedProductId !== 0x5055 }, async () => {
  const keyboard = new Keyboard({ enabled: true });
  const before = keyboard.snapshot();
  const plugin = await loadPlugin(keyboard);
  plugin.Initialize();
  const read = keyboard.device.read;
  keyboard.device.read = () => { keyboard.lastRead = 0; return []; };
  plugin.Render();
  const stalled = keyboard.snapshot();
  assert.deepEqual(stalled.rgb, new Array(92).fill([12,34,56]));
  assert.equal(stalled.enabled, true);
  assert.equal(stalled.effect, 6);
  const writesAfterFailure = keyboard.writes;
  plugin.Render();
  plugin.Shutdown();
  assert.equal(keyboard.writes, writesAfterFailure);
  keyboard.device.read = read;
  plugin.Shutdown();
  assert.deepEqual(keyboard.snapshot(), before);
});

test('a failed write drains the accepted prefix before restoring lighting',
  { skip: expectedProductId !== 0x5055 }, async () => {
  for (const failure of ['throw', 'return']) {
    const keyboard = new Keyboard({ enabled: true });
    const before = keyboard.snapshot();
    const plugin = await loadPlugin(keyboard);
    plugin.Initialize();
    const write = keyboard.device.write;
    let chunks = 0;
    keyboard.device.write = (data, length) => {
      if (data[1] === 7 && data[2] === 127 && data[3] === 3 && ++chunks === 5) {
        if (failure === 'throw') throw new Error('USB write failed');
        return -1;
      }
      return write(data, length);
    };
    plugin.Render();
    assert.deepEqual(keyboard.snapshot(), before);
    const writesAfterFailure = keyboard.writes;
    plugin.Render();
    assert.equal(keyboard.writes, writesAfterFailure);
  }
});

test('old per-key firmware is rejected before streaming writes',
  { skip: expectedProductId !== 0x5055 }, async () => {
  const keyboard = new Keyboard();
  keyboard.protocol = 1;
  const before = keyboard.snapshot();
  const plugin = await loadPlugin(keyboard);
  plugin.Initialize();
  plugin.Render();
  assert.deepEqual(keyboard.snapshot(), before);
  assert.equal(keyboard.writes, 0);
});

test('a lost fragment leaves the previous complete frame recoverable',
  { skip: expectedProductId !== 0x5055 }, async () => {
  const keyboard = new Keyboard({ enabled: true });
  const before = keyboard.snapshot();
  const plugin = await loadPlugin(keyboard);
  plugin.Initialize();
  const write = keyboard.device.write;
  let dropped = false;
  keyboard.device.write = (packet, size) => {
    if (!dropped && packet[1] === 7 && packet[2] === 127 && packet[3] === 3 && packet[5] === 5) {
      dropped = true;
      return;
    }
    return write(packet, size);
  };
  plugin.Render();
  assert.deepEqual(keyboard.snapshot(), before);
  const writesAfterFailure = keyboard.writes;
  plugin.Render();
  assert.equal(keyboard.writes, writesAfterFailure);
});

test('a stale frame reply cannot bypass waiting for the real commit reply',
  { skip: expectedProductId !== 0x5055 }, async () => {
  const keyboard = new Keyboard({ enabled: true });
  const before = keyboard.snapshot();
  const plugin = await loadPlugin(keyboard);
  plugin.Initialize();
  const read = keyboard.device.read;
  let originalReply;
  let injected = false;
  keyboard.device.read = () => {
    if (!injected) {
      injected = true;
      originalReply = read();
      const stale = originalReply.slice();
      stale[4] ^= 255;
      return stale;
    }
    if (originalReply) {
      const reply = originalReply;
      originalReply = null;
      keyboard.lastRead = reply.length;
      return reply;
    }
    return read();
  };
  plugin.Render();
  assert.deepEqual(keyboard.snapshot(), before);
});

test('frame sequence wrap preserves complete RGB output',
  { skip: expectedProductId !== 0x5055 }, async () => {
  const keyboard = new Keyboard();
  const plugin = await loadPlugin(keyboard);
  plugin.Initialize();
  for (let frame = 0; frame < 260; frame++) {
    keyboard.canvas = (x, y) => [(frame + x) & 255, y, 255 - (frame & 255)];
    plugin.Render();
  }
  assert.deepEqual(keyboard.rgb[0], [3, 0, 252]);
  assert.deepEqual(keyboard.rgb[91], [38, 11, 252]);
});
