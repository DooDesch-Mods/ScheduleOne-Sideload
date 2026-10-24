using System.Globalization;
using System.Text;
using S1API.Deliveries;
using S1API.Entities;
using S1API.GameTime;
using S1API.Law;
using S1API.Leveling;
using S1API.Money;
using S1API.Products;

namespace Sideload.Integrations.S1Api
{
    /// <summary>
    /// The handlers behind <c>s1api.*</c>, written against S1API.
    ///
    /// Sideload calls <see cref="Install"/> by reflection with plain BCL delegates and never sees a type from here, the
    /// same handshake Sideload.Api uses with Sideload. Every answer is JSON built by hand: the page parses it, and no
    /// S1API or IL2CPP object leaves this file.
    ///
    /// Each area is read in its own method and every handler catches. Two reasons, and the second is the one that
    /// matters: before a save is loaded most of S1API answers with an exception, and an S1API older or newer than the
    /// one this was compiled against throws MissingMethodException the first time a changed member's caller is
    /// compiled. Isolated like this, a missing member costs one area, not the whole object.
    ///
    /// Everything here runs on the Unity main thread: handlers are called from <c>s1.call</c>, and S1API raises its
    /// events there.
    ///
    /// The areas below are the curated ones. Every other static member of S1API comes from Generated.cs, which
    /// tools/S1ApiGen writes from the S1API DLL at build time, and goes through the same Guard, Report and Push.
    /// </summary>
    public static class Entry
    {
        private static Action<string, string, string> _emit;
        private static Func<string, string, bool> _mayWrite;
        private static Action<string> _warn;

        private static readonly HashSet<string> _watching = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _registered = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The generated topics (Generated.cs): name -> the subscription, run the first time a page asks.</summary>
        private static readonly Dictionary<string, Action> _topics = new Dictionary<string, Action>(StringComparer.Ordinal);
        private static readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Register every handler. Returns the number registered, which the loader logs.
        /// </summary>
        /// <param name="handle">name, handler(appId, argument) -> answer, registered for every app.</param>
        /// <param name="emit">appId (null = every app), event name, payload.</param>
        /// <param name="mayWrite">appId, area -> whether that app's mod allowed writing it.</param>
        /// <param name="warn">A line for the log.</param>
        public static int Install(Action<string, Func<string, string, string>> handle,
                                  Action<string, string, string> emit,
                                  Func<string, string, bool> mayWrite,
                                  Action<string> warn)
        {
            _emit = emit;
            _mayWrite = mayWrite;
            _warn = warn;

            int n = 0;
            void On(string name, Func<string, string, string> body)
            {
                if (!_registered.Add(name))
                {
                    Report("register " + name, new InvalidOperationException($"s1api.{name} is registered twice - the second one is left out."));
                    return;
                }
                handle("s1api." + name, (app, arg) => Guard(name, () => body(app, arg)));
                n++;
            }

            On("money", (_, __) => Money());
            On("time", (_, __) => Time());
            On("leveling", (_, __) => Leveling());
            On("law", (_, __) => Law());
            On("player", (_, __) => PlayerState());
            On("deliveries", (_, __) => Deliveries());
            On("products", (_, __) => Products());
            On("schema", (_, __) => Schema());
            On("watch", (_, topic) => Watch(topic));

            On("money.change", (app, arg) => Write(app, "money", arg, amount => S1API.Money.Money.ChangeCashBalance(amount, true, true)));
            On("leveling.addXp", (app, arg) => Write(app, "leveling", arg, amount => LevelManager.AddXP((int)Math.Round(amount))));

            // Everything else S1API offers, generated from its DLL at build time (tools/S1ApiGen). Registered after the
            // curated handlers so a curated name always wins; the generator already leaves those names out.
            Generated.Register(
                read: (name, body) => On(name, (_, __) => body()),
                write: (name, scope, body) => On(name, (app, arg) => WriteArgs(app, scope, name, arg, body)),
                topic: (name, subscribe) => _topics[name] = subscribe);

            return n;
        }

        // ---------------------------------------------------------------- areas --

        private static string Money() => new Obj()
            .Num("cash", S1API.Money.Money.GetCashBalance())
            .Num("online", S1API.Money.Money.GetOnlineBalance())
            .Num("netWorth", S1API.Money.Money.GetNetWorth())
            .ToString();

        private static string Time()
        {
            int time = TimeManager.CurrentTime;
            Day day = TimeManager.CurrentDay;
            return new Obj()
                .Str("day", day.ToString())
                .Int("dayIndex", (int)day)
                .Int("time", time)
                .Int("hour", time / 100)
                .Int("minute", time % 100)
                .Str("formatted", TimeManager.GetFormatted12HourTime())
                .Bool("night", TimeManager.IsNight)
                .Bool("endOfDay", TimeManager.IsEndOfDay)
                .Int("elapsedDays", TimeManager.ElapsedDays)
                .Bool("sleeping", TimeManager.SleepInProgress)
                .ToString();
        }

        private static string Leveling()
        {
            if (!LevelManager.Exists) return "null";

            FullRank rank = LevelManager.CurrentRank;
            return new Obj()
                .Str("rank", rank.Rank.ToString())
                .Int("rankIndex", (int)rank.Rank)
                .Int("tier", rank.Tier)
                .Int("xp", LevelManager.XP)
                .Int("totalXp", LevelManager.TotalXP)
                .Num("xpToNextTier", LevelManager.XPToNextTier)
                .ToString();
        }

        private static string Law()
        {
            Player local = Player.Local;
            var o = new Obj();

            if (local != null)
            {
                PursuitLevel level = LawManager.GetWantedLevel(local);
                o.Str("wantedLevel", level.ToString())
                 .Bool("wanted", LawManager.IsPlayerWanted(local))
                 .Bool("investigated", LawManager.IsUnderInvestigation(local));
            }
            else
            {
                o.Raw("wantedLevel", "null").Raw("wanted", "null").Raw("investigated", "null");
            }

            return o.Bool("curfewEnabled", CurfewManager.IsEnabled)
                    .Bool("curfewActive", CurfewManager.IsCurrentlyActive)
                    .Int("intensity", LawController.Intensity)
                    .ToString();
        }

        private static string PlayerState()
        {
            Player local = Player.Local;
            if (local == null) return "null";

            return new Obj()
                .Str("name", local.Name)
                .Num("health", local.CurrentHealth)
                .Num("maxHealth", local.MaxHealth)
                .Bool("dead", local.IsDead)
                .Bool("arrested", local.IsArrested)
                .Bool("inVehicle", local.IsInVehicle)
                .Str("region", Safe("player.region", () => local.CurrentRegion.ToString()))
                .Str("property", Safe("player.property", () => local.CurrentProperty?.ToString()))
                .ToString();
        }

        private static string Deliveries()
        {
            var sb = new StringBuilder("[");
            bool first = true;

            foreach (Delivery d in DeliveryRegistry.GetAll())
            {
                if (d == null) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append(DeliveryJson(d));
            }

            return sb.Append(']').ToString();
        }

        private static string DeliveryJson(Delivery d)
        {
            var items = new StringBuilder("[");
            bool first = true;
            if (d.Items != null)
            {
                foreach (DeliveryItem item in d.Items)
                {
                    if (item == null) continue;
                    if (!first) items.Append(',');
                    first = false;
                    items.Append(new Obj().Str("id", item.ItemId).Int("quantity", item.Quantity).ToString());
                }
            }
            items.Append(']');

            return new Obj()
                .Str("id", d.Id)
                .Str("store", d.StoreName)
                .Str("destination", d.DestinationCode)
                .Int("dock", d.LoadingDockIndex)
                .Str("status", d.Status.ToString())
                .Int("minutes", d.MinutesUntilArrival)
                .Raw("items", items.ToString())
                .ToString();
        }

        private static string Products()
        {
            var listed = new HashSet<string>(StringComparer.Ordinal);
            var favourite = new HashSet<string>(StringComparer.Ordinal);

            foreach (ProductDefinition p in ProductManager.ListedProducts ?? Array.Empty<ProductDefinition>())
                if (p != null) listed.Add(p.ID);
            foreach (ProductDefinition p in ProductManager.FavouritedProducts ?? Array.Empty<ProductDefinition>())
                if (p != null) favourite.Add(p.ID);

            var sb = new StringBuilder("[");
            bool first = true;

            foreach (ProductDefinition p in ProductManager.DiscoveredProducts ?? Array.Empty<ProductDefinition>())
            {
                if (p == null) continue;
                if (!first) sb.Append(',');
                first = false;

                sb.Append(new Obj()
                    .Str("id", p.ID)
                    .Str("name", p.Name)
                    .Str("type", Safe("products.type", () => p.PrimaryDrugType.ToString()))
                    .Num("price", p.Price)
                    .Num("marketValue", p.MarketValue)
                    .Bool("listed", listed.Contains(p.ID))
                    .Bool("favourite", favourite.Contains(p.ID))
                    .ToString());
            }

            return sb.Append(']').ToString();
        }

        /// <summary>What a page can read, for an editor to complete and to fill a data panel with: the curated areas
        /// (their shapes live in tools/S1ApiGen/s1api-gen.json) with the generated members, types, topics and writes.
        /// Written at build time by the generator.</summary>
        private static string Schema() => Generated.Schema;

        // ---------------------------------------------------------------- events --

        /// <summary>
        /// Start pushing one topic. Subscribed the first time any page asks and never unsubscribed: an event with no
        /// listening page is a dictionary lookup in Sideload, and the payload is built only when S1API raises the event.
        /// Keeping the subscription is cheaper than counting pages across reloads.
        /// </summary>
        private static string Watch(string topic)
        {
            string named = (topic ?? "").Trim();
            topic = named.ToLowerInvariant();
            if (_watching.Contains(named)) return "ok";

            // A generated topic is matched by its exact name, because its events go out under that name and the page
            // listens for the name it asked with.
            if (_topics.TryGetValue(named, out Action subscribe))
            {
                subscribe();
                _watching.Add(named);
                return "ok";
            }

            switch (topic)
            {
                case "money":
                    S1API.Money.Money.OnBalanceChanged += () => PushIfChanged("money", Money);
                    WatchOnTick("money", Money);
                    break;
                case "time": TimeManager.OnTick += () => Push("time", Time); break;
                case "hour": TimeManager.OnHourPass += () => Push("hour", Time); break;
                case "day": TimeManager.OnDayPass += () => Push("day", Time); break;
                case "sleep": TimeManager.OnSleepEnd += _ => Push("sleep", Time); break;
                case "xp":
                    LevelManager.OnXPChanged += (_, __) => PushIfChanged("xp", Leveling);
                    WatchOnTick("xp", Leveling);
                    break;
                case "rank": LevelManager.OnRankUp += (_, __) => Push("rank", Leveling); break;
                case "delivery":
                    DeliveryRegistry.Created += d => Push("delivery", () => DeliveryEvent("created", d));
                    DeliveryRegistry.StatusChanged += (d, _, __) => Push("delivery", () => DeliveryEvent("status", d));
                    DeliveryRegistry.Completed += d => Push("delivery", () => DeliveryEvent("completed", d));
                    break;
                default:
                    Report("watch " + topic, new ArgumentException($"'{topic}' is not a topic."));
                    return "unknown";
            }

            _watching.Add(topic);
            return "ok";
        }

        private static string DeliveryEvent(string kind, Delivery d) =>
            "{\"kind\":" + Obj.Quote(kind) + ",\"delivery\":" + (d == null ? "null" : DeliveryJson(d)) + "}";

        internal static void Push(string topic, Func<string> payload)
        {
            string json = Guard("event " + topic, payload);
            Emit(topic, json);
        }

        private static void Emit(string topic, string json)
        {
            try { _emit?.Invoke(null, "s1api:" + topic, json); }
            catch (Exception e) { Report("event " + topic, e); }
        }

        private static readonly Dictionary<string, string> _lastSent = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Push only when the area reads differently from the last push, so a change seen both by an S1API
        /// event and by the tick below reaches the page once.</summary>
        private static void PushIfChanged(string topic, Func<string> payload)
        {
            string json = Guard("event " + topic, payload);
            if (_lastSent.TryGetValue(topic, out string last) && last == json) return;
            _lastSent[topic] = json;
            Emit(topic, json);
        }

        /// <summary>
        /// S1API's events cover only part of what changes these areas: Money.OnBalanceChanged fires for changes made
        /// through S1API alone (a sale, a purchase or the console go past it), and LevelManager.OnXPChanged is the
        /// game's onRankChanged, which a plain XP gain does not raise. So the area is also read once a game minute and
        /// pushed when it differs - a game minute is about a second, and the read is a few property gets.
        /// </summary>
        private static void WatchOnTick(string topic, Func<string> payload)
        {
            _lastSent[topic] = Guard("event " + topic, payload);
            TimeManager.OnTick += () => PushIfChanged(topic, payload);
        }

        // ---------------------------------------------------------------- writes --

        private static string Write(string appId, string scope, string arg, Action<float> apply)
        {
            if (_mayWrite == null || !_mayWrite(appId, scope))
            {
                Report("write " + scope + " " + appId,
                       new UnauthorizedAccessException($"'{appId}' may not change {scope} - its mod has to call AllowGameWrites(\"{scope}\")."));
                return "denied";
            }

            if (!float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out float amount)
                || float.IsNaN(amount) || float.IsInfinity(amount))
            {
                Report("write " + scope + " " + appId, new ArgumentException($"'{arg}' is not a finite number."));
                return "error";
            }

            apply(amount);
            return "ok";
        }

        /// <summary>A generated write: the app's permission for the scope first, then the arguments, then the call.
        /// A bad argument throws inside <paramref name="apply"/> and comes back from the guard as a refused write.</summary>
        private static string WriteArgs(string appId, string scope, string name, string arg, Func<IReadOnlyList<object>, string> apply)
        {
            if (_mayWrite == null || !_mayWrite(appId, scope))
            {
                Report("write " + name + " " + appId,
                       new UnauthorizedAccessException($"'{appId}' may not call s1api.{name} - its mod has to call AllowGameWrites(\"{scope}\")."));
                return "denied";
            }

            return apply(Args.Parse(arg));
        }

        // ---------------------------------------------------------------- plumbing --

        internal static string Guard(string what, Func<string> body)
        {
            try { return body() ?? "null"; }
            catch (Exception e)
            {
                Report(what, e);
                return "null";
            }
        }

        /// <summary>Once per area and kind of failure. A page reading cash on a timer before the save has loaded
        /// would otherwise write the same line sixty times a minute.</summary>
        internal static void Report(string what, Exception e)
        {
            string key = what + "|" + e.GetType().Name;
            if (!_reported.Add(key)) return;
            _warn?.Invoke($"s1api.{what}: {e.GetType().Name}: {e.Message}");
        }

        /// <summary>One member that may be unset or missing on its own: null for it, the rest of the object intact.</summary>
        private static string Safe(string what, Func<string> read)
        {
            try { return read(); }
            catch (Exception e)
            {
                Report(what, e);
                return null;
            }
        }

        /// <summary>A flat JSON object, written as it goes. Numbers in the invariant culture - a German Windows
        /// would otherwise write 12,5 and the page would parse a syntax error.</summary>
        private sealed class Obj
        {
            private readonly StringBuilder _sb = new StringBuilder("{");
            private bool _any;

            private Obj Key(string name)
            {
                if (_any) _sb.Append(',');
                _any = true;
                _sb.Append(Quote(name)).Append(':');
                return this;
            }

            internal Obj Str(string name, string value)
            {
                Key(name);
                _sb.Append(value == null ? "null" : Quote(value));
                return this;
            }

            internal Obj Int(string name, int value)
            {
                Key(name);
                _sb.Append(value.ToString(CultureInfo.InvariantCulture));
                return this;
            }

            internal Obj Num(string name, double value)
            {
                Key(name);
                _sb.Append(double.IsNaN(value) || double.IsInfinity(value)
                    ? "null"
                    : Math.Round(value, 4).ToString("R", CultureInfo.InvariantCulture));
                return this;
            }

            internal Obj Bool(string name, bool value)
            {
                Key(name);
                _sb.Append(value ? "true" : "false");
                return this;
            }

            internal Obj Raw(string name, string json)
            {
                Key(name);
                _sb.Append(json);
                return this;
            }

            public override string ToString() => _sb.ToString() + "}";

            internal static string Quote(string value)
            {
                var sb = new StringBuilder(value.Length + 2).Append('"');
                foreach (char c in value)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
                return sb.Append('"').ToString();
            }
        }
    }
}
