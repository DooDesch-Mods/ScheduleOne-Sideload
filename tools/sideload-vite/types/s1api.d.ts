/// <reference path="./s1api.generated.d.ts" />
// `s1api` - game data for a page, when S1API is installed next to Sideload.
//
// Two halves. This file is the curated part, written by hand: the script in Sideload/Script/S1Api.cs (S1Api.Prelude),
// the handlers in Sideload/Integrations/S1API/Entry.cs and the shapes in tools/S1ApiGen/s1api-gen.json ("curated").
// Keep those in step. Everything else S1API offers is in s1api.generated.d.ts, which a Sideload build writes from the
// S1API DLL; it adds its own areas to `S1Api` and joins the curated areas below by their interface names.
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

// One interface per curated area, named S1Api<Area>Area: the generated file adds S1API's own members to an area of the
// same name (S1API.Money.Money joins `money`) by declaring the same interface again.
interface S1ApiMoneyArea extends S1ApiFields<S1ApiMoney>, S1ApiArea<S1ApiMoney> {
  /** Add (or, negative, take) cash. True only when the app's mod called `AllowGameWrites("money")`. */
  change(amount: number): boolean;
}
interface S1ApiTimeArea extends S1ApiFields<S1ApiTime>, S1ApiArea<S1ApiTime> {
  readonly now: S1ApiTime | null;
}
interface S1ApiLevelingArea extends S1ApiFields<S1ApiLeveling>, S1ApiArea<S1ApiLeveling> {
  readonly current: S1ApiLeveling | null;
  /** True only when the app's mod called `AllowGameWrites("leveling")`. */
  addXp(amount: number): boolean;
}
interface S1ApiLawArea extends S1ApiFields<S1ApiLaw>, S1ApiArea<S1ApiLaw> {}
interface S1ApiPlayerArea extends S1ApiFields<S1ApiPlayer>, S1ApiArea<S1ApiPlayer> {}
interface S1ApiDeliveriesArea {
  list(): S1ApiDelivery[];
}
interface S1ApiProductsArea {
  list(): S1ApiProduct[];
  listed(): S1ApiProduct[];
}

interface S1Api {
  /** False without S1API: every field is then null and `on` refuses. */
  readonly available: boolean;
  /** Version of this page-facing surface (not of S1API). */
  readonly version: number;

  readonly money: S1ApiMoneyArea;
  readonly time: S1ApiTimeArea;
  readonly leveling: S1ApiLevelingArea;
  readonly law: S1ApiLawArea;
  readonly player: S1ApiPlayerArea;
  readonly deliveries: S1ApiDeliveriesArea;
  readonly products: S1ApiProductsArea;

  /** Run `fn` whenever the topic changes. False when S1API is missing or the topic is unknown. */
  on<K extends keyof S1ApiTopics>(topic: K, fn: (value: S1ApiTopics[K] | null) => void): boolean;
  /** Remove one listener, or every listener of the topic when `fn` is left out. */
  off<K extends keyof S1ApiTopics>(topic: K, fn?: (value: S1ApiTopics[K] | null) => void): void;

  /** Every area, field, type, topic and write, curated and generated, for tools. */
  schema(): unknown;
}

declare const s1api: S1Api;
