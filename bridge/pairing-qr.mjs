// Connection QR for the Android app: address(es), port, bridge identity, pinned key fingerprint and the one-time
// pairing key. Scanning it pairs the phone without typing anything; the key changes after every pairing.
import qrcode from 'qrcode-generator';

/** gtav-riding-companion://pair?v=1&id=…&h=ip1,ip2&p=8788&k=<sha256 hex>&s=<pairing key> (k omitted for development HTTP). */
export function pairingUri({ bridgeId, hosts, port, fingerprint, pairingKey }) {
  if (!/^[A-Za-z0-9-]{1,64}$/.test(bridgeId)) throw new Error('Invalid bridge identity');
  if (!hosts.length || hosts.some(h => !/^\d{1,3}(\.\d{1,3}){3}$/.test(h))) throw new Error('Invalid host list');
  if (fingerprint != null && !/^[a-f0-9]{64}$/.test(fingerprint)) throw new Error('Invalid key fingerprint');
  if (!/^[A-Za-z0-9_-]{22}$/.test(pairingKey ?? '')) throw new Error('Invalid pairing key');
  const params = [`v=1`, `id=${bridgeId}`, `h=${hosts.join(',')}`, `p=${port}`];
  if (fingerprint) params.push(`k=${fingerprint}`);
  params.push(`s=${pairingKey}`);
  return `gtav-riding-companion://pair?${params.join('&')}`;
}

/**
 * The QR as terminal text: two modules per character (upper/lower half blocks), black on an explicit white
 * background so it scans the same in dark and light terminal themes.
 */
export function terminalQr(text) {
  const qr = qrcode(0, 'M');
  qr.addData(text, 'Byte');
  qr.make();
  const size = qr.getModuleCount();
  const quiet = 4; // the white margin the QR standard asks for; the terminal around it may be dark
  const dark = (r, c) => r >= 0 && c >= 0 && r < size && c < size && qr.isDark(r, c);
  const lines = [];
  for (let r = -quiet; r < size + quiet; r += 2) {
    let line = '';
    for (let c = -quiet; c < size + quiet; c++) {
      const top = dark(r, c);
      const bottom = dark(r + 1, c);
      line += top && bottom ? '█' : top ? '▀' : bottom ? '▄' : ' ';
    }
    lines.push(`\x1b[30;47m${line}\x1b[0m`);
  }
  return lines.join('\n');
}
