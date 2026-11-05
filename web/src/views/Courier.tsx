import { useApp } from '../store';
import { won, Job } from '../data';

const NEXT: Record<Job['status'], string> = { Open: '', Accepted: '픽업', PickedUp: '배송 완료', Delivered: '' };
const TEXT: Record<Job['status'], string> = { Open: '열림', Accepted: '수락됨', PickedUp: '픽업 완료', Delivered: '배송 완료' };

export function Courier() {
  const { jobs, acceptJob, advanceJob, courierId, setCourierId } = useApp();
  const busy = jobs.some((j) => j.mine && (j.status === 'Accepted' || j.status === 'PickedUp'));
  return (
    <section>
      <label className="who">
        배송자 선택
        <select value={courierId} onChange={(e) => setCourierId(Number(e.target.value))}>
          <option value={1}>가상 배송자 1</option>
          <option value={2}>가상 배송자 2</option>
        </select>
      </label>
      {!jobs.length && <div className="card"><p>열린 배송 작업이 없어요. 시연자 화면에서 배치를 실행하면 생깁니다.</p></div>}
      {jobs.slice().reverse().map((j) => (
        <article className="card" key={j.id}>
          <h3>작업 #{j.id} · {j.zone} · {TEXT[j.status]}</h3>
          <p>정류장 {j.stops}곳 · 예상 거리 {j.km}km (예상치) · 예상 수익 {won(j.earn)} (예상치)</p>
          <p className="hint">상점 준비: {j.pickups.length ? j.pickups.map((r) => `${r.storeName} ${r.ready ? '완료' : '대기'}`).join(', ') : '없음'}</p>
          <ol>
            {j.orderIds.map((id, i) => <li key={id}>정류장 {i + 1}: 주문 #{id} ({j.zone} 가상 주소)</li>)}
          </ol>
          {j.status === 'Open' && (
            <>
              <button className="btn primary" disabled={busy || !j.allReady} onClick={() => acceptJob(j.id)}>수락</button>
              {busy && <p className="hint">이미 수락한 작업이 있어 비활성화됐어요.</p>}
              {!busy && !j.allReady && <p className="hint">상점 준비가 끝나면 수락할 수 있어요.</p>}
            </>
          )}
          {j.mine && NEXT[j.status] && <button className="btn primary" onClick={() => advanceJob(j)}>{NEXT[j.status]}</button>}
        </article>
      ))}
    </section>
  );
}
