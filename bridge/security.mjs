import { createHash, randomBytes, randomInt, timingSafeEqual } from 'node:crypto';
import { networkInterfaces } from 'node:os';
export function privateAddress(address='') {
  const ip=address.replace(/^::ffff:/,'');
  if(ip==='::1')return true;
  const parts=ip.split('.').map(Number);
  if(parts.length!==4||parts.some(n=>!Number.isInteger(n)||n<0||n>255))return false;
  return parts[0]===127||parts[0]===10||parts[0]===192&&parts[1]===168||parts[0]===172&&parts[1]>=16&&parts[1]<=31;
}
export function lanAddresses(){return [...new Set(Object.values(networkInterfaces()).flat().filter(n=>n?.family==='IPv4'&&!n.internal&&privateAddress(n.address)).map(n=>n.address))];}
export function hash(value){return createHash('sha256').update(value).digest('hex');}
export function sameSecret(a,b){const x=Buffer.from(String(a)),y=Buffer.from(String(b));return x.length===y.length&&timingSafeEqual(x,y);}
export class Pairing {
  constructor({pin=String(randomInt(100000,1000000)),now=()=>Date.now()}={}){this.pin=pin;this.now=now;this.sessions=new Map();this.attempts=new Map();}
  pair(ip,pin){const now=this.now();for(const [key,value]of this.attempts)if(now-value.at>60000)this.attempts.delete(key);for(const [key,value]of this.sessions)if(value<now)this.sessions.delete(key);const entry=this.attempts.get(ip)??{count:0,at:now};if(entry.count>=5&&now-entry.at<60000)return {error:'Demasiados intentos. Espera un minuto.',status:429};entry.count++;this.attempts.set(ip,entry);if(!sameSecret(this.pin,pin))return {error:'Código incorrecto.',status:401};if(this.sessions.size>=100)return {error:'Límite de sesiones alcanzado. Reinicia el puente.',status:429};const token=randomBytes(32).toString('base64url');this.sessions.set(hash(token),now+864e5);return {token,expiresAt:new Date(now+864e5).toISOString()};}
  valid(token){const key=hash(token||'');const expires=this.sessions.get(key);if(!expires||expires<this.now()){this.sessions.delete(key);return false;}return true;}
  revoke(token){this.sessions.delete(hash(token||''));}
}
