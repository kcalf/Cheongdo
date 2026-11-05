import { useEffect, useState } from 'react';
import { AppProvider, useApp } from './store';
import { Role } from './data';
import { Resident } from './views/Resident';
import { Shop } from './views/Shop';
import { Courier } from './views/Courier';
import { Demo } from './views/Demo';

const ROLES: [Role, string, string, string][] = [
  ['resident', '주민', '🛒', '상품을 담고 주문·배송 상태를 봐요'],
  ['shop', '상점', '🏪', '상품·가격·재고를 관리하고 주문을 준비해요'],
  ['courier', '배송자', '🛵', '배송 작업을 수락하고 상태를 갱신해요'],
  ['demo', '시연자', '🎛️', '데모 데이터와 공동구매 계산을 재현해요'],
];

function Splash() {
  const [phase, setPhase] = useState<'show' | 'fade' | 'gone'>(
    sessionStorage.getItem('splash') ? 'gone' : 'show',
  );
  useEffect(() => {
    if (phase === 'gone') return;
    sessionStorage.setItem('splash', '1');
    const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const t1 = setTimeout(() => (reduce ? setPhase('gone') : setPhase('fade')), 2500);
    const t2 = setTimeout(() => setPhase('gone'), 3000);
    return () => { clearTimeout(t1); clearTimeout(t2); };
  }, []);
  if (phase === 'gone') return null;
  return (
    <div className={`splash ${phase}`} aria-hidden="true">
      <img src="/splash.png" alt="" />
    </div>
  );
}

function RolePicker() {
  const { setRole } = useApp();
  return (
    <section className="roles">
      <h2>어떤 역할로 시작할까요?</h2>
      <p className="hint">역할에 맞는 화면만 보여 드려요. (시연용 선택이며 실제 인증이 아닙니다)</p>
      <div className="role-grid">
        {ROLES.map(([r, l, icon, desc]) => (
          <button key={r} className="role-card" onClick={() => setRole(r)}>
            <span aria-hidden="true">{icon}</span>
            <strong>{l}</strong>
            <small>{desc}</small>
          </button>
        ))}
      </div>
    </section>
  );
}

function Shell() {
  const { role, setRole, toast } = useApp();
  const current = ROLES.find((r) => r[0] === role);
  return (
    <>
      <header className="top">
        <h1 className="brand">
          <img src="/mascot-small.png" alt="" />
          <span><b>장</b>보러 <b>갑</b>시다</span>
        </h1>
      </header>
      {current && (
        <div className="rolebar">
          <span>{current[2]} {current[1]} 화면</span>
          <button className="btn small" onClick={() => setRole(null)}>역할 바꾸기</button>
        </div>
      )}
      <main>
        {role === null && <RolePicker />}
        {role === 'resident' && <Resident />}
        {role === 'shop' && <Shop />}
        {role === 'courier' && <Courier />}
        {role === 'demo' && <Demo />}
      </main>
      {toast && <div className="toast" role="status">{toast.text}</div>}
    </>
  );
}

export default function App() {
  return (
    <AppProvider>
      <Splash />
      <Shell />
    </AppProvider>
  );
}
