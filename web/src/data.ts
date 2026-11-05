export type Role = 'resident' | 'shop' | 'courier' | 'demo';
export type Zone = '청도읍' | '금천면' | '풍각면';

export interface Store {
  id: number;
  name: string;
  zone: Zone;
  hours: string;
  note: string;
}

export interface Product {
  id: number;
  name: string;
  unit: string;
  keywords: string[];
  prices: Record<number, number>;
  stock: Record<number, number>;
}

export interface OrderLine {
  productId: number;
  name?: string;
  qty: number;
  storeId: number;
  preferred: boolean;
  changedReason?: string;
  refunded?: boolean;
}

export type OrderStatus = 'Paid' | 'Confirmed' | 'Preparing' | 'Delivering' | 'Delivered' | 'Refunded';

export interface Order {
  id: number;
  residentId: number;
  zone: Zone;
  lines: OrderLine[];
  estimate: number;
  final?: number;
  refund: number;
  status: OrderStatus;
  batchId?: number;
  feeShare?: number;
  baseline?: number;
  createdAt: string;
}

export type PrepStatus = 'Confirmed' | 'Preparing' | 'Ready';
export interface StoreOrder {
  id: number;
  orderId: number;
  storeId: number;
  items: { name: string; qty: number; unitPrice: number }[];
  status: PrepStatus;
}

export type JobStatus = 'Open' | 'Accepted' | 'PickedUp' | 'Delivered';
export interface Job {
  id: number;
  batchId: number;
  zone: Zone;
  orderIds: number[];
  stops: number;
  fee: number;
  km: number;
  earn: number;
  status: JobStatus;
  pickups: { storeId: number; storeName: string; ready: boolean }[];
  allReady: boolean;
  mine: boolean;
}

export interface BatchRun {
  id: number;
  label: string;
  date: string;
  status: string;
  orders: number;
}

export interface Resident {
  id: number;
  name: string;
  zone: Zone;
  address: string;
}

export const STORES: Store[] = [
  { id: 1, name: '탑마트 청도점', zone: '청도읍', hours: '08:30~22:00', note: '매월 2·4번째 일요일 휴업' },
  { id: 2, name: '동곡식자재마트', zone: '금천면', hours: '07:00~22:30', note: '연중무휴' },
  { id: 3, name: '풍각식자재마트', zone: '풍각면', hours: '07:00~22:30', note: '연중무휴' },
];

export const RESIDENTS: Resident[] = [
  { id: 1, name: '가상 주민 A', zone: '청도읍', address: '청도읍 가상로 1' },
  { id: 2, name: '가상 주민 B', zone: '금천면', address: '금천면 가상로 2' },
  { id: 3, name: '가상 주민 C', zone: '풍각면', address: '풍각면 가상로 3' },
];

const P = (
  id: number, name: string, unit: string, keywords: string[],
  prices: [number, number, number], stock: [number, number, number],
): Product => ({
  id, name, unit, keywords,
  prices: { 1: prices[0], 2: prices[1], 3: prices[2] },
  stock: { 1: stock[0], 2: stock[1], 3: stock[2] },
});

export const seedProducts = (): Product[] => [
  P(1, '두부', '모', ['두부'], [2200, 1900, 2000], [20, 15, 10]),
  P(2, '대파', '단', ['대파', '파'], [2800, 3200, 2500], [15, 10, 12]),
  P(3, '계란 30구', '판', ['계란', '달걀'], [7900, 7500, 8200], [10, 8, 5]),
  P(4, '우유 1L', '개', ['우유'], [2900, 2700, 2800], [20, 20, 20]),
  P(5, '라면 5입', '봉', ['라면'], [4200, 3900, 4100], [30, 25, 20]),
  P(6, '쌀 10kg', '포', ['쌀'], [29000, 27500, 28000], [8, 6, 3]),
  P(7, '양파 1kg', '망', ['양파'], [2500, 2300, 2600], [20, 15, 10]),
  P(8, '콩나물', '봉', ['콩나물'], [1500, 1300, 1400], [12, 12, 12]),
];

export const BASE_FEE = 3000;
export const STOP_FEE = 500;
export const ESTIMATE_FEE = BASE_FEE + STOP_FEE;
export const KM: Record<Zone, number> = { 청도읍: 4, 금천면: 9, 풍각면: 12 };
export const EARN_PER_KM = 400;

export const won = (n: number) => `${n.toLocaleString('ko-KR')}원`;

export function lowestStore(p: Product, qty: number): number | null {
  let best: number | null = null;
  for (const s of STORES) {
    if (p.stock[s.id] < qty) continue;
    if (best === null || p.prices[s.id] < p.prices[best]) best = s.id;
  }
  return best;
}

export function nextBatch(clock: string): { slot: string; cutoff: string; nextDay: boolean } {
  if (clock < '08:30') return { slot: '09:00', cutoff: '08:30', nextDay: false };
  if (clock < '14:30') return { slot: '15:00', cutoff: '14:30', nextDay: false };
  return { slot: '09:00', cutoff: '08:30', nextDay: true };
}

export interface Interpreted {
  text: string;
  productId?: number;
  qty: number;
  status: 'ok' | 'ambiguous' | 'notfound';
  question?: string;
}

const KNUM: Record<string, number> = {
  한: 1, 두: 2, 세: 3, 네: 4, 다섯: 5, 여섯: 6, 일곱: 7, 여덟: 8, 아홉: 9, 열: 10,
};

export function interpret(input: string, products: Product[]): Interpreted[] {
  const chunks = input.split(/[,\n]|랑|하고|그리고|와|과|\s및\s/).map((s) => s.trim()).filter(Boolean);
  return chunks.map((text) => {
    const hit = products.find((p) => p.keywords.some((k) => text.includes(k)));
    const digit = text.match(/(\d+)/);
    const kor = Object.keys(KNUM).find((k) => text.includes(k));
    const qty = digit ? Number(digit[1]) : kor ? KNUM[kor] : 0;
    if (!hit) {
      return { text, qty: qty || 1, status: 'notfound' as const, question: '카탈로그에 없는 상품입니다.' };
    }
    if (!qty) {
      return { text, productId: hit.id, qty: 1, status: 'ambiguous' as const, question: '수량이 없어 1로 가정했어요. 맞는지 확인해 주세요.' };
    }
    return { text, productId: hit.id, qty, status: 'ok' as const };
  });
}
