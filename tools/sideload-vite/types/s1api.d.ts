// `s1api` - game data for a page, when S1API is installed next to Sideload.
//
// Written by hand, unlike sideload.d.ts: the surface is the script in Sideload/Script/S1Api.cs (S1Api.Prelude) and the
// handlers in Sideload/Integrations/S1API/Entry.cs, and the C# generator reads neither. Keep the three in step - the
// schema() answer in Entry.cs lists the same fields.
//
//   /// <reference types="@doodesch/sideload-vite/s1api" />
//
// Every value is read fresh from the game when it is accessed. Without S1API, or before a save has loaded, a field
// reads as null - check `s1api.available` once and treat null as "not known yet".

interface S1ApiArea<T> {
  /** The whole area in one read, or null. */
  get(): T | null;
}

interface S1ApiMoney {
  cash: number;
  online: number;
  netWorth: number;
}

interface S1ApiTime {
  day: 'Monday' | 'Tuesday' | 'Wednesday' | 'Thursday' | 'Friday' | 'Saturday' | 'Sunday';
  dayIndex: number;
  /** 24-hour clock as a number: 1430 is 2:30 PM. */
  time: number;
  hour: number;
  minute: number;
  /** "2:30 PM", the way the game writes it. */
  formatted: string;
  night: boolean;
  endOfDay: boolean;
  elapsedDays: number;
  sleeping: boolean;
}

interface S1ApiLeveling {
  rank: 'StreetRat' | 'Hoodlum' | 'Peddler' | 'Hustler' | 'Bagman' | 'Enforcer' | 'ShotCaller' | 'BlockBoss' | 'Underlord' | 'Baron' | 'Kingpin';
  rankIndex: number;
  tier: number;
  xp: number;
  totalXp: number;
  xpToNextTier: number;
}

interface S1ApiLaw {
  wantedLevel: 'None' | 'Investigating' | 'Arresting' | 'NonLethal' | 'Lethal' | null;
  wanted: boolean | null;
  investigated: boolean | null;
  curfewEnabled: boolean;
  curfewActive: boolean;
  intensity: number;
}

interface S1ApiPlayer {
  name: string;
  health: number;
  maxHealth: number;
  dead: boolean;
  arrested: boolean;
  inVehicle: boolean;
  region: string | null;
  property: string | null;
}

interface S1ApiDelivery {
  id: string;
  store: string;
  destination: string;
  dock: number;
  status: 'InTransit' | 'Waiting' | 'Arrived' | 'Completed';
  minutes: number;
  items: { id: string; quantity: number }[];
}

interface S1ApiProduct {
  id: string;
  name: string;
  type: string | null;
  price: number;
  marketValue: number;
  listed: boolean;
  favourite: boolean;
}

type S1ApiFields<T> = { readonly [K in keyof T]: T[K] | null };

interface S1ApiTopics {
  money: S1ApiMoney;
  time: S1ApiTime;
  hour: S1ApiTime;
  day: S1ApiTime;
  sleep: S1ApiTime;
  xp: S1ApiLeveling;
  rank: S1ApiLeveling;
  delivery: { kind: 'created' | 'status' | 'completed'; delivery: S1ApiDelivery | null };
}

interface S1Api {
  /** False without S1API: every field is then null and `on` refuses. */
  readonly available: boolean;
  /** Version of this page-facing surface (not of S1API). */
  readonly version: number;

  readonly money: S1ApiFields<S1ApiMoney> & S1ApiArea<S1ApiMoney> & {
    /** Add (or, negative, take) cash. True only when the app's mod called `AllowGameWrites("money")`. */
    change(amount: number): boolean;
  };
  readonly time: S1ApiFields<S1ApiTime> & S1ApiArea<S1ApiTime> & { readonly now: S1ApiTime | null };
  readonly leveling: S1ApiFields<S1ApiLeveling> & S1ApiArea<S1ApiLeveling> & {
    readonly current: S1ApiLeveling | null;
    /** True only when the app's mod called `AllowGameWrites("leveling")`. */
    addXp(amount: number): boolean;
  };
  readonly law: S1ApiFields<S1ApiLaw> & S1ApiArea<S1ApiLaw>;
  readonly player: S1ApiFields<S1ApiPlayer> & S1ApiArea<S1ApiPlayer>;
  readonly deliveries: { list(): S1ApiDelivery[] };
  readonly products: { list(): S1ApiProduct[]; listed(): S1ApiProduct[] };

  /** Run `fn` whenever the topic changes. False when S1API is missing or the topic is unknown. */
  on<K extends keyof S1ApiTopics>(topic: K, fn: (value: S1ApiTopics[K] | null) => void): boolean;
  /** Remove one listener, or every listener of the topic when `fn` is left out. */
  off<K extends keyof S1ApiTopics>(topic: K, fn?: (value: S1ApiTopics[K] | null) => void): void;

  /** Every area, field and topic, for tools. */
  schema(): unknown;
}

declare const s1api: S1Api;
