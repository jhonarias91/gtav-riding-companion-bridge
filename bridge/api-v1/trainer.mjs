// Native VirtualGearBox observation and request-specific read-back for one shift.
const MODES = ['none', 'manual', 'auto', 'tempManual', 'native'];
const integer = (v, min, max) => Number.isInteger(v) && v >= min && v <= max ? v : null;
export function toTrainer(snap) {
  const raw = snap?.raw, t = raw?.trainer;
  const reasonCode = !snap ? 'ADAPTER_ABSENT' : !snap.fresh ? 'ADAPTER_STALE' : raw.modLoaded === false ? 'MOD_NOT_LOADED'
    : raw.bindingProfile !== 'gtbikev-0804' ? 'UNSUPPORTED_VERSION' : !t ? 'CAPABILITY_UNAVAILABLE' : t.reasonCode ?? null;
  const mode = reasonCode === null && MODES.includes(t.mode) ? t.mode : null;
  const gearCount = reasonCode === null ? integer(t.gearCount, 1, 255) : null;
  const gear = gearCount === null ? null : integer(t.gear, 1, gearCount);
  const ratio = reasonCode === null && Number.isFinite(t.ratio) && t.ratio > 0 && t.ratio <= 100 ? t.ratio : null;
  const enabled = reasonCode === null && typeof t.enabled === 'boolean' ? t.enabled : null;
  const available = reasonCode === null && mode !== null && gear !== null && ratio !== null && enabled !== null;
  const blocked = reasonCode ?? (!available ? 'CAPABILITY_UNAVAILABLE' : raw.isDtOk !== true ? 'MOD_INACTIVE'
    : raw.gameControl !== 'inControl' ? 'GAME_NOT_IN_CONTROL' : enabled !== true ? 'GEARS_DISABLED' : t.shiftReasonCode ?? null);
  const busy = snap?.fresh === true && t?.busy === true;
  return { available, reasonCode: reasonCode ?? (available ? null : 'CAPABILITY_UNAVAILABLE'), enabled, mode, gear, gearCount, ratio,
    canShiftUp: blocked === null && !busy && gear < gearCount, canShiftDown: blocked === null && !busy && gear > 1,
    shiftReasonCode: blocked, busy };
}
export function gearShiftReason(snap, direction) {
  const doc = toTrainer(snap);
  return doc.reasonCode ?? doc.shiftReasonCode ?? (doc.busy ? 'BRIDGE_BUSY' : direction === 'up' ? doc.canShiftUp ? null : 'GEAR_LIMIT' : doc.canShiftDown ? null : 'GEAR_LIMIT');
}
export function gearEvidence(args, base, { snap }) {
  const report = snap?.fresh ? snap.raw.trainer?.command : null;
  if (!report || report.requestId !== base.requestId) return null;
  const gear = integer(report.gear, 1, 255), mode = MODES.includes(report.mode) ? report.mode : null;
  if (report.status === 'failed') return { status: 'failed', observed: { gear, mode }, code: report.reasonCode ?? 'CAPABILITY_UNAVAILABLE' };
  return report.status === 'done' && gear !== null && mode !== null ? { status: 'applied', observed: { gear, mode } } : null;
}
