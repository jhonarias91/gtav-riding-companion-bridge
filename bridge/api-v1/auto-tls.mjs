// Automatic HTTPS for the Android bridge. The private key is created once and kept; its SHA-256 (SPKI) is the
// fingerprint the phone pins, so it never changes. The certificate is rebuilt in memory on every start with the
// PC's current LAN addresses in its SAN, because the app checks the address as well as the pinned key.
import { createHash, createPrivateKey, createPublicKey, generateKeyPairSync, randomBytes, sign, X509Certificate } from 'node:crypto';
import { mkdir, readFile, writeFile, rename } from 'node:fs/promises';
import { join } from 'node:path';

const DAY = 24 * 60 * 60_000;
const CERT_LIFETIME_MS = 397 * DAY;

// Minimal DER encoder: just what an X.509 v3 certificate needs.
const len = n => (n < 0x80 ? Buffer.from([n]) : (() => {
  const bytes = [];
  for (let v = n; v > 0; v >>= 8) bytes.unshift(v & 0xff);
  return Buffer.from([0x80 | bytes.length, ...bytes]);
})());
const tlv = (tag, ...parts) => { const body = Buffer.concat(parts); return Buffer.concat([Buffer.from([tag]), len(body.length), body]); };
const seq = (...parts) => tlv(0x30, ...parts);
const set = (...parts) => tlv(0x31, ...parts);
const int = bytes => tlv(0x02, bytes[0] & 0x80 ? Buffer.concat([Buffer.from([0]), bytes]) : bytes);
const oid = dotted => {
  const [a, b, ...rest] = dotted.split('.').map(Number);
  const out = [40 * a + b];
  for (const v of rest) {
    const chunk = [v & 0x7f];
    for (let x = v >> 7; x > 0; x >>= 7) chunk.unshift(0x80 | (x & 0x7f));
    out.push(...chunk);
  }
  return tlv(0x06, Buffer.from(out));
};
const utf8 = text => tlv(0x0c, Buffer.from(text, 'utf8'));
const time = ms => {
  const iso = new Date(ms).toISOString(); // YYYY-MM-DDTHH:MM:SS.sssZ
  const digits = iso.slice(0, 19).replace(/[-T:]/g, '');
  const year = Number(iso.slice(0, 4));
  return year < 2050 ? tlv(0x17, Buffer.from(`${digits.slice(2)}Z`)) : tlv(0x18, Buffer.from(`${digits}Z`));
};
const bitString = bytes => tlv(0x03, Buffer.from([0]), bytes);
const octets = bytes => tlv(0x04, bytes);
const explicit = (n, ...parts) => tlv(0xa0 + n, ...parts);
const NULL = Buffer.from([0x05, 0x00]);

const name = cn => seq(set(seq(oid('2.5.4.3'), utf8(cn))));
const ipBytes = ip => Buffer.from(ip.split('.').map(Number));

function signatureAlgorithm(key) {
  if (key.asymmetricKeyType === 'ec') return seq(oid('1.2.840.10045.4.3.2')); // ecdsa-with-SHA256
  if (key.asymmetricKeyType === 'rsa') return seq(oid('1.2.840.113549.1.1.11'), NULL); // sha256WithRSAEncryption
  throw new Error(`Unsupported bridge key type: ${key.asymmetricKeyType}`);
}

/** Self-signed certificate (PEM) for [keyPem], valid for the given IPv4 addresses. */
export function selfSignedCertificate(keyPem, ipAddresses, now = Date.now()) {
  const key = createPrivateKey(keyPem);
  const spki = createPublicKey(key).export({ type: 'spki', format: 'der' });
  const algorithm = signatureAlgorithm(key);
  // Positive and minimal: a leading 0x00 byte would be non-minimal DER, which OpenSSL rejects.
  const serial = randomBytes(16);
  serial[0] = (serial[0] & 0x7f) | 0x40;
  const subject = name('GTAV Riding Companion bridge');
  const san = seq(...[...new Set(ipAddresses)].map(ip => tlv(0x87, ipBytes(ip)))); // iPAddress [7]
  const extensions = explicit(3, seq(
    seq(oid('2.5.29.19'), octets(seq())), // basicConstraints: not a CA
    seq(oid('2.5.29.17'), octets(san)), // subjectAltName
  ));
  const tbs = seq(
    explicit(0, int(Buffer.from([2]))), // v3
    int(serial),
    algorithm,
    subject,
    seq(time(now - DAY), time(now + CERT_LIFETIME_MS)),
    subject,
    spki,
    extensions,
  );
  const der = seq(tbs, algorithm, bitString(sign('sha256', tbs, key)));
  return `-----BEGIN CERTIFICATE-----\n${der.toString('base64').match(/.{1,64}/g).join('\n')}\n-----END CERTIFICATE-----\n`;
}

/** SHA-256 of the certificate's public key (SPKI), as the app's Bridge key fingerprint field expects it. */
export function keyFingerprint(certPem) {
  const spki = new X509Certificate(certPem).publicKey.export({ type: 'spki', format: 'der' });
  return createHash('sha256').update(spki).digest('hex');
}

/** Reads the bridge's private key from [directory], creating it the first time. */
export async function loadOrCreateBridgeKey(directory) {
  const file = join(directory, 'bridge-key.pem');
  try {
    return { keyPem: await readFile(file, 'utf8'), file, created: false };
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;
  }
  const { privateKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  const keyPem = privateKey.export({ type: 'pkcs8', format: 'pem' });
  await mkdir(directory, { recursive: true });
  const temp = `${file}.${randomBytes(4).toString('hex')}.tmp`;
  await writeFile(temp, keyPem, { mode: 0o600, flag: 'wx' });
  await rename(temp, file);
  return { keyPem, file, created: true };
}
