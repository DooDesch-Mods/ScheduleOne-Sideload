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
    /// </summary>
    public static class Entry
    {
        private static Action<string, string, string> _emit;
        private static Func<string, string, bool> _mayWrite;
        private static Action<string> _warn;

        private static readonly HashSet<string> _watching = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                .Str("region", Safe(() => local.CurrentRegion.ToString()))
                .Str("property", Safe(() => local.CurrentProperty?.ToString()))
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
                    .Str("type", Safe(() => p.DrugType.ToString()))
                    .Num("price", p.Price)
                    .Num("marketValue", p.MarketValue)
                    .Bool("listed", listed.Contains(p.ID))
                    .Bool("favourite", favourite.Contains(p.ID))
                    .ToString());
            }

            return sb.Append(']').ToString();
        }

        /// <summary>What a page can read, for an editor to complete and to fill a data panel with. Hand-kept next to
        /// the areas above; the version in Sideload's prelude moves with it.</summary>
        private static string Schema() =>
            "{\"version\":1,\"areas\":{" +
            "\"money\":{\"cash\":\"number\",\"online\":\"number\",\"netWorth\":\"number\"}," +
            "\"time\":{\"day\":\"string\",\"dayIndex\":\"number\",\"time\":\"number\",\"hour\":\"number\",\"minute\":\"number\",\"formatted\":\"string\",\"night\":\"boolean\",\"endOfDay\":\"boolean\",\"elapsedDays\":\"number\",\"sleeping\":\"boolean\"}," +
            "\"leveling\":{\"rank\":\"string\",\"rankIndex\":\"number\",\"tier\":\"number\",\"xp\":\"number\",\"totalXp\":\"number\",\"xpToNextTier\":\"number\"}," +
            "\"law\":{\"wantedLevel\":\"string\",\"wanted\":\"boolean\",\"investigated\":\"boolean\",\"curfewEnabled\":\"boolean\",\"curfewActive\":\"boolean\",\"intensity\":\"number\"}," +
            "\"player\":{\"name\":\"string\",\"health\":\"number\",\"maxHealth\":\"number\",\"dead\":\"boolean\",\"arrested\":\"boolean\",\"inVehicle\":\"boolean\",\"region\":\"string\",\"property\":\"string\"}," +
            "\"deliveries\":{\"list\":\"[{id,store,destination,dock,status,minutes,items:[{id,quantity}]}]\"}," +
            "\"products\":{\"list\":\"[{id,name,type,price,marketValue,listed,favourite}]\"}" +
            "},\"topics\":[\"money\",\"time\",\"hour\",\"day\",\"sleep\",\"xp\",\"rank\",\"delivery\"]," +
            "\"writes\":{\"money.change\":\"money\",\"leveling.addXp\":\"leveling\"}}";

        // ---------------------------------------------------------------- events --

        /// <summary>
        /// Start pushing one topic. Subscribed the first time any page asks and never unsubscribed: an event with no
        /// listening page is a dictionary lookup in Sideload, and these fire at most once a game minute. Keeping the
        /// subscription is cheaper than counting pages across reloads.
        /// </summary>
        private static string Watch(string topic)
        {
            topic = (topic ?? "").Trim().ToLowerInvariant();
            if (_watching.Contains(topic)) return "ok";

            switch (topic)
            {
                case "money": S1API.Money.Money.OnBalanceChanged += () => Push("money", Money); break;
                case "time": TimeManager.OnTick += () => Push("time", Time); break;
                case "hour": TimeManager.OnHourPass += () => Push("hour", Time); break;
                case "day": TimeManager.OnDayPass += () => Push("day", Time); break;
                case "sleep": TimeManager.OnSleepEnd += _ => Push("sleep", Time); break;
                case "xp": LevelManager.OnXPChanged += (_, __) => Push("xp", Leveling); break;
                case "rank": LevelManager.OnRankUp += (_, __) => Push("rank", Leveling); break;
                case "delivery":
                    DeliveryRegistry.Created += d => Push("delivery", () => DeliveryEvent("created", d));
                    DeliveryRegistry.StatusChanged += (d, _, __) => Push("delivery", () => DeliveryEvent("status", d));
                    DeliveryRegistry.Completed += d => Push("delivery", () => DeliveryEvent("completed", d));
                    break;
                default: return "unknown";
            }

            _watching.Add(topic);
            return "ok";
        }

        private static string DeliveryEvent(string kind, Delivery d) =>
            "{\"kind\":" + Obj.Quote(kind) + ",\"delivery\":" + (d == null ? "null" : DeliveryJson(d)) + "}";

        private static void Push(string topic, Func<string> payload)
        {
            string json = Guard("event " + topic, payload);
            try { _emit?.Invoke(null, "s1api:" + topic, json); }
            catch (Exception e) { Report("event " + topic, e); }
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
                return "error";

            apply(amount);
            return "ok";
        }

        // ---------------------------------------------------------------- plumbing --

        private static string Guard(string what, Func<string> body)
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
        private static void Report(string what, Exception e)
        {
            string key = what + "|" + e.GetType().Name;
            if (!_reported.Add(key)) return;
            _warn?.Invoke($"s1api.{what}: {e.GetType().Name}: {e.Message}");
        }

        private static string Safe(Func<string> read)
        {
            try { return read(); }
            catch { return null; }
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
