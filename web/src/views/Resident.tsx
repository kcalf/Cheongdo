import { useEffect, useRef, useState } from 'react';
import { useApp } from '../store';
import { Loader } from '../Loader';
import { ApiError, call, streamRun } from '../api';

const EMOJI: Record<number, string> = { 1: '🧈', 2: '🌿', 3: '🥚', 4: '🥛', 5: '🍜', 6: '🌾', 7: '🧅', 8: '🌱' };
import { OrderLine, Order, STORES, RESIDENTS, ESTIMATE_FEE, lowestStore, nextBatch, won } from '../data';

type Step = 'input' | 'review' | 'pay' | 'done';
const EXAMPLES = ['두부 2모랑 대파 한 단', '계란 한 판, 우유 2개, 라면 3봉', '쌀 한 포, 양파 2개, 사과 3개'];
const STATUS_TEXT: Record<Order['status'], string> = {
  Paid: '결제 완료', Confirmed: '배치 확정', Preparing: '상점 준비', Delivering: '배송 중', Delivered: '완료', Refunded: '모의 환불',
};
const TIMELINE: Order['status'][] = ['Paid', 'Confirmed', 'Preparing', 'Delivering', 'Delivered'];

export function Resident() {
  const app = useApp();
  const { products, clock, aiOn, residentId } = app;
  const [tab, setTab] = useState<'order' | 'history'>('order');
  const [mode, setMode] = useState<'ai' | 'manual'>('ai');
  const [step, setStep] = useState<Step>('input');
  const [lines, setLines] = useState<OrderLine[]>([]);
  const [aiMeta, setAiMeta] = useState<Record<number, string>>({});
  const [confirmed, setConfirmed] = useState(false);

  const addLine = (productId: number, qty: number, ai?: string) => {
    const p = products.find((x) => x.id === productId)!;
    const sid = lowestStore(p, qty) ?? 1;
    setLines((ls) => {
      const found = ls.find((l) => l.productId === productId);
      if (found) return ls.map((l) => (l.productId === productId ? { ...l, qty: l.qty + qty } : l));
      return [...ls, { productId, qty, storeId: sid, preferred: false }];
    });
    if (ai) setAiMeta((m) => ({ ...m, [productId]: ai }));
  };

  const total = lines.reduce((s, l) => s + l.qty * products.find((p) => p.id === l.productId)!.prices[l.storeId], 0);
  const estimate = total + ESTIMATE_FEE;
  const nb = nextBatch(clock);

  return (
    <section>
      <div className="seg" role="tablist">
        <button role="tab" aria-selected={tab === 'order'} onClick={() => setTab('order')}>주문하기</button>
        <button role="tab" aria-selected={tab === 'history'} onClick={() => setTab('history')}>주문 내역</button>
      </div>
      <label className="who">
        주민 선택
        <select value={residentId} onChange={(e) => { app.setResidentId(Number(e.target.value)); }}>
          {RESIDENTS.map((r) => <option key={r.id} value={r.id}>{r.name} ({r.zone})</option>)}
        </select>
      </label>

      {tab === 'history' && <History />}

      {tab === 'order' && step === 'input' && (
        <div className="hero">
          <h2>오늘은 뭘 같이 장볼까요?</h2>
          <p>이웃과 함께 주문하면 배송비가 줄어요.</p>
          <span className="batch-pill">다음 확정 {nb.nextDay ? '내일 ' : ''}{nb.slot} · 마감 {nb.cutoff}</span>
        </div>
      )}

      {tab === 'order' && step === 'input' && (
        <div className="layout">
          <div>
            <div className="seg small">
              <button aria-selected={mode === 'ai'} onClick={() => setMode('ai')}>AI로 주문</button>
              <button aria-selected={mode === 'manual'} onClick={() => setMode('manual')}>직접 검색</button>
            </div>
            {mode === 'ai'
              ? <AiOrder aiOn={aiOn} onAdd={addLine} onManual={() => setMode('manual')} />
              : <Manual onAdd={(id) => addLine(id, 1)} />}
            <p className="hint">실제 개인정보를 입력하지 마세요.</p>
          </div>
          <Cart
            lines={lines} setLines={setLines} aiMeta={aiMeta}
            total={total} estimate={estimate}
            action={<div className="cart-action"><button className="btn primary block" disabled={!lines.length} onClick={() => { setConfirmed(false); setStep('review'); }}>주문 확인하기</button></div>}
          />
        </div>
      )}

      {tab === 'order' && step === 'input' && lines.length > 0 && (
        <div className="cta-bar">
          <div><small>추정 금액 (예상치)</small><b>{won(estimate)}</b></div>
          <button className="btn primary" onClick={() => { setConfirmed(false); setStep('review'); }}>주문 확인하기</button>
        </div>
      )}

      {tab === 'order' && step === 'review' && (
        <div className="card">
          <h2>주문을 확인해 주세요</h2>
          <p>품목, 수량, 상점을 다시 한 번 확인합니다. 확인하지 않으면 결제로 넘어갈 수 없어요.</p>
          <Cart lines={lines} setLines={setLines} aiMeta={aiMeta} total={total} estimate={estimate} embedded />
          <label className="check">
            <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} />
            위 내용이 맞습니다. (AI 제안 포함)
          </label>
          <div className="row">
            <button className="btn" onClick={() => setStep('input')}>수정하기</button>
            <button className="btn primary" disabled={!confirmed} onClick={() => setStep('pay')}>결제로 이동</button>
          </div>
        </div>
      )}

      {tab === 'order' && step === 'pay' && (
        <Pay
          estimate={estimate} total={total} nb={nb}
          onBack={() => setStep('review')}
          onPay={async (fail) => {
            const err = await app.placeOrder(lines, fail);
            if (!err) { setLines([]); setAiMeta({}); setStep('done'); }
            return err;
          }}
        />
      )}

      {tab === 'order' && step === 'done' && (
        <div className="card center">
          <h2>결제 완료</h2>
          <p>{nb.nextDay ? '내일 ' : ''}{nb.slot} 배치에서 확정돼요. 마감은 {nb.cutoff}입니다.</p>
          <button className="btn primary" onClick={() => { setStep('input'); setTab('history'); }}>주문 내역 보기</button>
        </div>
      )}
    </section>
  );
}

interface Cand { text: string; productId?: number; qty: number; status: 'ok' | 'ambiguous' | 'notfound'; question?: string }

function AiOrder({ aiOn, onAdd, onManual }: { aiOn: boolean; onAdd: (id: number, qty: number, note: string) => void; onManual: () => void }) {
  const { products, addTrace, auth } = useApp();
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [log, setLog] = useState<string[]>([]);
  const [error, setError] = useState('');
  const [result, setResult] = useState<Cand[] | null>(null);
  const abort = useRef<AbortController>();
  useEffect(() => () => abort.current?.abort(), []);

  const run = async (input: string) => {
    if (!input.trim() || !auth) return;
    abort.current?.abort();
    const ac = new AbortController();
    abort.current = ac;
    setResult(null); setError(''); setLog([]); setBusy(true);
    addTrace(`AI 호출: 주문 해석 "${input}"`);
    const push = (m: string) => { setLog((l) => [...l, m]); addTrace(m); };
    try {
      const { runId } = await call<{ runId: number }>(auth, 'POST', '/agent-runs', { text: input });
      await streamRun(auth, runId, (type, d) => {
        if (type === 'step') push(d.message);
        else if (type === 'tool_call') push(`도구 호출: ${d.tool} → ${d.matched ?? '일치 없음'}`);
        else if (type === 'draft') {
          setResult((d.items as any[]).map((i) => ({
            text: i.text, productId: i.productId ?? undefined, qty: i.qty, status: i.status, question: i.question ?? undefined,
          })));
        } else if (type === 'error') { setError(d.message); addTrace(`AI 응답 실패: ${d.code}`); }
      }, ac.signal);
    } catch (e) {
      if (!ac.signal.aborted) setError(e instanceof ApiError ? e.message : 'AI 응답을 받지 못했어요.');
    } finally {
      if (abort.current === ac) setBusy(false);
    }
  };

  return (
    <div className="card">
      <h3>필요한 걸 말씀해 주세요</h3>
      <label htmlFor="ai-text" className="sr">주문 내용</label>
      <textarea id="ai-text" rows={3} value={text} placeholder="예: 두부 2모랑 대파 한 단" onChange={(e) => setText(e.target.value)} />
      <div className="chips">
        {EXAMPLES.map((e) => <button key={e} className="chip" onClick={() => { setText(e); void run(e); }}>{e}</button>)}
      </div>
      <button className="btn primary block" disabled={busy || !text.trim()} onClick={() => void run(text)}>
        {busy ? '확인 중…' : 'AI에게 맡기기'}
      </button>
      {!aiOn && <p className="hint">시연자가 AI를 꺼 두었어요. 직접 검색으로 주문할 수 있습니다.</p>}

      {busy && <Loader label="AI가 확인하고 있어요" />}
      {(busy || log.length > 0) && (
        <ol className="steps" aria-live="polite">
          {log.map((s, i) => <li key={i} className="done">✔ {s}</li>)}
          {busy && <li className="now">⏳ 처리 중…</li>}
        </ol>
      )}

      {error && (
        <div className="alert" role="alert">
          <p>{error}</p>
          <div className="row">
            <button className="btn" onClick={() => void run(text)}>다시 시도</button>
            <button className="btn primary" onClick={onManual}>직접 검색해서 주문</button>
          </div>
        </div>
      )}

      {result && (
        <div className="results">
          {result.map((r, i) => {
            const p = r.productId ? products.find((x) => x.id === r.productId) : undefined;
            return (
              <div key={i} className={`cand ${r.status}`}>
                <span className="badge ai">AI 제안</span>
                <strong>{p ? `${p.name} ${r.qty}${p.unit}` : r.text}</strong>
                {p && <small>{STORES.map((s) => `${s.zone} ${won(p.prices[s.id])}(재고 ${p.stock[s.id]})`).join(' / ')}</small>}
                {r.question && <p className="warn">{r.status === 'notfound' ? '찾을 수 없음: ' : '확인 필요: '}{r.question}</p>}
                {p && <button className="btn small" onClick={() => onAdd(p.id, r.qty, r.question ?? 'AI 제안')}>장바구니에 담기</button>}
              </div>
            );
          })}
          <p className="hint">출처: 카탈로그 가격·재고. AI 제안은 확인 후에만 주문됩니다.</p>
        </div>
      )}
    </div>
  );
}

function Manual({ onAdd }: { onAdd: (id: number) => void }) {
  const { products } = useApp();
  const [q, setQ] = useState('');
  const list = products.filter((p) => p.name.includes(q.trim()));
  return (
    <div className="card">
      <h2>상품 검색</h2>
      <input aria-label="상품 검색" placeholder="상품 이름" value={q} onChange={(e) => setQ(e.target.value)} />
      <div className="pgrid">
        {list.map((p) => {
          const open = STORES.map((s) => p.prices[s.id]).filter((_, i) => p.stock[STORES[i].id] > 0);
          const low = open.length ? Math.min(...open) : 0;
          return (
            <div className="pcard" key={p.id}>
              <div className="pimg" aria-hidden="true">{EMOJI[p.id] ?? '🛒'}</div>
              <strong>{p.name}</strong>
              <span className="price">{open.length ? `${won(low)}~` : '품절'}</span>
              <small>상점 3곳 비교 · 단위 {p.unit}</small>
              <button className="btn small" onClick={() => onAdd(p.id)}>담기</button>
            </div>
          );
        })}
      </div>
      {!list.length && <p>찾을 수 없음</p>}
    </div>
  );
}

function Cart({ lines, setLines, aiMeta, total, estimate, action, embedded }: {
  lines: OrderLine[]; setLines: (f: (l: OrderLine[]) => OrderLine[]) => void; aiMeta: Record<number, string>;
  total: number; estimate: number; action?: React.ReactNode; embedded?: boolean;
}) {
  const { products } = useApp();
  const upd = (pid: number, patch: Partial<OrderLine>) => setLines((ls) => ls.map((l) => (l.productId === pid ? { ...l, ...patch } : l)));
  const body = (
    <>
      {!embedded && <h2>장바구니</h2>}
      {!lines.length && <p>담은 상품이 없어요.</p>}
      {lines.map((l) => {
        const p = products.find((x) => x.id === l.productId)!;
        const low = lowestStore(p, l.qty);
        return (
          <div key={l.productId} className="line">
            <div className="lh">
              <strong>{p.name}</strong>
              {aiMeta[p.id] && <span className="badge ai">AI 제안</span>}
              <button className="link" aria-label={`${p.name} 삭제`} onClick={() => setLines((ls) => ls.filter((x) => x.productId !== p.id))}>삭제</button>
            </div>
            <div className="row">
              <button className="btn small" aria-label="수량 줄이기" onClick={() => upd(p.id, { qty: Math.max(1, l.qty - 1) })}>−</button>
              <output>{l.qty}{p.unit}</output>
              <button className="btn small" aria-label="수량 늘리기" onClick={() => upd(p.id, { qty: l.qty + 1 })}>＋</button>
              <select aria-label={`${p.name} 상점`} value={l.storeId} onChange={(e) => upd(p.id, { storeId: Number(e.target.value), preferred: Number(e.target.value) !== low })}>
                {STORES.map((s) => <option key={s.id} value={s.id} disabled={p.stock[s.id] < l.qty}>
                  {s.name} {won(p.prices[s.id])}{p.stock[s.id] < l.qty ? ' (재고 부족)' : s.id === low ? ' (최저가)' : ''}
                </option>)}
              </select>
              <b>{won(l.qty * p.prices[l.storeId])}</b>
            </div>
            {aiMeta[p.id] && aiMeta[p.id] !== 'AI 제안' && <p className="warn">{aiMeta[p.id]}</p>}
          </div>
        );
      })}
      <dl className="sum">
        <dt>상품 금액</dt><dd>{won(total)}</dd>
        <dt>추정 배송비 (예상치)</dt><dd>{won(ESTIMATE_FEE)}</dd>
        <dt>추정 금액</dt><dd><b>{won(estimate)}</b></dd>
      </dl>
      <p className="hint">공동구매가 확정되면 배송비가 줄어 차액을 돌려받을 수 있어요.</p>
      {action}
    </>
  );
  return embedded ? <div>{body}</div> : <aside className="card sticky">{body}</aside>;
}

function Pay({ estimate, total, nb, onBack, onPay }: {
  estimate: number; total: number; nb: ReturnType<typeof nextBatch>; onBack: () => void; onPay: (fail: boolean) => Promise<string | null>;
}) {
  const [card, setCard] = useState('시연용 카드 A');
  const [fail, setFail] = useState<string | null>(null);
  const [paying, setPaying] = useState(false);
  const pay = async () => {
    setPaying(true);
    const err = await onPay(card === '승인 실패 카드');
    if (err) { setPaying(false); setFail(err); }
  };
  if (paying) return <div className="card"><Loader label="모의 결제 중이에요" /></div>;
  return (
    <div className="card">
      <span className="badge mock">모의 결제</span>
      <h2>결제하기</h2>
      <p>카드 번호는 입력하지 않아요. 시연용 카드를 선택하세요.</p>
      <div className="chips">
        {['시연용 카드 A', '시연용 카드 B', '승인 실패 카드'].map((c) => (
          <button key={c} className={`chip ${card === c ? 'on' : ''}`} aria-pressed={card === c} onClick={() => { setCard(c); setFail(null); }}>{c}</button>
        ))}
      </div>
      <dl className="sum">
        <dt>상품 금액</dt><dd>{won(total)}</dd>
        <dt>추정 금액 (배송비 분담액 포함)</dt><dd><b>{won(estimate)}</b></dd>
      </dl>
      <p>마감 {nb.cutoff} · 확정 {nb.nextDay ? '내일 ' : ''}{nb.slot}. 마감 이후 주문은 다음 배치에서 확정돼요.</p>
      {fail && <p className="alert" role="alert">{fail} 주문은 만들어지지 않았습니다. 다른 카드로 다시 시도하세요.</p>}
      <div className="row">
        <button className="btn" onClick={onBack}>이전</button>
        <button className="btn primary" onClick={pay}>모의 결제하기</button>
      </div>
    </div>
  );
}

function History() {
  const { orders, residentId, products } = useApp();
  const mine = orders.filter((o) => o.residentId === residentId).slice().reverse();
  const [open, setOpen] = useState<number | null>(null);
  if (!mine.length) return <div className="card"><p>아직 주문이 없어요. 주문하기 탭에서 시작해 보세요.</p></div>;
  return (
    <div>
      {mine.map((o) => {
        const idx = TIMELINE.indexOf(o.status);
        return (
          <article key={o.id} className="card">
            <h3>주문 #{o.id} · {STATUS_TEXT[o.status]}</h3>
            {o.status === 'Refunded'
              ? <p className="alert">모든 품목의 재고가 없어 전액 모의 환불됐어요 ({won(o.refund)}).</p>
              : (
                <ol className="timeline">
                  {TIMELINE.map((t, i) => <li key={t} className={i <= idx ? 'on' : ''}>{STATUS_TEXT[t]}</li>)}
                </ol>
              )}
            <ul>
              {o.lines.map((l) => {
                const p = products.find((x) => x.id === l.productId);
                const st = STORES.find((s) => s.id === l.storeId);
                return (
                  <li key={l.productId}>
                    {l.name ?? p?.name} {l.qty}{p?.unit}{st ? ` · ${st.name}` : ''}
                    {l.refunded && <span className="badge mock">모의 환불</span>}
                    {l.changedReason && <small className="warn"> {l.changedReason}</small>}
                  </li>
                );
              })}
            </ul>
            <p>추정 금액 {won(o.estimate)}{o.final !== undefined && <> → 최종 금액 {won(o.final)}</>}</p>
            {o.refund > 0 && o.status !== 'Refunded' && <p className="ok">차액 {won(o.refund)}이 모의 환불돼요.</p>}
            {o.baseline !== undefined && o.final !== undefined && o.status !== 'Refunded' && (
              <>
                <p className="ok">공동구매 절감액(예상치): {won(o.baseline - o.final)}</p>
                <button className="link" aria-expanded={open === o.id} onClick={() => setOpen(open === o.id ? null : o.id)}>계산식 펼치기</button>
                {open === o.id && (
                  <p className="formula">
                    개인 주문 기준 {won(o.baseline)} (평균 가격 + 단독 배송비 {won(ESTIMATE_FEE)}) − 공동구매 {won(o.final)}
                    (상품 {won(o.final - (o.feeShare ?? 0))} + 배송비 분담 {won(o.feeShare ?? 0)}). 시연 규칙 기반 예상치이며 음수일 수 있어요.
                  </p>
                )}
              </>
            )}
          </article>
        );
      })}
    </div>
  );
}
