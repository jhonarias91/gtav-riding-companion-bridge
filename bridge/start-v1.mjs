// Starts the native Android bridge v1. HTTPS by default: with GTBIKE_BRIDGE_TLS_CERT/KEY when set, otherwise with a
// key the bridge creates once and a certificate rebuilt on each start. --http-dev serves plain HTTP for debug builds.
//   node bridge/start-v1.mjs [--port 8788] [--host 0.0.0.0] [--map-directory PATH] [--enable-riders] [--http-dev]
//     [--desktop-status-file PATH]   (used by the PC window: QR, pairing code and a loopback-only desktop token)
//     [--mod-config PATH]            (GTBikeVConfig.ini; default DocumentsRockstar GamesGTA VModSettings)
import { join, dirname } from 'node:path';
import { homedir } from 'node:os';
import { readFile, writeFile, mkdir, rename, unlink } from 'node:fs/promises';
import { randomBytes, X509Certificate } from 'node:crypto';
import { createBridgeV1, BRIDGE_VERSION } from './api-v1/server.mjs';
import { TOKEN_LIFETIME_DAYS } from './api-v1/auth.mjs';
import { loadWorldMap } from './api-v1/world.mjs';
import { lanAddresses } from './security.mjs';
import { keyFingerprint, loadOrCreateBridgeKey, selfSignedCertificate } from './api-v1/auto-tls.mjs';
import { pairingUri, terminalQr, imageQr } from './pairing-qr.mjs';

const options = {};
for (let i = 2; i < process.argv.length; i++) {
  const option = process.argv[i];
  if (option === '--enable-riders' || option === '--http-dev') options[option] = true;
  else if (['--port', '--host', '--map-directory', '--desktop-status-file', '--mod-config'].includes(option) && process.argv[i + 1] && !process.argv[i + 1].startsWith('--')) {
    options[option] = process.argv[++i];
  } else throw Error('Usage: node bridge/start-v1.mjs [--port 8788] [--host 0.0.0.0] [--map-directory PATH] [--enable-riders] [--http-dev]');
}
const port = Number(options['--port'] ?? 8788);
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw Error('The port must be between 1024 and 65535.');
const host = options['--host'] ?? '0.0.0.0';
if (host !== '0.0.0.0' && host !== '127.0.0.1' && !lanAddresses().includes(host)) throw Error('Use 0.0.0.0, 127.0.0.1 or one of this PC\'s private IPv4 addresses.');

const base = join(process.env.LOCALAPPDATA ?? homedir(), 'GTBikeVCompanion');
// GTBike V's own INI (P15 game options). The PC window passes the real Documents folder; otherwise use the folder the
// adapter reported for Activities (a sibling of ModSettings), then the usual Documents location.
async function modConfigPath() {
  if (options['--mod-config']) return options['--mod-config'];
  try {
    const { activitiesPath } = JSON.parse(await readFile(join(base, 'v1', 'paths.json'), 'utf8'));
    if (typeof activitiesPath === 'string' && activitiesPath.length > 3) return join(dirname(activitiesPath.replace(/[\\/]+$/, '')), 'ModSettings', 'GTBikeVConfig.ini');
  } catch { /* adapter has not run yet */ }
  return join(homedir(), 'Documents', 'Rockstar Games', 'GTA V', 'ModSettings', 'GTBikeVConfig.ini');
}
const world = { riders: options['--enable-riders'] === true };
if (options['--map-directory']) {
  try {
    world.map = await loadWorldMap(options['--map-directory']);
  } catch (error) {
    console.warn(`Game map disabled. ${error.code ?? error.message}`);
  }
}
const env = process.env;
if (Boolean(env.GTBIKE_BRIDGE_TLS_CERT) !== Boolean(env.GTBIKE_BRIDGE_TLS_KEY)) throw new Error('Set both GTBIKE_BRIDGE_TLS_CERT and GTBIKE_BRIDGE_TLS_KEY, or neither.');
const hosts = host === '0.0.0.0' ? lanAddresses() : [host];
let tls = null;
if (env.GTBIKE_BRIDGE_TLS_CERT) {
  tls = { cert: await readFile(env.GTBIKE_BRIDGE_TLS_CERT, 'utf8'), key: await readFile(env.GTBIKE_BRIDGE_TLS_KEY, 'utf8') };
} else if (!options['--http-dev']) {
  const { keyPem, file, created } = await loadOrCreateBridgeKey(join(base, 'tls'));
  if (created) console.log(`Created the bridge key: ${file}. Keep it: the app trusts this key.`);
  tls = { key: keyPem, cert: selfSignedCertificate(keyPem, [...hosts, '127.0.0.1']) };
}
const day = iso => iso.slice(0, 10);
let bridge;
const desktopStatusFile = options['--desktop-status-file'];
// The PC window authenticates with this per-run secret. It lives only in memory and in the status file under the
// user's own %LOCALAPPDATA%, and the bridge accepts it only from 127.0.0.1.
const desktopToken = desktopStatusFile ? randomBytes(32).toString('base64url') : null;
const certSha256 = () => (tls ? new X509Certificate(tls.cert).fingerprint256.replace(/:/g, '').toLowerCase() : null);
let desktopWrite = Promise.resolve();
const connectionUri = () => hosts.length ? pairingUri({ bridgeId: bridge.bridgeId, hosts, port,
  fingerprint: tls ? keyFingerprint(tls.cert) : null, pairingKey: bridge.pairing.currentPairingKey() }) : null;
const updateDesktopStatus = () => {
  if (!desktopStatusFile || !bridge) return;
  const uri = connectionUri();
  const data = JSON.stringify({ bridgeId: bridge.bridgeId, hosts, port,
    fingerprint: tls ? keyFingerprint(tls.cert) : null,
    pin: bridge.pairing.currentPin(), pinExpiresAtUtc: new Date(bridge.pairing.pinExpiresAt).toISOString(),
    qrDataUrl: uri ? imageQr(uri) : null, paired: bridge.pairing.activeClients(),
    bridgeVersion: BRIDGE_VERSION, transport: tls ? 'https' : 'http', desktopToken, certSha256: certSha256() });
  desktopWrite = desktopWrite.then(async () => {
    await mkdir(join(desktopStatusFile, '..'), { recursive: true });
    const temporary = `${desktopStatusFile}.${process.pid}.tmp`;
    await writeFile(temporary, data, { mode: 0o600 });
    await rename(temporary, desktopStatusFile);
  }).catch(error => console.warn(`Desktop status unavailable: ${error.message}`));
};
// The QR has the address, port, identity, key fingerprint and a one-time pairing key: scanning it pairs the phone.
const showQr = () => {
  if (!hosts.length) return;
  const uri = connectionUri();
  console.log('\nIn the app, tap Scan QR code and point the camera here:\n');
  console.log(terminalQr(uri));
};
const showPin = () => console.log(`\nOr, if you type the address in the app, use this pairing code: ${bridge.pairing.currentPin()}  (expires in 5 min)`);
const hint = () => console.log('\nPress Enter to show the current QR again, plus a code for typing the address by hand. Ctrl+C to stop.');
// A paired phone came back after its pairing expired: show a fresh QR right away so the user can scan it.
let lastExpiredNotice = 0;
const onPairingExpired = ({ clientName, expiresAtUtc }) => {
  if (Date.now() - lastExpiredNotice < 60_000) return;
  lastExpiredNotice = Date.now();
  console.log(`\n${clientName}: pairing expired on ${day(expiresAtUtc)} (pairings last ${TOKEN_LIFETIME_DAYS} days). Scan the QR again in the app.`);
  showQr();
  updateDesktopStatus();
  hint();
};
const onPaired = ({ clientName, expiresAtUtc }) => {
  console.log(`\nPaired: ${clientName}  (until ${day(expiresAtUtc)}). The QR above is used up; press Enter for a new one.`);
  updateDesktopStatus();
};
bridge = await createBridgeV1({ adapterDirectory: join(base, 'v1'), stateDirectory: join(base, 'bridge-v1'), world,
  tls, desktopToken, modConfig: { path: await modConfigPath() }, onPairingExpired, onPaired });
bridge.server.on('error', e => {
  console.error(e.code === 'EADDRINUSE' ? `Port ${port} is already in use. Choose another with --port.` : e.message);
  process.exitCode = 1;
});
bridge.server.listen(port, host, () => {
  console.log(`\nGTAV Riding Companion · Android bridge v1 (${BRIDGE_VERSION}, ${tls ? 'HTTPS' : 'development HTTP'})\n`);
  console.log(`Bridge identity: ${bridge.bridgeId}   <- check that the phone shows the same one`);
  if (tls) console.log(`Bridge key fingerprint: ${keyFingerprint(tls.cert)}`);
  for (const address of hosts) console.log(`Address for the phone: ${address}  port ${port}`);
  if (!hosts.length) console.log('No private network address found: connect the PC to your home network and restart the bridge.');
  console.log(`Adapter data: ${join(base, 'v1')}`);
  for (const p of bridge.pairing.activeClients()) console.log(`Paired phone: ${p.clientName}  (until ${day(p.expiresAtUtc)})`);
  showQr();
  updateDesktopStatus();
  hint();
  process.stdin.on('data', () => { showQr(); showPin(); updateDesktopStatus(); hint(); });
  if (desktopStatusFile) setInterval(updateDesktopStatus, 30_000);
});
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, async () => {
  await bridge.close();
  await desktopWrite;
  if (desktopStatusFile) await unlink(desktopStatusFile).catch(() => {});
  process.exit(0);
});
