import { useState } from 'react';
import { useApp } from '../store';
import { Loader } from '../Loader';
import { won, nextBatch } from '../data';

const CLOCKS = ['08:00', '08:30', '10:00', '14:30', '16:00'];

export function Demo() {
  const { clock, setClock, aiOn, setAiOn, runBatch, batches, metrics, trace, reset, orders } = useApp();
  const [confirm, setConfirm] = useState(false);
  const [running, setRunning] = useState(false);
  const [msg, setMsg] = useState('');
  const [open, setOpen] = useState<string | null>(null);
  const nb = nextBatch(clock);
  const waiting = orders.filter((o) => o.status === 'Paid').length;
  const rows: [string, string, string][] = [
    ['묶인 주문 수', `${metrics.orders}건`, '배치로 확정된 주문 수'],
    ['줄어든 배송 횟수', `${metrics.savedDeliveries}회`, '확정 주문 수 − 배송 작업 수. 주문마다 단독 배송한다고 가정'],
    ['절감 비용 (예상치)', won(metrics.saved), '단독 주문(평균 가격 + 배송비 3,500원) − 공동구매 금액. 음수일 수 있음'],
    ['상점 준비 목록', `${metrics.storeOrders}건`, '상점별 합계 준비 목록'],
    ['배송자 일감 통합', `${metrics.jobs}건`, '구역별 배송 작업. 배송비 3,000원 + 정류장당 500원'],
  ];
  return (
    <section>
      <div className="card">
        <h2>시연 제어</h2>
        <label className="who">시연 시계
          <select value={clock} onChange={(e) => void setClock(e.target.value)}>{(CLOCKS.includes(clock) ? CLOCKS : [clock, ...CLOCKS]).map((c) => <option key={c}>{c}</option>)}</select>
        </label>
        <p>다음 배치: {nb.nextDay ? '내일 ' : ''}{nb.slot} (마감 {nb.cutoff}) · 대기 주문 {waiting}건</p>
        <label className="check"><input type="checkbox" checked={aiOn} onChange={(e) => void setAiOn(e.target.checked)} /> AI 켜기 (끄면 실패 안내와 수동 주문 전환을 시연)</label>
        <div className="row">
          <button className="btn primary" onClick={() => setConfirm(true)}>배치 지금 실행</button>
          <button className="btn" onClick={() => void reset()}>시연 데이터 초기화</button>
        </div>
        {msg && <p role="status" className="ok">{msg}</p>}
      </div>

      {confirm && (
        <div className="modal" role="dialog" aria-modal="true" aria-labelledby="cf">
          <div className="card">
            {running ? <Loader label="배치를 실행하고 있어요" /> : (
              <>
                <h3 id="cf">정말 실행할까요?</h3>
                <p>대기 중인 결제 완료 주문 {waiting}건을 지금 확정합니다.</p>
                <div className="row">
                  <button className="btn" onClick={() => setConfirm(false)}>취소</button>
                  <button className="btn primary" autoFocus onClick={async () => { setRunning(true); setMsg(await runBatch()); setRunning(false); setConfirm(false); }}>실행</button>
                </div>
              </>
            )}
          </div>
        </div>
      )}

      <div className="card">
        <h2>효과 지표 <span className="badge mock">예상치</span></h2>
        {rows.map(([k, v, f]) => (
          <div key={k} className="metric">
            <div className="row between"><span>{k}</span><b>{v}</b></div>
            <button className="link" aria-expanded={open === k} onClick={() => setOpen(open === k ? null : k)}>계산식과 가정</button>
            {open === k && <p className="formula">{f}. 모든 값은 시연 규칙 기반이며 실측이 아닙니다.</p>}
          </div>
        ))}
      </div>

      <div className="card">
        <h2>배치 기록</h2>
        {batches.length === 0 ? <p>아직 실행 기록이 없어요.</p> : (
          <ul>{batches.map((b) => <li key={b.id}>#{b.id} · {b.date} {b.label} · 상태 {b.status} · 주문 {b.orders}건</li>)}</ul>
        )}
      </div>

      <div className="card">
        <h2>AI·도구 호출 기록</h2>
        {trace.length === 0 ? <p>AI 주문을 사용하면 호출 단계가 여기에 기록됩니다.</p> : <ul className="trace">{trace.map((t, i) => <li key={i}>{t}</li>)}</ul>}
      </div>
    </section>
  );
}
