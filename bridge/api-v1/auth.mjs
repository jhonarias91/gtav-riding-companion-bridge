// Native pairing for the Android client (CONTRATO_PUENTE_V1.md §2). No browser Origin is required or faked:
// v1 is a separate surface with its own port, its own tokens and no access to the legacy /api/* routes.
// PIN: 6 digits, 5 min lifetime, rotated after every successful pairing; 5 attempts per IP per minute and
// 20 per minute for the whole bridge. Tokens: 256-bit random, only their SHA-256 is stored, 30 days, revocable.
// An expired token never authorizes anything again; its hash is kept for 90 days only so the console can tell the
// user which phone needs a new code.
// QR pairing key: 128-bit random, shown only inside the console QR, valid until a phone uses it (or the bridge
// restarts). It is long enough that guessing is not possible, so unlike the PIN it does not need to expire quickly.
import { createHash, randomBytes, randomInt, timingSafeEqual } from 'node:crypto';
import { readFile, writeFile, rename, mkdir } from 'node:fs/promises';
import { join } from 'node:path';

const PIN_LIFETIME_MS = 5 * 60_000;
export const TOKEN_LIFETIME_DAYS = 30;
const TOKEN_LIFETIME_MS = TOKEN_LIFETIME_DAYS * 24 * 60 * 60_000;
const EXPIRED_RETENTION_MS = 90 * 24 * 60 * 60_000;
const WINDOW_MS = 60_000;
const PER_IP = 5;
const GLOBAL = 20;
const MAX_TOKENS = 16;
const MAX_EXPIRED = 16;

const sha256 = value => createHash('sha256').update(value).digest('hex');
const sameSecret = (a, b) => {
  const x = Buffer.from(String(a));
  const y = Buffer.from(String(b));
  return x.length === y.length && timingSafeEqual(x, y);
};

export class NativePairing {
  constructor({ stateDirectory, now = () => Date.now(), pin = null } = {}) {
    this.file = stateDirectory ? join(stateDirectory, 'tokens.json') : null;
    this.now = now;
    this.fixedPin = pin; // Tests only.
    this.tokens = new Map(); // hash -> { clientName, expiresAt }
    this.attempts = new Map(); // ip -> { count, at }
    this.global = { count: 0, at: 0 };
    this.rotatePin();
    this.rotatePairingKey();
  }

  async load() {
    if (!this.file) return;
    try {
      const saved = JSON.parse(await readFile(this.file, 'utf8'));
      for (const t of saved.tokens ?? []) {
        if (typeof t.hash === 'string' && /^[a-f0-9]{64}$/.test(t.hash) && Number.isFinite(t.expiresAt)) {
          this.tokens.set(t.hash, { clientName: String(t.clientName ?? '').slice(0, 40), expiresAt: t.expiresAt });
        }
      }
    } catch { /* No saved tokens: phones pair again. */ }
    this.prune();
  }

  /** Drops expired hashes past their retention, and the oldest ones beyond MAX_EXPIRED. */
  prune() {
    const now = this.now();
    const expired = [...this.tokens].filter(([, v]) => v.expiresAt <= now).sort((a, b) => b[1].expiresAt - a[1].expiresAt);
    expired.forEach(([hash, v], i) => { if (i >= MAX_EXPIRED || now - v.expiresAt > EXPIRED_RETENTION_MS) this.tokens.delete(hash); });
  }

  async persist() {
    if (!this.file) return;
    const temp = `${this.file}.${randomBytes(4).toString('hex')}.tmp`;
    const tokens = [...this.tokens].map(([hash, v]) => ({ hash, ...v }));
    await mkdir(join(this.file, '..'), { recursive: true });
    await writeFile(temp, JSON.stringify({ tokens }), { mode: 0o600 });
    await rename(temp, this.file);
  }

  rotatePin() {
    this.pin = this.fixedPin ?? String(randomInt(100000, 1000000));
    this.pinExpiresAt = this.now() + PIN_LIFETIME_MS;
  }

  rotatePairingKey() {
    this.pairingKey = randomBytes(16).toString('base64url');
  }

  /** Pairing key for the console QR. */
  currentPairingKey() {
    return this.pairingKey;
  }

  /** Current PIN for the PC console; rotates when expired. */
  currentPin() {
    if (this.now() >= this.pinExpiresAt) this.rotatePin();
    return this.pin;
  }

  /**
   * Pairs with the six-digit PIN, or with the QR pairing key when [pairingKey] is given.
   * Returns { token, expiresAtUtc } or { error, status, retryAfterS }.
   */
  async pair(ip, pin, clientName, pairingKey = null) {
    const now = this.now();
    if (now - this.global.at > WINDOW_MS) this.global = { count: 0, at: now };
    const entry = this.attempts.get(ip);
    const current = entry && now - entry.at <= WINDOW_MS ? entry : { count: 0, at: now };
    if (current.count >= PER_IP || this.global.count >= GLOBAL) {
      return { error: 'RATE_LIMITED', status: 429, retryAfterS: Math.ceil((WINDOW_MS - (now - current.at)) / 1000) || 1 };
    }
    current.count++;
    this.global.count++;
    this.attempts.set(ip, current);
    if (pairingKey !== null) {
      if (typeof pairingKey !== 'string' || !/^[A-Za-z0-9_-]{22}$/.test(pairingKey) || !sameSecret(pairingKey, this.pairingKey)) {
        return { error: 'PAIRING_PIN_INVALID', status: 401 };
      }
    } else {
      if (typeof pin !== 'string' || !/^[0-9]{6}$/.test(pin)) return { error: 'PAIRING_PIN_INVALID', status: 401 };
      if (now >= this.pinExpiresAt) {
        this.rotatePin();
        return { error: 'PAIRING_PIN_EXPIRED', status: 401 };
      }
      if (!sameSecret(pin, this.pin)) return { error: 'PAIRING_PIN_INVALID', status: 401 };
    }
    this.attempts.delete(ip);
    this.rotatePin(); // One code, one phone: the PIN and the QR key both change after any pairing.
    this.rotatePairingKey();
    this.prune();
    const active = [...this.tokens].filter(([, v]) => v.expiresAt > now);
    if (active.length >= MAX_TOKENS) this.tokens.delete(active.sort((a, b) => a[1].expiresAt - b[1].expiresAt)[0][0]);
    const token = randomBytes(32).toString('base64url');
    const expiresAt = now + TOKEN_LIFETIME_MS;
    this.tokens.set(sha256(token), { clientName: String(clientName ?? 'Android').slice(0, 40), expiresAt });
    await this.persist();
    return { token, expiresAtUtc: new Date(expiresAt).toISOString() };
  }

  static hashOf(header) {
    const match = /^Bearer ([A-Za-z0-9_-]{43})$/.exec(header ?? '');
    return match ? sha256(match[1]) : null;
  }

  /** Bearer header -> token hash, or null. */
  verify(header) {
    const hash = NativePairing.hashOf(header);
    return hash && this.isActive(hash) ? hash : null;
  }

  isActive(hash) {
    const entry = this.tokens.get(hash);
    return Boolean(entry) && entry.expiresAt > this.now();
  }

  /** Bearer header of a token that was valid and has expired -> { clientName, expiresAtUtc }, otherwise null. */
  expired(header) {
    const hash = NativePairing.hashOf(header);
    const entry = hash ? this.tokens.get(hash) : null;
    if (!entry || entry.expiresAt > this.now() || this.now() - entry.expiresAt > EXPIRED_RETENTION_MS) return null;
    return { clientName: entry.clientName, expiresAtUtc: new Date(entry.expiresAt).toISOString() };
  }

  /** Phones with a valid token, soonest expiry first. For the PC console only. */
  activeClients() {
    const now = this.now();
    return [...this.tokens.values()].filter(v => v.expiresAt > now).sort((a, b) => a.expiresAt - b.expiresAt)
      .map(v => ({ clientName: v.clientName, expiresAtUtc: new Date(v.expiresAt).toISOString() }));
  }

  async revoke(hash) {
    if (this.tokens.delete(hash)) await this.persist();
  }
}
