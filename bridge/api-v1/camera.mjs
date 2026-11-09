// GTA gameplay cameras for the OnBike context, shared by Android and PWA.
export const CAMERA_MODES = ['near', 'medium', 'far', 'firstPerson', 'cinematic'];
const number = (v, min, max) => typeof v === 'number' && Number.isFinite(v) && v >= min && v <= max ? Math.round(v * 10) / 10 : null;

export function toCamera(snap) {
  const raw = snap?.raw;
  const camera = raw?.camera;
  const reasonCode = !snap ? 'ADAPTER_ABSENT' : !snap.fresh ? 'ADAPTER_STALE'
    : raw.modLoaded === false ? 'MOD_NOT_LOADED' : raw.isDtOk !== true ? 'MOD_INACTIVE'
      : !camera ? 'CAPABILITY_UNAVAILABLE' : camera.reasonCode ?? null;
  const modes = reasonCode === null && Array.isArray(camera?.modes) ? CAMERA_MODES.filter(id => camera.modes.includes(id)) : [];
  const currentMode = modes.includes(camera?.currentMode) ? camera.currentMode : null;
  const headingDeg = reasonCode === null ? number(camera?.headingDeg, -180, 180) : null;
  const pitchDeg = reasonCode === null ? number(camera?.pitchDeg, -90, 90) : null;
  const available = reasonCode === null && currentMode !== null;
  const lookReasonCode = reasonCode ?? camera?.lookReasonCode ?? (!['near', 'medium', 'far'].includes(currentMode) || headingDeg === null || pitchDeg === null ? 'CAPABILITY_UNAVAILABLE' : null);
  return { available, reasonCode: reasonCode ?? (available ? null : 'CAPABILITY_UNAVAILABLE'), modes, currentMode,
    headingDeg, pitchDeg, canLook: available && lookReasonCode === null, lookReasonCode,
    busy: snap?.fresh === true && camera?.busy === true };
}

export function cameraEvidence(args, base, { snap }) {
  const result = snap?.fresh ? snap.raw.camera?.command : null;
  if (!result || result.requestId !== base.requestId) return null;
  const mode = CAMERA_MODES.includes(result.mode) ? result.mode : null;
  const observed = { mode, headingDeg: number(result.headingDeg, -180, 180), pitchDeg: number(result.pitchDeg, -90, 90) };
  if (result.status === 'failed') return { status: 'failed', observed, code: result.reasonCode ?? 'CAPABILITY_UNAVAILABLE' };
  if (result.status !== 'done' || mode === null) return null;
  if ('mode' in args && mode !== args.mode) return null;
  if ('headingDeg' in args && (observed.headingDeg === null || observed.pitchDeg === null
    || Math.abs(((args.headingDeg - observed.headingDeg + 540) % 360) - 180) > 3 || Math.abs(args.pitchDeg - observed.pitchDeg) > 3)) return null;
  return { status: 'applied', observed };
}
