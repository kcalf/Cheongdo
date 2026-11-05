import { useState } from 'react';
import { useApp } from '../store';
import { STORES, won } from '../data';

export function Shop() {
  const { shopId, setShopId, storeOrders, products, setPrep, setProduct, addProduct } = useApp();
  const [tab, setTab] = useState<'orders' | 'items'>('orders');
  const store = STORES.find((s) => s.id === shopId)!;
  const mine = storeOrders.filter((s) => s.storeId === shopId);
  const label = { Confirmed: '확정', Preparing: '준비 중', Ready: '준비 완료' } as const;
  return (
    <section>
      <label className="who">
        상점 선택
        <select value={shopId} onChange={(e) => setShopId(Number(e.target.value))}>
          {STORES.map((s) => <option key={s.id} value={s.id}>{s.name} ({s.zone})</option>)}
        </select>
      </label>
      <p className="hint">영업시간 {store.hours} · {store.note}</p>
      <div className="seg" role="tablist">
        <button role="tab" aria-selected={tab === 'orders'} onClick={() => setTab('orders')}>확정 주문</button>
        <button role="tab" aria-selected={tab === 'items'} onClick={() => setTab('items')}>상품·가격·재고</button>
      </div>
      {tab === 'orders' && (mine.length === 0
        ? <div className="card"><p>확정된 주문이 없어요. 주민 주문은 마감 시각(08:30, 14:30)에 묶여 확정됩니다. 바로 보려면 시연자 역할의 "시연 제어"에서 배치를 실행하세요.</p></div>
        : mine.slice().reverse().map((so) => (
          <article className="card" key={so.id}>
            <h3>준비 목록 #{so.id} · 주문 #{so.orderId} · 상태: {label[so.status]}</h3>
            <ul>
              {so.items.map((i, k) => <li key={k}>{i.name} {i.qty}개 · {won(i.unitPrice)}</li>)}
            </ul>
            <div className="row">
              <button className="btn" disabled={so.status !== 'Confirmed'} onClick={() => setPrep(so.id, 'Preparing')}>준비 중</button>
              <button className="btn primary" disabled={so.status === 'Ready' || so.status === 'Confirmed'} onClick={() => setPrep(so.id, 'Ready')}>준비 완료</button>
            </div>
          </article>
        )))}
      {tab === 'items' && (
        <div className="card">
          <AddForm onAdd={(n, u, pr, st) => addProduct(n, u, pr, st)} />
          {products.map((p) => <ItemRow key={`${p.id}-${shopId}-${p.prices[shopId]}-${p.stock[shopId]}`} name={p.name} price={p.prices[shopId]} stock={p.stock[shopId]} onSave={(pr, st) => setProduct(p.id, pr, st)} />)}
        </div>
      )}
    </section>
  );
}

function AddForm({ onAdd }: { onAdd: (name: string, unit: string, price: number, stock: number) => Promise<boolean> }) {
  const [name, setName] = useState('');
  const [unit, setUnit] = useState('개');
  const [price, setPrice] = useState(1000);
  const [stock, setStock] = useState(10);
  return (
    <form className="line row wrap" onSubmit={async (e) => { e.preventDefault(); if (await onAdd(name, unit, Math.max(0, price), Math.max(0, stock))) setName(''); }}>
      <strong className="grow">새 상품 추가</strong>
      <label>상품명 <input value={name} onChange={(e) => setName(e.target.value)} placeholder="예: 사과 1kg" /></label>
      <label>단위 <input value={unit} onChange={(e) => setUnit(e.target.value)} size={4} /></label>
      <label>가격 <input type="number" min={0} value={price} onChange={(e) => setPrice(Number(e.target.value))} /></label>
      <label>재고 <input type="number" min={0} value={stock} onChange={(e) => setStock(Number(e.target.value))} /></label>
      <button className="btn primary small" type="submit">추가</button>
    </form>
  );
}

function ItemRow({ name, price, stock, onSave }: { name: string; price: number; stock: number; onSave: (p: number, s: number) => void }) {
  const [pr, setPr] = useState(price);
  const [st, setSt] = useState(stock);
  return (
    <div className="line row wrap">
      <strong className="grow">{name}</strong>
      <label>가격 <input type="number" min={0} value={pr} onChange={(e) => setPr(Number(e.target.value))} /></label>
      <label>재고 <input type="number" min={0} value={st} onChange={(e) => setSt(Number(e.target.value))} /></label>
      <button className="btn small" onClick={() => onSave(Math.max(0, pr), Math.max(0, st))} aria-label={`${name} 저장 (현재 ${won(price)})`}>저장</button>
    </div>
  );
}
