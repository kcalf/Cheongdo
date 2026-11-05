/* eslint-disable @typescript-eslint/no-explicit-any */
import { createContext, useCallback, useContext, useEffect, useRef, useState, ReactNode } from 'react';
import {
  Order, OrderLine, Product, StoreOrder, Job, BatchRun, Role, Zone, RESIDENTS, STORES,
} from './data';
import { ApiError, Auth, call, uuid } from './api';

export interface Toast { id: number; text: string }
export interface Metrics { orders: number; savedDeliveries: number; saved: number; storeOrders: number; jobs: number }

interface Ctx {
  role: Role | null; setRole: (r: Role | null) => void;
  auth: Auth | null;
  residentId: number; setResidentId: (n: number) => void;
  shopId: number; setShopId: (n: number) => void;
  courierId: number; setCourierId: (n: number) => void;
  clock: string; setClock: (c: string) => Promise<void>;
  aiOn: boolean; setAiOn: (b: boolean) => Promise<void>;
  products: Product[]; orders: Order[]; storeOrders: StoreOrder[]; jobs: Job[]; batches: BatchRun[];
  trace: string[]; addTrace: (t: string) => void;
  toast: Toast | null; loading: boolean;
  placeOrder: (lines: OrderLine[], fail: boolean) => Promise<string | null>;
  setPrep: (id: number, s: 'Preparing' | 'Ready') => Promise<void>;
  setProduct: (productId: number, price: number, stock: number) => Promise<void>;
  addProduct: (name: string, unit: string, price: number, stock: number) => Promise<boolean>;
  acceptJob: (id: number) => Promise<void>;
  advanceJob: (job: Job) => Promise<void>;
  runBatch: () => Promise<string>;
  metrics: Metrics;
  reset: () => Promise<void>;
}

const C = createContext<Ctx>(null as unknown as Ctx);
export const useApp = () => useContext(C);

const ZERO: Metrics = { orders: 0, savedDeliveries: 0, saved: 0, storeOrders: 0, jobs: 0 };
const ORDER_STATUS: Record<string, Order['status']> = {
  InBatch: 'Paid', Confirmed: 'Confirmed', Preparing: 'Preparing', ReadyForPickup: 'Preparing',
  PickedUp: 'Delivering', Delivered: 'Delivered', Cancelled: 'Refunded', Refunded: 'Refunded',
};
const zoneOfResident = (id: number): Zone => RESIDENTS.find((r) => r.id === id)?.zone ?? '청도읍';

function mapProduct(p: any): Product {
  const prices: Record<number, number> = {};
  const stock: Record<number, number> = {};
  for (const s of STORES) { prices[s.id] = 0; stock[s.id] = 0; }
  for (const s of p.stores ?? []) { prices[s.storeId] = s.price; stock[s.storeId] = s.stockQty; }
  return { id: p.productId, name: p.name, unit: p.unit, keywords: [p.name], prices, stock };
}

function mapOrder(o: any): Order {
  const lines: OrderLine[] = (o.items ?? []).map((i: any) => ({
    productId: i.productId, name: i.name, qty: i.qty, storeId: i.storeId ?? 0, preferred: false,
    changedReason: i.storeChangedReason ?? undefined, refunded: i.status === 'Refunded',
  }));
  const status = ORDER_STATUS[o.status] ?? 'Paid';
  const final = o.finalTotal ?? undefined;
  return {
    id: o.id, residentId: o.residentId, zone: zoneOfResident(o.residentId), lines, estimate: o.estimatedTotal,
    final, refund: status === 'Refunded' ? o.estimatedTotal : final !== undefined ? Math.max(0, o.estimatedTotal - final) : 0,
    status, batchId: o.batchId ?? undefined, feeShare: o.feeShare ?? undefined,
    baseline: o.individualBaselineTotal ?? undefined, createdAt: o.createdAt,
  };
}

export function AppProvider({ children }: { children: ReactNode }) {
  const [role, setRoleState] = useState<Role | null>(null);
  const [residentId, setResidentId] = useState(1);
  const [shopId, setShopId] = useState(1);
  const [courierId, setCourierId] = useState(1);
  const [clock, setClockState] = useState('10:00');
  const [date, setDate] = useState('');
  const [aiOn, setAiOnState] = useState(true);
  const [products, setProducts] = useState<Product[]>([]);
  const [orders, setOrders] = useState<Order[]>([]);
  const [storeOrders, setStoreOrders] = useState<StoreOrder[]>([]);
  const [jobs, setJobs] = useState<Job[]>([]);
  const [batches, setBatches] = useState<BatchRun[]>([]);
  const [metrics, setMetrics] = useState<Metrics>(ZERO);
  const [trace, setTrace] = useState<string[]>([]);
  const [toast, setToast] = useState<Toast | null>(null);
  const [loading, setLoading] = useState(false);
  const seq = useRef(0);
  const warned = useRef(false);

  const auth: Auth | null = role === null ? null
    : { role, userId: role === 'resident' ? residentId : role === 'shop' ? shopId : role === 'courier' ? courierId : 0 };
  const authRef = useRef(auth);
  authRef.current = auth;

  const say = useCallback((text: string) => {
    const id = Date.now();
    setToast({ id, text });
    setTimeout(() => setToast((t) => (t && t.id === id ? null : t)), 3500);
  }, []);

  const addTrace = useCallback((t: string) => setTrace((x) => [`${new Date().toLocaleTimeString('ko-KR')} ${t}`, ...x].slice(0, 40)), []);

  const refresh = useCallback(async () => {
    const a = authRef.current;
    if (!a) return;
    const my = ++seq.current;
    try {
      const get = <T,>(p: string) => call<T>(a, 'GET', p);
      const [clk, prods, bs, ords, sos, js, met] = await Promise.all([
        get<any>('/clock'),
        get<any[]>('/products'),
        get<any[]>('/batches'),
        a.role === 'resident' || a.role === 'demo' ? get<any[]>('/orders') : Promise.resolve(null),
        a.role === 'shop' ? get<any[]>('/store/orders') : Promise.resolve(null),
        a.role === 'courier' || a.role === 'demo' ? get<any[]>('/courier/jobs') : Promise.resolve(null),
        a.role === 'demo' ? get<any>('/demo/metrics') : Promise.resolve(null),
      ]);
      if (my !== seq.current) return;
      warned.current = false;
      setDate(String(clk.nowKst).slice(0, 10));
      setClockState(String(clk.nowKst).slice(11, 16));
      setAiOnState(!!clk.aiEnabled);
      setProducts(prods.map(mapProduct));
      setBatches(bs.map((b) => ({ id: b.id, label: b.label, date: b.businessDate, status: b.status, orders: b.orderCount })));
      if (ords) setOrders(ords.map(mapOrder).reverse());
      if (sos) {
        setStoreOrders(sos.map((s) => ({
          id: s.id, orderId: s.orderId, storeId: a.userId, status: s.status,
          items: (s.items ?? []).map((i: any) => ({ name: i.name, qty: i.qty, unitPrice: i.unitPrice })),
        })));
      }
      if (js) {
        setJobs(js.map((j): Job => ({
          id: j.id, batchId: j.batchId, zone: (j.zoneName as Zone) ?? '청도읍', orderIds: j.route ?? [], stops: j.stopCount,
          fee: j.fee, km: Math.round((j.estimatedDistanceM ?? 0) / 100) / 10, earn: j.estimatedEarning, status: j.status,
          pickups: j.pickups ?? [], allReady: !!j.allReady, mine: j.acceptedByCourierId != null && j.acceptedByCourierId === a.userId,
        })));
      }
      if (met) {
        setMetrics({
          orders: met.ordersInBatches, savedDeliveries: met.savedDeliveries, saved: met.totalSavings,
          storeOrders: met.storeOrders, jobs: met.deliveryJobs,
        });
      }
    } catch (e) {
      if (my !== seq.current) return;
      if (!warned.current) { warned.current = true; say(e instanceof ApiError ? e.message : '데이터를 불러오지 못했어요.'); }
    }
  }, [say]);

  useEffect(() => {
    if (role === null) return undefined;
    setLoading(true);
    refresh().finally(() => setLoading(false));
    const t = window.setInterval(() => { if (!document.hidden) void refresh(); }, 5000);
    return () => window.clearInterval(t);
  }, [role, residentId, shopId, courierId, refresh]);

  const setRole = (r: Role | null) => {
    setProducts([]); setOrders([]); setStoreOrders([]); setJobs([]); setBatches([]); setMetrics(ZERO);
    setRoleState(r);
  };

  // 요청을 보내고 목록을 새로 읽는다. 실패하면 서버 메시지를 안내한다.
  const act = async <T,>(fn: () => Promise<T>): Promise<T | undefined> => {
    try {
      const r = await fn();
      await refresh();
      return r;
    } catch (e) {
      say(e instanceof ApiError ? e.message : '요청을 처리하지 못했어요.');
      await refresh();
      return undefined;
    }
  };

  const placeOrder: Ctx['placeOrder'] = async (lines, fail) => {
    const a = authRef.current!;
    const items = lines.map((l) => ({ productId: l.productId, qty: l.qty, ...(l.preferred ? { preferredStoreId: l.storeId } : {}) }));
    try {
      await call(a, 'POST', '/orders', { items, payment: { simulate: fail ? 'fail' : 'approve' } }, uuid());
      await refresh();
      return null;
    } catch (e) {
      return e instanceof ApiError ? e.message : '주문을 만들지 못했어요.';
    }
  };

  const setPrep: Ctx['setPrep'] = async (id, s) => {
    await act(() => call(authRef.current!, 'POST', `/store/orders/${id}/${s === 'Preparing' ? 'prepare' : 'ready'}`));
  };

  const setProduct: Ctx['setProduct'] = async (pid, price, stock) => {
    if (await act(() => call(authRef.current!, 'PUT', `/store/products/${pid}`, { price, stockQty: stock }))) say('상품 정보를 저장했습니다.');
  };

  const addProduct: Ctx['addProduct'] = async (name, unit, price, stock) => {
    const nm = name.trim();
    if (!nm || !unit.trim()) { say('상품명과 단위를 입력하세요.'); return false; }
    try {
      await call(authRef.current!, 'POST', '/store/products', { name: nm, unit: unit.trim(), price, stockQty: stock }, uuid());
      await refresh();
      say('상품을 추가했습니다.');
      return true;
    } catch (e) {
      say(e instanceof ApiError ? e.message : '상품을 추가하지 못했어요.');
      return false;
    }
  };

  const acceptJob: Ctx['acceptJob'] = async (id) => { await act(() => call(authRef.current!, 'POST', `/courier/jobs/${id}/accept`)); };

  const advanceJob: Ctx['advanceJob'] = async (job) => {
    const step = job.status === 'Accepted' ? 'pickup' : job.status === 'PickedUp' ? 'deliver' : null;
    if (step) await act(() => call(authRef.current!, 'POST', `/courier/jobs/${job.id}/${step}`));
  };

  const setClock: Ctx['setClock'] = async (c) => {
    await act(() => call(authRef.current!, 'PUT', '/demo/clock', { time: `${date}T${c}:00` }));
  };

  const setAiOn: Ctx['setAiOn'] = async (b) => {
    await act(() => call(authRef.current!, 'PUT', '/demo/ai', { enabled: b }));
  };

  const runBatch = async (): Promise<string> => {
    const target = batches
      .filter((b) => b.status === 'Open' && b.orders > 0)
      .sort((x, y) => (x.date + x.label).localeCompare(y.date + y.label))[0];
    if (!target) return '대기 중인 결제 완료 주문이 없습니다.';
    const slot = target.label.includes('09') ? 'AM09' : 'PM15';
    const r = await act(() => call<any>(authRef.current!, 'POST', '/demo/batches/run', { businessDate: target.date, slot, confirm: true }));
    if (!r) return '배치를 실행하지 못했어요.';
    addTrace(`배치 #${target.id} 실행: ${r.result}`);
    return r.result === 'Confirmed' ? `배치 #${target.id}를 실행했습니다.` : r.message ?? '이미 확정된 배치입니다.';
  };

  const reset = async () => {
    if (await act(() => call(authRef.current!, 'POST', '/demo/reset', { confirm: true }))) { setTrace([]); say('시연 데이터를 초기화했습니다.'); }
  };

  const value: Ctx = {
    role, setRole, auth, residentId, setResidentId, shopId, setShopId, courierId, setCourierId,
    clock, setClock, aiOn, setAiOn, products, orders, storeOrders, jobs, batches, trace, addTrace, toast, loading,
    placeOrder, setPrep, setProduct, addProduct, acceptJob, advanceJob, runBatch, metrics, reset,
  };
  return <C.Provider value={value}>{children}</C.Provider>;
}

