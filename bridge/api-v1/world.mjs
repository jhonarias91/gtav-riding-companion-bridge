// Optional P8 resources. Map calibration and assets are supplied by the PC operator.
import { createHash } from 'node:crypto';
import { lstat, open, realpath } from 'node:fs/promises';
import { join } from 'node:path';
import { crc32 } from 'node:zlib';

const PNG_HEADER = Buffer.from('89504e470d0a1a0a0000000d49484452', 'hex');
const MAX_MAP_BYTES = 12 * 1024 * 1024;
const CHECKPOINT_TOLERANCE_PX = 4;
const MAX_DIMENSION = 8192;

async function readRegularFile(directory, name, maxBytes) {
  const path = join(directory, name);
  const entry = await lstat(path);
  if (!entry.isFile() || entry.size > maxBytes || await realpath(path) !== path) throw new Error(`Invalid map file: ${name}`);
  const file = await open(path, 'r');
  try {
    const info = await file.stat();
    if (info.dev !== entry.dev || info.ino !== entry.ino || !info.isFile() || info.size > maxBytes) {
      throw new Error(`Invalid map file: ${name}`);
    }
    const data = Buffer.alloc(info.size);
    let offset = 0;
    while (offset < data.length) {
      const { bytesRead } = await file.read(data, offset, data.length - offset, offset);
      if (bytesRead === 0) throw new Error(`Incomplete map file: ${name}`);
      offset += bytesRead;
    }
    return data;
  } finally {
    await file.close();
  }
}

const worldPoint = p => Number.isFinite(p?.x) && Number.isFinite(p?.y) && Math.abs(p.x) <= 100_000 && Math.abs(p.y) <= 100_000;
const cross = (ax, ay, bx, by) => ax * by - ay * bx;

/** Three fit anchors and independent checkpoints calibrate the particular image identified by its SHA-256. */
export function calibrate(config, width, height) {
  const { anchors, checkpoints } = config;
  if (!Array.isArray(anchors) || anchors.length !== 3 || !Array.isArray(checkpoints) || checkpoints.length < 2 || checkpoints.length > 16) {
    throw new Error('Map needs three anchors and two to sixteen checkpoints');
  }
  const landmarks = [...anchors, ...checkpoints];
  for (const [i, p] of landmarks.entries()) {
    if (!worldPoint(p) || !Number.isFinite(p.px) || !Number.isFinite(p.py) || p.px < 0 || p.px >= width || p.py < 0 || p.py >= height ||
        landmarks.slice(0, i).some(q => Math.hypot(p.x - q.x, p.y - q.y) < 1 || Math.hypot(p.px - q.px, p.py - q.py) < 8)) {
      throw new Error('Map landmarks must be distinct and inside the image');
    }
  }
  const [a, b, c] = anchors;
  const bx = b.x - a.x, by = b.y - a.y;
  const cx = c.x - a.x, cy = c.y - a.y;
  const area = cross(bx, by, cx, cy);
  const imageArea = cross(b.px - a.px, b.py - a.py, c.px - a.px, c.py - a.py);
  if (Math.abs(area) < 0.01 * Math.hypot(bx, by) * Math.hypot(cx, cy) || Math.abs(imageArea) < 0.05 * width * height) {
    throw new Error('Map anchors must span the image and be non-collinear');
  }
  const pixel = ({ x, y }) => {
    const alongB = cross(x - a.x, y - a.y, cx, cy) / area;
    const alongC = cross(bx, by, x - a.x, y - a.y) / area;
    return {
      px: a.px + alongB * (b.px - a.px) + alongC * (c.px - a.px),
      py: a.py + alongB * (b.py - a.py) + alongC * (c.py - a.py),
    };
  };
  for (const p of checkpoints) {
    const actual = pixel(p);
    if (Math.hypot(actual.px - p.px, actual.py - p.py) > CHECKPOINT_TOLERANCE_PX) throw new Error('Map checkpoint differs by more than 4 px');
  }
  return point => {
    if (!worldPoint(point)) return null;
    const { px, py } = pixel(point);
    return px >= 0 && py >= 0 && px < width && py < height ? { u: px / width, v: py / height } : null;
  };
}

function pngDimensions(bytes) {
  if (bytes.length < 45 || !bytes.subarray(0, 16).equals(PNG_HEADER)) throw new Error('Map image must be a PNG');
  let hasData = false;
  for (let start = 8; start + 12 <= bytes.length;) {
    const size = bytes.readUInt32BE(start);
    const end = start + 8 + size;
    if (end + 4 > bytes.length || crc32(bytes.subarray(start + 4, end)) !== bytes.readUInt32BE(end)) break;
    const type = bytes.toString('ascii', start + 4, start + 8);
    if (type === 'IDAT' && size > 0) hasData = true;
    if (type === 'IEND' && size === 0 && hasData && end + 4 === bytes.length) {
      return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
    }
    start = end + 4;
  }
  throw new Error('Map PNG is incomplete or corrupt');
}

export async function loadWorldMap(path) {
  const directory = await realpath(path);
  const configBytes = await readRegularFile(directory, 'calibration.json', 16_384);
  const config = JSON.parse(configBytes.toString('utf8').replace(/^\uFEFF/, ''));
  const declared = text => typeof text === 'string' && text.trim().length > 0 && text.length <= 240;
  if (config?.version !== 1 || !declared(config.source) || !declared(config.rights)) throw new Error('Map provenance and permission are required');
  const bytes = await readRegularFile(directory, 'map.png', MAX_MAP_BYTES);
  const digest = createHash('sha256').update(bytes).digest('hex');
  if (config.sha256 !== digest) throw new Error('Map image does not match the calibrated PNG');
  const { width, height } = pngDimensions(bytes);
  if (width < 32 || height < 32 || width > MAX_DIMENSION || height > MAX_DIMENSION || width * height > 16_000_000) {
    throw new Error('Map dimensions are outside supported limits');
  }
  const project = calibrate(config, width, height);
  const revision = createHash('sha256').update(digest).update(JSON.stringify([config.anchors, config.checkpoints])).digest('hex');
  return { id: `map-${digest}`, revision, width, height, project, bytes };
}

function readBlock(snap) {
  if (!snap) return 'ADAPTER_ABSENT';
  if (!snap.fresh) return 'ADAPTER_STALE';
  if (snap.raw.modLoaded !== true) return 'MOD_NOT_LOADED';
  if (snap.raw.bindingProfile !== 'gtbikev-0804') return 'UNSUPPORTED_VERSION';
  if (snap.raw.isDtOk !== true || snap.raw.modLayer !== 'dataReady') return 'MOD_INACTIVE';
  return null;
}

export function worldReasons(snap, { map, riders = false } = {}) {
  const raw = snap?.raw;
  const block = readBlock(snap);
  return {
    'map.position': !map ? 'ASSET_UNAVAILABLE' : block ??
      (raw.gameControl !== 'inControl' ? 'GAME_NOT_IN_CONTROL' : !worldPoint(raw.p8Map?.position) ? 'CAPABILITY_UNAVAILABLE' : null),
    'riders.read': !riders ? 'PHASE_NOT_AVAILABLE' : block ??
      (typeof raw.p8Riders?.connected !== 'boolean' || !Array.isArray(raw.p8Riders?.riders) ? 'CAPABILITY_UNAVAILABLE' : null),
  };
}

const routeKey = value => Number.isInteger(value) && value > 0 && value <= 65535 && (value & 0x7fff) !== 0;
const course = key => routeKey(key) ? { id: String(key & 0x7fff), reverse: (key & 0x8000) !== 0 } : null;

export function toMap(snap, map) {
  const { position, route } = snap.raw.p8Map;
  const active = course(route?.courseKey);
  const waypoints = Array.isArray(route?.waypoints) ? route.waypoints.slice(0, 512).map(map.project) : [];
  return {
    assetId: map.id, calibrationRevision: map.revision, width: map.width, height: map.height, sequence: snap.raw.sequence,
    position: map.project(position),
    route: active && waypoints.some(Boolean) ? { ...active, waypoints: active.reverse ? waypoints.reverse() : waypoints } : null,
  };
}

const reading = (value, max) => Number.isFinite(value) && value >= 0 && value <= max ? value : null;

export function toRiders(snap) {
  const raw = snap.raw.p8Riders;
  const seen = new Set();
  const riders = [];
  for (const rider of raw.riders.slice(0, 64)) {
    if (!Number.isInteger(rider?.rank) || rider.rank < 1 || rider.rank > 64 || seen.has(rider.rank)) continue;
    seen.add(rider.rank);
    const name = typeof rider.name === 'string' ? rider.name.replace(/[\p{Cc}\p{Cf}]/gu, '').trim().slice(0, 80) || null : null;
    const kind = ['local', 'human', 'bot'].includes(rider.kind) ? rider.kind : 'unknown';
    riders.push({ rank: rider.rank, kind, name, wattsPerKg: reading(rider.wattsPerKg, 100), speedMps: reading(rider.speedMps, 100) });
  }
  return {
    connected: raw.connected, course: course(raw.courseKey),
    busiestRoute: raw.connected ? course(raw.joinCourseKey) : null,
    riders: riders.sort((a, b) => a.rank - b.rank),
  };
}
