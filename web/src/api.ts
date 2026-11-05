import { Role } from './data';

const BASE = '/api';
// 시연용 키다. 실제 인증이 아니며, 운영 환경에서는 쓰지 않는다.
const OP_KEY: string = (import.meta as unknown as { env?: Record<string, string> }).env?.VITE_OPERATOR_KEY ?? 'dev-operator-key';
const WIRE: Record<Role, string> = { resident: 'resident', shop: 'store', courier: 'courier', demo: 'operator' };

export interface Auth { role: Role; userId: number }

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string) { super(message); }
}

export const uuid = () =>
  (globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`);

function headers(a: Auth, extra: Record<string, string> = {}): Record<string, string> {
  const h: Record<string, string> = { 'X-Demo-Role': WIRE[a.role], 'X-Demo-User-Id': String(a.userId), ...extra };
  if (a.role === 'demo') h['X-Operator-Key'] = OP_KEY;
  return h;
}

async function send(a: Auth, method: string, path: string, body?: unknown, idem?: string, signal?: AbortSignal): Promise<Response> {
  const h = headers(a, body !== undefined ? { 'Content-Type': 'application/json' } : {});
  if (idem) h['Idempotency-Key'] = idem;
  try {
    return await fetch(BASE + path, { method, headers: h, body: body === undefined ? undefined : JSON.stringify(body), signal });
  } catch (e) {
    if (signal?.aborted) throw e;
    throw new ApiError(0, 'NETWORK', '서버에 연결할 수 없어요. 잠시 뒤 다시 시도해 주세요.');
  }
}

async function fail(res: Response): Promise<never> {
  let code = 'ERROR';
  let message = '요청을 처리하지 못했어요.';
  try { const j = await res.json(); code = j.code ?? code; message = j.message ?? message; } catch { /* 본문 없음 */ }
  throw new ApiError(res.status, code, message);
}

export async function call<T = unknown>(a: Auth, method: string, path: string, body?: unknown, idem?: string): Promise<T> {
  const res = await send(a, method, path, body, idem);
  if (!res.ok) await fail(res);
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

// EventSource는 헤더를 보낼 수 없어 fetch 스트림으로 SSE를 읽는다.
export async function streamRun(
  a: Auth, runId: number, onEvent: (type: string, data: any) => void, signal: AbortSignal,
): Promise<void> {
  const res = await send(a, 'GET', `/agent-runs/${runId}/events`, undefined, undefined, signal);
  if (!res.ok || !res.body) await fail(res);
  const reader = res.body!.getReader();
  const dec = new TextDecoder();
  let buf = '';
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buf += dec.decode(value, { stream: true });
    let i: number;
    while ((i = buf.indexOf('\n\n')) >= 0) {
      const block = buf.slice(0, i);
      buf = buf.slice(i + 2);
      let type = 'message';
      let data = '';
      for (const line of block.split('\n')) {
        if (line.startsWith('event:')) type = line.slice(6).trim();
        else if (line.startsWith('data:')) data += line.slice(5).trim();
      }
      if (data) onEvent(type, JSON.parse(data));
    }
  }
}
