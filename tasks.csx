//tasks: two tools over the task store of the project gatto opened.
//it does not ship with gatto, so it loads unvetted and its tools ask for permission.
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;

public sealed record ArcFile(JsonObject? Data, List<Problem> Problems);

public static class Arc
{
    public const string Head = "const ARC = ";

    sealed class Frame(string path, bool isObject)
    {
        public readonly string At = path;
        public readonly bool IsObject = isObject;
        public readonly HashSet<string> Names = new(StringComparer.Ordinal);
        public string Pending = "";
        public int Index;
    }

    public static ArcFile Load(string root)
    {
        var path = Path.Combine(root, "arc.js");
        return File.Exists(path) ? Parse(File.ReadAllBytes(path)) : Fail("arc.js does not exist");
    }

    static ArcFile Fail(string message) => new(null, [new Problem("0", "arc.js", message)]);

    static string ChildPath(Stack<Frame> frames)
    {
        if (frames.Count == 0) return "ARC";
        var parent = frames.Peek();
        return parent.IsObject ? parent.At + "." + parent.Pending : parent.At + "[" + parent.Index++ + "]";
    }

    //the scan refuses a property given twice at any depth, because a JSON reader keeps the last value without a word
    public static ArcFile Parse(byte[] bytes)
    {
        string text;
        try
        {
            var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            text = Tracker.Utf8.GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            return Fail("arc.js is not valid UTF-8");
        }
        var end = text.LastIndexOf(';');
        if (!text.StartsWith(Head, StringComparison.Ordinal) || end < Head.Length || text[(end + 1)..].Trim().Length > 0)
            return Fail("arc.js is not const ARC = , a JSON document and a semicolon");
        var json = Tracker.Utf8.GetBytes(text[Head.Length..end]);
        var problems = new List<Problem>();
        var frames = new Stack<Frame>();
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                {
                    frames.Pop();
                    continue;
                }
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var top = frames.Peek();
                    var name = reader.GetString() ?? "";
                    if (!top.Names.Add(name)) problems.Add(new Problem("0", "arc.js", $"{top.At} holds the property '{name}' twice, and JSON keeps only the last"));
                    top.Pending = name;
                    continue;
                }
                var at = ChildPath(frames);
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) frames.Push(new Frame(at, reader.TokenType == JsonTokenType.StartObject));
            }
        }
        catch (JsonException e)
        {
            //the parser's own message ends with a zero-based position, so it is cut and the one-based line stays the only number
            var reason = e.Message.Split(" LineNumber:")[0].Trim().TrimEnd('.');
            return Fail($"arc.js is not valid JSON at line {(e.LineNumber ?? 0) + 1}: {reason}");
        }
        if (problems.Count > 0) return new ArcFile(null, problems);
        try
        {
            return JsonNode.Parse(json) is JsonObject data ? new ArcFile(data, problems) : Fail("arc.js does not hold a JSON object");
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return Fail("arc.js cannot be read: " + e.Message);
        }
    }
}

public static class ArcRules
{
    public static readonly string[] Numbers = ["0", "1", "2", "3", "4", "5", "6", "8", "9'", "10", "11", "12"];
    static readonly string[] EraStates = ["shipped", "current", "planned"];
    static readonly string[] NodeStates = ["shipped", "inflight", "planned"];
    static readonly Regex StrictTag = new("^v[0-9]+[.][0-9]+[.][0-9]+$");
    static readonly Regex KeyShape = new("^[a-z0-9]+$");
    static readonly Regex DateShape = new("^[0-9]{4}-[0-9]{2}-[0-9]{2}$");
    static readonly JsonSerializerOptions Quote = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    sealed record Ctx(List<JsonObject> Eras, List<string> Tags);

    //one row per rule of the Node checker: its number there, the rule that holds it here, and the case that proves it
    public static readonly (string Node, string Here, string Proof)[] Mapping =
    [
        ("0", "0", "an era state outside the enum, a bucket without a meaning, a property given twice, broken JSON"),
        ("0, a bucket items array", "dropped", "U2: a bucket the arc does not list"),
        ("1", "1", "a second current era"),
        ("2", "2", "a duplicated era key"),
        ("3", "3", "a shipped era after a planned one"),
        ("4", "4", "two in-flight nodes in one funnel"),
        ("5", "5", "an in-flight node under a shipped era"),
        ("6", "6", "a shipped node under a planned era"),
        ("7", "U4", "a wave that names no era"),
        ("8, a done stamp", "U2", "a date that does not exist"),
        ("8, a funnel date", "8", "a shipped node without a date, a planned node with a date"),
        ("9'", "9'", "a cut version no era claims"),
        ("10", "10", "an era carrying a positional id"),
        ("11", "11", "an era shipping a cut version while still current"),
        ("12", "12", "detail on an era that has a funnel"),
        ("13", "U5", "an open item waved at a shipped era"),
        ("14, a key twice in a row", "U2", "a duplicate key"),
        ("14, a key twice in the arc", "0", "a property given twice"),
    ];

    public static bool IsStrictTag(string tag) => StrictTag.IsMatch(tag);

    public static bool IsString(JsonNode? n) => n is not null && n.GetValueKind() == JsonValueKind.String;

    static JsonObject Obj(JsonNode? n) => n as JsonObject ?? new JsonObject();

    public static List<JsonObject> EraList(JsonObject d) => d["eras"] is JsonArray a ? a.Select(Obj).ToList() : [];

    public static List<JsonObject> Funnel(JsonObject e) => e["funnel"] is JsonArray a ? a.Select(Obj).ToList() : [];

    public static string? State(JsonObject o) => IsString(o["state"]) ? o["state"]!.GetValue<string>() : null;

    //the text JavaScript's String() gives a property, where an absent one reads undefined
    public static string Text(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var v)) return "undefined";
        if (v is null) return "null";
        return v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString(Quote);
    }

    static string Js(JsonObject o, string key) => !o.TryGetPropertyValue(key, out var v) ? "undefined" : v is null ? "null" : v.ToJsonString(Quote);

    static string Js(JsonNode? n) => n is null ? "null" : n.ToJsonString(Quote);

    static bool Truthy(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var v) || v is null) return false;
        return v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.GetValue<double>() != 0,
            _ => true,
        };
    }

    static List<JsonNode?> ShipsNodes(JsonObject e)
    {
        if (!e.TryGetPropertyValue("ships", out var s) || s is null) return [];
        return s is JsonArray a ? a.ToList() : [s];
    }

    public static List<string> Ships(JsonObject e) => ShipsNodes(e).Select(n => n is null ? "null" : n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString(Quote)).ToList();

    public static IEnumerable<string> Shape(JsonObject d)
    {
        if (d["eras"] is not JsonArray eras)
        {
            yield return "eras is not an array";
            yield break;
        }
        if (d["buckets"] is not JsonArray) yield return "buckets is not an array";
        if (!IsString(d["updated"])) yield return "updated is not a string";
        if (d.ContainsKey("note") && !IsString(d["note"])) yield return "note is present but not a string";
        for (var i = 0; i < eras.Count; i++)
        {
            var e = Obj(eras[i]);
            var at = $"eras[{i}] ({Text(e, "key")})";
            if (!IsString(e["key"])) yield return $"{at}: key is not a string";
            if (!IsString(e["title"])) yield return $"{at}: title is not a string";
            if (State(e) is not { } state || !EraStates.Contains(state)) yield return $"{at}: state {Js(e, "state")} is not shipped|current|planned";
            if (e.ContainsKey("funnel") && e["funnel"] is not JsonArray)
            {
                yield return $"{at}: funnel is not an array";
                continue;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var funnel = Funnel(e);
            for (var j = 0; j < funnel.Count; j++)
            {
                var n = funnel[j];
                var nat = $"{Text(e, "key")}.funnel[{j}] ({Text(n, "id")})";
                if (!IsString(n["id"])) yield return $"{nat}: id is not a string";
                else if (!seen.Add(n["id"]!.GetValue<string>())) yield return $"{nat}: duplicate node id within the funnel";
                if (!IsString(n["title"])) yield return $"{nat}: title is not a string";
                if (!IsString(n["desc"])) yield return $"{nat}: desc is not a string";
                if (State(n) is not { } nodeState || !NodeStates.Contains(nodeState)) yield return $"{nat}: state {Js(n, "state")} is not shipped|inflight|planned";
            }
        }
        if (d["buckets"] is JsonArray buckets)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < buckets.Count; i++)
            {
                var b = Obj(buckets[i]);
                var at = $"buckets[{i}] ({Text(b, "key")})";
                if (!IsString(b["key"])) yield return $"{at}: key is not a string";
                else if (!keys.Add(b["key"]!.GetValue<string>())) yield return $"{at}: key appears more than once";
                if (!IsString(b["title"])) yield return $"{at}: title is not a string";
                if (!IsString(b["meaning"])) yield return $"{at}: meaning is not a string";
            }
        }
    }

    static IEnumerable<string> R1(Ctx c)
    {
        var current = c.Eras.Where(e => State(e) == "current").ToList();
        if (current.Count == 0) yield return "no era is current — between waves, promote the next PLANNED era to current (it simply has no funnel yet)";
        else if (current.Count > 1) yield return $"{current.Count} eras are current: {string.Join(", ", current.Select(e => Text(e, "key")))}";
    }

    static IEnumerable<string> R2(Ctx c)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in c.Eras)
        {
            var key = Text(e, "key");
            if (!KeyShape.IsMatch(key)) yield return $"era key {Js(e, "key")} is not ^[a-z0-9]+$";
            if (!seen.Add(key)) yield return $"era key {Js(e, "key")} appears more than once";
        }
    }

    static int Rank(JsonObject e) => State(e) switch { "shipped" => 0, "current" => 1, "planned" => 2, _ => -1 };

    static IEnumerable<string> R3(Ctx c)
    {
        for (var i = 1; i < c.Eras.Count; i++)
        {
            var a = c.Eras[i - 1];
            var b = c.Eras[i];
            if (Rank(b) >= 0 && Rank(a) >= 0 && Rank(b) < Rank(a))
                yield return $"era {Text(b, "key")} ({Text(b, "state")}) sits after {Text(a, "key")} ({Text(a, "state")}) — the array runs shipped, then the one current, then planned";
        }
    }

    static IEnumerable<string> R4(Ctx c)
    {
        foreach (var e in c.Eras)
        {
            var inflight = Funnel(e).Count(n => State(n) == "inflight");
            if (inflight > 1) yield return $"era {Text(e, "key")}: {inflight} nodes are in flight; at most one may be";
        }
    }

    static IEnumerable<string> R5(Ctx c)
    {
        foreach (var e in c.Eras.Where(e => State(e) == "shipped"))
            foreach (var n in Funnel(e).Where(n => State(n) == "inflight"))
                yield return $"era {Text(e, "key")} is shipped but node {Text(n, "id")} is still in flight";
    }

    static IEnumerable<string> R6(Ctx c)
    {
        foreach (var e in c.Eras.Where(e => State(e) == "planned"))
            foreach (var n in Funnel(e).Where(n => State(n) != "planned"))
                yield return $"era {Text(e, "key")} is planned but node {Text(n, "id")} is {Text(n, "state")}";
    }

    static IEnumerable<string> R8(Ctx c)
    {
        foreach (var e in c.Eras)
            foreach (var n in Funnel(e))
            {
                if (State(n) == "shipped" && !DateShape.IsMatch(Text(n, "date")))
                    yield return $"{Text(e, "key")}.{Text(n, "id")} is shipped but its date is {Js(n, "date")}; want YYYY-MM-DD";
                if (State(n) != "shipped" && n.ContainsKey("date"))
                    yield return $"{Text(e, "key")}.{Text(n, "id")} is {Text(n, "state")} but carries a date";
            }
    }

    static IEnumerable<string> R9(Ctx c)
    {
        var claimed = new HashSet<string>(c.Eras.SelectMany(Ships), StringComparer.Ordinal);
        foreach (var tag in c.Tags.Where(tag => !claimed.Contains(tag)))
            yield return $"tag {tag} is cut in core/ but no era claims it in ships";
    }

    static IEnumerable<string> R10(Ctx c)
    {
        foreach (var e in c.Eras)
        {
            if (e.ContainsKey("id")) yield return $"era {Text(e, "key")} carries an id field — position is array order, never data";
            foreach (var s in ShipsNodes(e))
            {
                var text = s is null ? "null" : s.GetValueKind() == JsonValueKind.String ? s.GetValue<string>() : s.ToJsonString(Quote);
                if (!StrictTag.IsMatch(text)) yield return $"era {Text(e, "key")}: ships entry {Js(s)} is not vN.N.N";
            }
        }
    }

    static IEnumerable<string> R11(Ctx c)
    {
        var tagged = new HashSet<string>(c.Tags, StringComparer.Ordinal);
        foreach (var e in c.Eras)
        {
            var ships = Ships(e);
            if (ships.Count == 0) continue;
            var allTagged = ships.All(tagged.Contains);
            if (allTagged && State(e) != "shipped")
                yield return $"era {Text(e, "key")} ships {string.Join(", ", ships)} — all cut in core/ - but is {Text(e, "state")}; flip it to shipped";
            if (!allTagged && State(e) == "shipped")
                yield return $"era {Text(e, "key")} is shipped but {string.Join(", ", ships.Where(s => !tagged.Contains(s)))} was never tagged in core/";
        }
    }

    static IEnumerable<string> R12(Ctx c)
    {
        foreach (var e in c.Eras.Where(e => Funnel(e).Count > 0 && Truthy(e, "detail")))
            yield return $"era {Text(e, "key")} has a funnel and a detail — the detail renders nowhere; fold it into note or delete it";
    }

    static readonly (string Number, Func<Ctx, IEnumerable<string>> Rule)[] Table =
    [
        ("1", R1), ("2", R2), ("3", R3), ("4", R4), ("5", R5), ("6", R6), ("8", R8), ("9'", R9), ("10", R10), ("11", R11), ("12", R12),
    ];

    //rule 0 is the gate, because every later rule reads a shape it assumes
    public static List<Problem> Run(JsonObject data, (bool Skipped, string Reason, List<string> List) tags)
    {
        var problems = Shape(data).Select(m => new Problem("0", "arc.js", m)).ToList();
        if (problems.Count > 0) return problems;
        var ctx = new Ctx(EraList(data), tags.List);
        foreach (var (number, rule) in Table)
        {
            if (tags.Skipped && (number == "9'" || number == "11")) continue;
            problems.AddRange(rule(ctx).Select(m => new Problem(number, "arc.js", m)));
        }
        return problems;
    }
}

public static class GitRun
{
    //the failure text names which of four causes stopped the run, because one reason for all of them sends the fix to the wrong place
    public static (string? Output, string Failure) Run(string program, string dir, string? stdin, params string[] args)
    {
        if (!Directory.Exists(dir)) return (null, $"the folder {dir} does not exist");
        var psi = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(dir);
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process? started;
        try
        {
            started = Process.Start(psi);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (null, $"{program} could not start ({e.Message})");
        }
        if (started is null) return (null, $"{program} could not start");
        using var p = started;
        try
        {
            if (stdin is not null)
            {
                p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }
            var error = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(30000))
            {
                p.Kill(true);
                return (null, $"{program} ran past 30 seconds in {dir}");
            }
            var firstError = error.Result.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "no message";
            return p.ExitCode == 0 ? (output.Result.Trim(), "") : (null, $"{program} exited {p.ExitCode} in {dir}: {firstError}");
        }
        catch (IOException e)
        {
            return (null, $"{program} broke its pipes in {dir} ({e.Message})");
        }
    }

    public static string? Output(string dir, string? stdin, params string[] args) => Run("git", dir, stdin, args).Output;
}

public sealed class Item
{
    public string Name = "";
    public string Body = "";
    public bool HeaderIntact = true;
    public readonly Dictionary<string, string> Keys = new(StringComparer.Ordinal);

    public string? this[string key] => Keys.TryGetValue(key, out var v) ? v : null;
    public string Id => this["id"] ?? Name;
    public bool IsOpen => this["state"] == "open";
    public IEnumerable<string> Tags => (this["tags"] ?? "").Split(", ", StringSplitOptions.RemoveEmptyEntries);

    public Item Clone()
    {
        var c = new Item { Name = Name, Body = Body, HeaderIntact = HeaderIntact };
        foreach (var kv in Keys) c.Keys[kv.Key] = kv.Value;
        return c;
    }
}

//the store root is the nearest directory upward holding both tasks and arc.js, so a run from any folder under a project reads that project's store
public static class StoreRoot
{
    public const string NotFound = "no tracker store above {0}: a store is a folder holding tasks\\ and arc.js";

    public static string? Locate(string start)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "tasks")) && File.Exists(Path.Combine(dir.FullName, "arc.js")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}

public sealed record Problem(string Rule, string Where, string Message)
{
    public override string ToString() => $"rule {Rule}: {Where}: {Message}";
}

public sealed record ListFilter(string? Priority, string? Bucket, string? Wave, string? Owner, string? Tag, string State);

//every read the cli prints is a function here that returns its text, so a caller without the cli prints the same bytes
public static class Queries
{
    public const int RuleCount = 24;

    static StringWriter Text() => new() { NewLine = "\n" };

    static int Rank(Item i) => i["priority"] switch { "now" => 0, "next" => 1, "later" => 2, _ => 3 };

    public static string List(string root, ListFilter f)
    {
        var o = Text();
        var (items, problems) = Store.Load(root);
        IEnumerable<Item> rows = items;
        if (f.State == "open") rows = rows.Where(i => i.IsOpen);
        if (f.State == "done") rows = rows.Where(i => i["state"] == "done");
        if (f.Priority is { } priority) rows = rows.Where(i => i["priority"] == priority);
        if (f.Bucket is { } bucket) rows = rows.Where(i => i["bucket"] == bucket);
        //a wave filter matches the era key, so hard also lists the items waved hard+
        if (f.Wave is { } wave) rows = rows.Where(i => i["wave"] is { } w && w.TrimEnd('+') == wave.TrimEnd('+'));
        if (f.Owner is { } owner) rows = rows.Where(i => (i["owner"] ?? "").Split('+').Contains(owner));
        if (f.Tag is { } tag) rows = rows.Where(i => i.Tags.Contains(tag));

        var sep = " " + Tracker.Dot + " ";
        foreach (var i in rows.OrderBy(Rank).ThenBy(i => i["created"] ?? "", StringComparer.Ordinal).ThenBy(i => i.Id, StringComparer.Ordinal))
            o.WriteLine(string.Join(sep, i.Id, i["priority"] ?? "-", i["size"] ?? "-", i["owner"] ?? "-", i["bucket"] ?? "-", i["wave"] ?? "-", i["title"] ?? "-"));
        if (problems.Count > 0) o.WriteLine($"the store holds {problems.Count} violation(s), so run check");
        return o.ToString();
    }

    public static string Mine(string root, string seat) => List(root, new ListFilter(null, null, null, seat, null, "open"));

    //named apart from the seats type, because a method named like a type shadows it here and a script cannot reach past with a global alias
    public static string SeatList(string root)
    {
        var o = Text();
        var (seats, present) = Seats.Load(root);
        if (!present) { o.WriteLine("no seats.md; seat add <name> \"<line>\" declares the first seat"); return o.ToString(); }
        var (items, _) = Store.Load(root);
        foreach (var s in seats)
            o.WriteLine($"{items.Count(i => i.IsOpen && (i["owner"] ?? "").Split('+').Contains(s.Name)),4} {s.Name}: {s.Line}");
        return o.ToString();
    }

    public static (string Text, int Code) Show(string root, string id)
    {
        var o = Text();
        var (path, item, problems) = Store.Find(root, id);
        if (path is null || item is null || problems.Count > 0)
        {
            foreach (var p in problems) o.WriteLine("refused: " + p);
            return (o.ToString(), 1);
        }
        return (Writer.Text(item), 0);
    }

    public static string Tags(string root)
    {
        var o = Text();
        var (items, _) = Store.Load(root);
        foreach (var g in items.SelectMany(i => i.Tags).GroupBy(t => t, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            o.WriteLine($"{g.Count(),4} {g.Key}");
        return o.ToString();
    }

    //a seat reads this at session start in place of roadmap.js, which renders every item for a browser and costs a context window to open
    public static string Position(string root)
    {
        var o = Text();
        var (items, problems) = Store.Load(root);
        var arc = Arc.Load(root);
        var sep = " " + Tracker.Dot + " ";
        var current = arc.Data is null ? null : ArcRules.EraList(arc.Data).FirstOrDefault(e => ArcRules.State(e) == "current");
        if (current is null) o.WriteLine("no current era: arc.js is unreadable or names none, so run check");
        else
        {
            o.WriteLine(string.Join(sep, "era " + ArcRules.Text(current, "key"), ArcRules.Text(current, "title"), "ships " + string.Join(", ", ArcRules.Ships(current))));
            foreach (var n in ArcRules.Funnel(current))
                o.WriteLine("  " + string.Join(sep, ArcRules.Text(n, "id"), ArcRules.Text(n, "state"), ArcRules.Text(n, "title")));
        }
        var open = items.Where(i => i.IsOpen).ToList();
        var listed = open.Where(i => i["priority"] is "now" or "next").OrderBy(Rank).ThenBy(i => i["created"] ?? "", StringComparer.Ordinal).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        o.WriteLine($"open: {open.Count} items, {listed.Count} at now or next, {open.Count - listed.Count} later");
        foreach (var i in listed)
            o.WriteLine("  " + string.Join(sep, i.Id, i["priority"], i["owner"] ?? "-", i["wave"] ?? "-", i["title"] ?? "-"));
        if (problems.Count > 0) o.WriteLine($"the store holds {problems.Count} violation(s), so run check");
        return o.ToString();
    }

    //a missing tasks folder reads as 0 items, so the item count on the last line shows a run against the wrong root
    public static (string Text, int Code) Check(string root, (bool Skipped, string Reason, List<string> List) tags, IEnumerable<Problem> extra)
    {
        var o = Text();
        var (items, read) = Store.Load(root);
        var arc = Arc.Load(root);
        if (tags.Skipped) o.WriteLine($"rule 9' + 11 SKIPPED — {tags.Reason}; release coverage unverified");
        var problems = arc.Data is null ? new List<Problem>() : ArcRules.Run(arc.Data, tags);
        problems.AddRange(Rules.Check(items, read, Store.Scratchpad(root), arc.Data, Seats.Load(root)));
        problems.AddRange(Seats.Lines(root));
        problems.AddRange(extra);
        problems.AddRange(Rules.Assets(root, items));
        foreach (var p in problems) o.WriteLine(p);
        var head = $"tracker: {items.Count} items, {ArcRules.EraList(arc.Data ?? new JsonObject()).Count} eras, {RuleCount} rules";
        if (problems.Count > 0)
        {
            o.WriteLine($"{head}, {problems.Count} violation(s), exit 1");
            return (o.ToString(), 1);
        }
        if (tags.Skipped)
        {
            o.WriteLine($"{head}, 0 violations, but the tag rules did not run, exit 2");
            return (o.ToString(), 2);
        }
        o.WriteLine($"{head}, 0 violations");
        return (o.ToString(), 0);
    }
}

public static class Rules
{
    //U1 and U2 read one item's header
    public static List<Problem> Values(Item it, ISet<string> buckets)
    {
        var p = new List<Problem>();
        var f = it.Name;
        var id = it["id"];
        if (id is null) p.Add(new Problem("U1", f, "the header holds no id"));
        else if (f != id + ".md") p.Add(new Problem("U1", f, $"the file name is not its id {id}"));

        foreach (var key in Tracker.Required)
            if (key != "id" && it[key] is null) p.Add(new Problem("U2", f, $"the header has no {key}"));
        foreach (var (key, v) in it.Keys)
        {
            if (v.Length == 0) p.Add(new Problem("U2", f, $"{key} has an empty value"));
            else if (v != v.Trim()) p.Add(new Problem("U2", f, $"{key} has a space at an edge"));
            if (v.Contains('\n') || v.Contains(Tracker.CR)) p.Add(new Problem("U2", f, $"{key} holds a line break"));
        }
        if (id is not null && !Shapes.Id.IsMatch(id)) p.Add(new Problem("U2", f, $"id '{id}' is not U- and four digits"));
        Enum(p, it, "state", Tracker.States);
        Enum(p, it, "priority", Tracker.Priorities);
        Enum(p, it, "size", Tracker.Sizes);
        if (it["owner"] is { } owner && !Shapes.Owner.IsMatch(owner)) p.Add(new Problem("U2", f, $"owner '{owner}' is not ? or lowercase names joined with +"));
        if (it["bucket"] is { } bucket && !buckets.Contains(bucket)) p.Add(new Problem("U2", f, $"bucket '{bucket}' is not one of {string.Join(", ", buckets.Order(StringComparer.Ordinal))}"));
        if (it["wave"] is { } wave && !Shapes.Wave.IsMatch(wave)) p.Add(new Problem("U2", f, $"wave '{wave}' is not an era key with an optional +"));
        if (it["tags"] is { } tags)
        {
            var parts = tags.Split(", ");
            foreach (var t in parts)
                if (!Shapes.Tag.IsMatch(t)) p.Add(new Problem("U2", f, $"tag '{t}' is not lowercase letters, digits and hyphens"));
            if (parts.Distinct(StringComparer.Ordinal).Count() != parts.Length) p.Add(new Problem("U2", f, "a tag appears twice"));
        }
        foreach (var key in new[] { "created", "done" })
            if (it[key] is { } date && !Tracker.IsDate(date)) p.Add(new Problem("U2", f, $"{key} '{date}' is not a YYYY-MM-DD date"));
        return p;
    }

    static void Enum(List<Problem> p, Item it, string key, string[] allowed)
    {
        if (it[key] is { } v && Array.IndexOf(allowed, v) < 0) p.Add(new Problem("U2", it.Name, $"{key} '{v}' is not one of {string.Join(", ", allowed)}"));
    }

    //U3 holds the keys that each state requires and forbids
    public static IEnumerable<Problem> State(Item it)
    {
        if (it["state"] == "open")
        {
            if (it["priority"] is null) yield return new Problem("U3", it.Name, "an open item has no priority");
            if (it["bucket"] is null) yield return new Problem("U3", it.Name, "an open item has no bucket");
            if (it["done"] is not null) yield return new Problem("U3", it.Name, "an open item carries a done date");
        }
        else if (it["state"] == "done")
        {
            if (it["done"] is null) yield return new Problem("U3", it.Name, "a done item has no done date");
            if (it["priority"] is not null) yield return new Problem("U3", it.Name, "a done item carries a priority");
        }
    }

    public static IEnumerable<Problem> AcrossItems(List<Item> items, string? scratchpad)
    {
        foreach (var g in items.Where(i => i["id"] is not null).GroupBy(i => i["id"]!, StringComparer.Ordinal).Where(g => g.Count() > 1))
            yield return new Problem("U1", g.Key, "the id is held by " + string.Join(", ", g.Select(i => i.Name)));

        var now = items.Where(i => i.IsOpen && i["priority"] == "now").ToList();
        if (now.Count > Tracker.NowCap)
            yield return new Problem("U6", "tasks/", $"{now.Count} open items hold priority now, and the cap is {Tracker.NowCap}");

        foreach (var i in now.Where(i => i["owner"] == "?"))
            yield return new Problem("U7", i.Name, "a now item has owner ?");
        foreach (var i in now.Where(i => i["size"] == "?"))
            yield return new Problem("U7", i.Name, "a now item has size ?");

        foreach (var i in items.Where(i => i.IsOpen && i["priority"] == "next" && i["wave"] is null && i["bucket"] != "ready"))
            yield return new Problem("U8", i.Name, "a next item has no wave and is not in ready");

        if (scratchpad is not null)
        {
            var ids = new HashSet<string>(items.Select(i => i["id"]).OfType<string>(), StringComparer.Ordinal);
            foreach (var m in Shapes.Mention.Matches(scratchpad).Select(m => m.Value).Distinct(StringComparer.Ordinal))
                if (!ids.Contains(m)) yield return new Problem("U9", "scratchpad.md", $"it names {m}, and no item holds that id");
        }
    }

    public static List<Problem> Check(List<Item> items, List<Problem> read, string? scratchpad, JsonObject? arc, (List<Seat> List, bool Present) seats) =>
        read.Concat(items.SelectMany(State)).Concat(AcrossItems(items, scratchpad)).Concat(Eras(items, arc)).Concat(items.SelectMany(i => Seats.Check(i, seats.List, seats.Present))).ToList();

    //U11 re-runs the asset census, so the store cannot embed a missing image, keep an image its body does not embed once, or keep a folder with no item
    public static IEnumerable<Problem> Assets(string root, List<Item> items)
    {
        var dir = Path.Combine(Store.TasksDir(root), "assets");
        foreach (var it in items)
        {
            var own = Path.Combine(dir, it.Id);
            var files = Directory.Exists(own) ? Directory.EnumerateFiles(own).Select(f => Path.GetFileName(f)).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (id, file) in Shapes.Embeds(it.Body))
            {
                if (id == it.Id && files.Contains(file)) counts[file] = counts.GetValueOrDefault(file) + 1;
                else yield return new Problem("U11", it.Name, $"it embeds assets/{id}/{file}, and assets/{it.Id}/ holds no such file");
            }
            foreach (var file in files.Order(StringComparer.Ordinal))
                if (counts.GetValueOrDefault(file) != 1) yield return new Problem("U11", it.Name, $"its body embeds assets/{it.Id}/{file} {counts.GetValueOrDefault(file)} times, and it must embed the file once");
        }
        if (!Directory.Exists(dir)) yield break;
        var ids = new HashSet<string>(items.Select(i => i.Id), StringComparer.Ordinal);
        foreach (var folder in Directory.EnumerateDirectories(dir).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal))
            if (!ids.Contains(folder)) yield return new Problem("U11", "tasks/assets/" + folder, "the folder names no item");
        foreach (var loose in Directory.EnumerateFiles(dir).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal))
            yield return new Problem("U11", "tasks/assets/" + loose, "a file sits outside every item's folder, so no body can embed it");
    }

    //U4 and U5 read the era keys, so they run only on an arc file that passes rule 0
    public static IEnumerable<Problem> Eras(List<Item> items, JsonObject? arc)
    {
        if (arc is null || ArcRules.Shape(arc).Any()) yield break;
        var eras = ArcRules.EraList(arc);
        var keys = new HashSet<string>(eras.Select(e => ArcRules.Text(e, "key")), StringComparer.Ordinal);
        var shipped = new HashSet<string>(eras.Where(e => ArcRules.State(e) == "shipped").Select(e => ArcRules.Text(e, "key")), StringComparer.Ordinal);
        foreach (var i in items)
        {
            if (i["wave"] is not { } wave) continue;
            var key = wave.TrimEnd('+');
            if (!keys.Contains(key)) yield return new Problem("U4", i.Name, $"wave '{wave}' matches no era key");
            else if (i.IsOpen && shipped.Contains(key))
                yield return new Problem("U5", i.Name, wave.EndsWith('+')
                    ? $"waved '{wave}', but {key} has SHIPPED - 'or later' now means any time, which is not an allocation"
                    : $"waved at '{wave}', which has SHIPPED - stamp it done or re-point it");
        }
    }
}

public sealed record Seat(string Name, string Line);

//seats are declared in the store so a model cannot invent owners; an open item may name only a declared seat or ?
public static class Seats
{
    public const string FileName = "seats.md";
    static readonly Regex NameShape = new("^[a-z0-9][a-z0-9.-]*$");

    public static (List<Seat> List, bool Present) Load(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path)) return ([], false);
        var list = new List<Seat>();
        foreach (var raw in File.ReadAllLines(path, Tracker.Utf8))
        {
            var line = raw.TrimEnd((char)13);
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (line.Length == 0 || colon < 1) continue;
            list.Add(new Seat(line[..colon], line[(colon + 2)..]));
        }
        return (list, true);
    }

    //a line that is not a seat name and its line would drop that seat without a word, and every item it owns would then fail at the item instead of at this file
    public static List<Problem> Lines(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path)) return [];
        var found = new List<Problem>();
        var lines = File.ReadAllLines(path, Tracker.Utf8);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd((char)13);
            if (line.Length == 0) continue;
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 1 || !ValidName(line[..colon]))
                found.Add(new Problem("U24", FileName, $"line {i + 1} is not a lowercase seat name, a colon and one line saying what the seat does"));
        }
        return found;
    }

    public static void Save(string root, List<Seat> list) =>
        File.WriteAllBytes(Path.Combine(root, FileName), Tracker.Utf8.GetBytes(string.Concat(list.Select(s => s.Name + ": " + s.Line + "\n"))));

    public static bool ValidName(string name) => NameShape.IsMatch(name);

    public static IEnumerable<Problem> Check(Item it, List<Seat> seats, bool present)
    {
        if (!it.IsOpen || it["owner"] is not { } owner) yield break;
        foreach (var part in owner.Split('+'))
        {
            if (part == "?" || seats.Any(s => s.Name == part)) continue;
            yield return new Problem("U24", it.Name, present
                ? $"owner '{part}' is not a declared seat"
                : $"no seats.md declares seats, so owner '{part}' cannot be checked");
        }
    }
}

public static class Shapes
{
    public static readonly Regex Id = new("^U-[0-9]{4}$");
    public static readonly Regex FileName = new("^U-([0-9]{4})[.]md$");
    public static readonly Regex Owner = new("^([?]|[a-z0-9][a-z0-9.-]*([+][a-z0-9][a-z0-9.-]*)*)$");
    public static readonly Regex Wave = new("^[a-z0-9]+[+]?$");
    public static readonly Regex Tag = new("^[a-z0-9-]+$");
    public static readonly Regex Mention = new("U-[0-9]{4}");

    //one line form names an item's image, and render and U11 both read it here so the two cannot disagree
    public static readonly Regex Embed = new("^[!][[][^]]*[]][(]assets/(U-[0-9]{4})/([^/() ]+)[)]$");

    public static IEnumerable<(string Id, string File)> Embeds(string body) =>
        body.Split((char)10).Select(line => Embed.Match(line)).Where(m => m.Success).Select(m => (m.Groups[1].Value, m.Groups[2].Value));

    //the page links every asset under the note, so a line that only embeds one leaves the note, and so do the blank lines it leaves
    public static string DropEmbeds(string note)
    {
        var nl = ((char)10).ToString();
        var lines = note.Split((char)10);
        if (!lines.Any(line => Embed.IsMatch(line))) return note;
        var kept = string.Join(nl, lines.Where(line => !Embed.IsMatch(line)));
        while (kept.Contains(nl + nl + nl, StringComparison.Ordinal)) kept = kept.Replace(nl + nl + nl, nl + nl, StringComparison.Ordinal);
        return kept.Trim((char)10);
    }
}

public static class Store
{
    public static string TasksDir(string root) => Path.Combine(root, "tasks");

    public static string? Scratchpad(string root)
    {
        var path = Path.Combine(root, "scratchpad.md");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    //the arc file names the buckets, and when it cannot be read its rule 0 problems are reported and the five open buckets apply
    public static HashSet<string> Buckets(string root, List<Problem> problems)
    {
        var five = new HashSet<string>(Tracker.OpenBuckets, StringComparer.Ordinal);
        var arc = Arc.Load(root);
        if (arc.Data is null)
        {
            problems.AddRange(arc.Problems);
            return five;
        }
        if (arc.Data["buckets"] is not JsonArray list) return five;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in list)
            if (b is JsonObject o && ArcRules.IsString(o["key"]) && o["key"]!.GetValue<string>() is var key && key != "closed") found.Add(key);
        return found;
    }

    public static (List<Item> Items, List<Problem> Problems) Load(string root)
    {
        var problems = new List<Problem>();
        var buckets = Buckets(root, problems);
        var items = new List<Item>();
        var dir = TasksDir(root);
        if (!Directory.Exists(dir)) return (items, problems);
        foreach (var path in Directory.EnumerateFiles(dir, "*.md").OrderBy(p => p, StringComparer.Ordinal))
        {
            var (item, found) = Parse(Path.GetFileName(path), File.ReadAllBytes(path), buckets);
            items.Add(item);
            problems.AddRange(found);
        }
        return (items, problems);
    }

    //returns the problems it finds and never throws on the bytes of a file
    public static (Item Item, List<Problem> Problems) Parse(string name, byte[] bytes, ISet<string> buckets)
    {
        var item = new Item { Name = name };
        var problems = new List<Problem>();
        string text;
        try
        {
            var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            text = Tracker.Utf8.GetString(bytes, start, bytes.Length - start).Replace(Tracker.CR + "\n", "\n");
        }
        catch (DecoderFallbackException)
        {
            item.HeaderIntact = false;
            problems.Add(new Problem("U2", name, "the file is not valid UTF-8"));
            return (item, problems);
        }
        var lines = text.Split('\n');
        var close = lines[0] == "---" ? Array.IndexOf(lines, "---", 1) : -1;
        if (close < 0)
        {
            item.HeaderIntact = false;
            problems.Add(new Problem("U2", name, lines[0] == "---" ? "the header has no closing --- line" : "the file does not open with a --- line"));
            return (item, problems);
        }
        for (var i = 1; i < close; i++)
        {
            var at = lines[i].IndexOf(": ", StringComparison.Ordinal);
            if (at <= 0)
            {
                item.HeaderIntact = false;
                problems.Add(new Problem("U2", name, $"line {i + 1} is not key: value"));
                continue;
            }
            var key = lines[i][..at];
            if (Array.IndexOf(Tracker.KeyOrder, key) < 0)
            {
                item.HeaderIntact = false;
                problems.Add(new Problem("U2", name, $"unknown key '{key}'"));
                continue;
            }
            if (!item.Keys.TryAdd(key, lines[i][(at + 2)..]))
            {
                item.HeaderIntact = false;
                problems.Add(new Problem("U2", name, $"duplicate key '{key}'"));
            }
        }
        var body = string.Join('\n', lines[(close + 1)..]);
        item.Body = body.StartsWith('\n') ? body[1..] : body;
        problems.AddRange(Rules.Values(item, buckets));
        return (item, problems);
    }

    public static (string? Path, Item? Item, List<Problem> Problems) Find(string root, string id)
    {
        var none = new List<Problem>();
        if (!Shapes.Id.IsMatch(id)) return (null, null, [new Problem("U2", id, "it is not U- and four digits")]);
        var path = Path.Combine(TasksDir(root), id + ".md");
        if (!File.Exists(path)) return (null, null, [new Problem("U1", id, $"no file tasks/{id}.md")]);
        var (item, problems) = Parse(id + ".md", File.ReadAllBytes(path), Buckets(root, none));
        return (path, item, none.Concat(problems).ToList());
    }

    //a deleted task file frees no id, because a letter may already cite it, so the next id also passes every name git ever recorded here
    static IEnumerable<string> HistoricalNames(string tasksDir)
    {
        var (output, _) = GitRun.Run("git", tasksDir, null, "log", "--all", "--format=", "--name-only", "--", ".");
        return output is null ? [] : output.Split('\n').Select(line => Path.GetFileName(line.Trim()));
    }

    public static (string? Id, string? Error) Create(string tasksDir, Item item, Action<string>? beforeCreate)
    {
        Directory.CreateDirectory(tasksDir);
        var history = HistoricalNames(tasksDir).ToList();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var number = Directory.EnumerateFiles(tasksDir, "*.md").Select(Path.GetFileName).Concat(history)
                .Select(name => Shapes.FileName.Match(name ?? ""))
                .Where(m => m.Success)
                .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .DefaultIfEmpty(0).Max() + 1;
            if (number > 9999) return (null, "every id up to U-9999 is taken");
            var id = "U-" + number.ToString("D4", CultureInfo.InvariantCulture);
            var next = item.Clone();
            next.Keys["id"] = id;
            next.Name = id + ".md";
            var path = Path.Combine(tasksDir, next.Name);
            beforeCreate?.Invoke(path);
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(Writer.Bytes(next));
                return (id, null);
            }
            catch (IOException) when (File.Exists(path))
            {
                //another writer created this id first, so the next attempt counts the files again
            }
        }
        return (null, "another writer took the next id twice in a row, so run add again");
    }
}

public static class Tracker
{
    public const int NowCap = 5;
    public const char CR = (char)13;
    public static readonly string[] KeyOrder = ["id", "title", "state", "priority", "size", "owner", "bucket", "wave", "trigger", "tags", "created", "done", "source"];
    public static readonly string[] Required = ["id", "title", "state", "size", "owner", "created"];
    public static readonly string[] OpenBuckets = ["ready", "word", "trigger", "plan", "design"];
    public static readonly string[] States = ["open", "done"];
    public static readonly string[] Priorities = ["now", "next", "later"];
    public static readonly string[] Sizes = ["XS", "S", "M", "L", "?"];
    public static readonly UTF8Encoding Utf8 = new(false, true);
    public static readonly string Dot = ((char)0xB7).ToString();

    public static string Today() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool IsDate(string s) => DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}

public static class Writer
{
    public static string Text(Item it)
    {
        var sb = new StringBuilder("---\n");
        foreach (var key in Tracker.KeyOrder)
            if (it.Keys.TryGetValue(key, out var v)) sb.Append(key).Append(": ").Append(v).Append('\n');
        return sb.Append("---\n\n").Append(it.Body).ToString();
    }

    public static byte[] Bytes(Item it) => Tracker.Utf8.GetBytes(Text(it));
}

//every write passes the rules here and returns its lines, so the cli and the extension refuse the same item with the same text
public static class Writes
{
    public static List<Problem> Validate(Item it, string root)
    {
        var arc = new List<Problem>();
        var buckets = Store.Buckets(root, arc);
        var (seats, present) = Seats.Load(root);
        return arc.Concat(Rules.Values(it, buckets)).Concat(Rules.State(it)).Concat(Seats.Check(it, seats, present)).ToList();
    }

    public static string Tags(string raw) => string.Join(", ", raw.Split(',').Select(t => t.Trim()));

    static (int Code, string Text) Refuse(IEnumerable<Problem> problems) => (1, string.Concat(problems.Select(p => "refused: " + p + "\n")));

    public static (int Code, string Text) Add(string root, Item item, string today)
    {
        item.Keys["created"] = today;
        var problems = Validate(item, root);
        if (problems.Count > 0) return Refuse(problems);
        var (id, error) = Store.Create(Store.TasksDir(root), item, null);
        return id is null ? (1, "refused: " + error + "\n") : (0, $"{id} tasks/{id}.md\n");
    }

    public static (int Code, string Text) Close(string root, string id, string note, string today)
    {
        var (path, item, problems) = Store.Find(root, id);
        if (path is null || item is null || problems.Count > 0) return Refuse(problems);
        if (!item.IsOpen) return (1, $"refused: {item.Id} is not open\n");
        var next = item.Clone();
        next.Keys["state"] = "done";
        next.Keys.Remove("priority");
        next.Keys["done"] = today;
        next.Body = note + "\n" + (item.Body.Length > 0 ? "\n" + item.Body : "");

        var found = Validate(next, root);
        if (note.Length == 0 || note.Contains('\n') || note.Contains(Tracker.CR)) found.Add(new Problem("U3", next.Name, "the closing text is one line that is not empty"));
        if (found.Count > 0) return Refuse(found);
        File.WriteAllBytes(path, Writer.Bytes(next));
        return (0, $"{next.Id} closed\n");
    }

    public static (int Code, string Text) Set(string root, string id, string key, string value)
    {
        if (Array.IndexOf(Tracker.KeyOrder, key) < 0) return (1, $"refused: '{key}' is not a header key\n");
        if (key == "id") return (1, "refused: an id never changes\n");
        var (path, item, problems) = Store.Find(root, id);
        //a header that did not read whole would lose its unreadable lines on the write, so set refuses it
        if (path is null || item is null || !item.HeaderIntact) return Refuse(problems);

        var next = item.Clone();
        if (value.Length == 0) next.Keys.Remove(key);
        else next.Keys[key] = key == "tags" ? Tags(value) : value;
        var found = Validate(next, root);
        if (found.Count > 0) return Refuse(found);
        File.WriteAllBytes(path, Writer.Bytes(next));
        return (0, $"{next.Id} {key} {(value.Length == 0 ? "removed" : "set")}\n");
    }
}

//tasks, generated from the tracker repository. two tools, so two schemas enter every request, and the verbs are enum values
//no helper here may share a name with a store type, because a script nests every type beside these functions and the names collide
string StoreDir() => StoreRoot.Locate(Gatto.Cwd) ?? "";
string Missing() => string.Format(StoreRoot.NotFound, Gatto.Cwd);
string SessionSeat()
{
    var s = Gatto.Config.Section("tasks");
    return s is { } el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty("owner", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "gatto";
}
var seat = SessionSeat();
var loadRoot = StoreDir();
var seatMissing = loadRoot.Length > 0 && !Seats.Load(loadRoot).List.Any(x => x.Name == seat)
    ? $"seat '{seat}' is not declared in seats.md at {loadRoot}; seat add <name> \"<line>\" declares it"
    : null;
if (seatMissing is not null) Gatto.Log("tasks: " + seatMissing);

string? Opt(JsonElement args, string k) => args.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

ToolResult ReadView(JsonElement args)
{
    var root = StoreDir();
    if (root.Length == 0) return new ToolResult(Missing());
    return Opt(args, "view") switch
    {
        "list" => new ToolResult(Queries.List(root, new ListFilter(Opt(args, "priority"), Opt(args, "bucket"), null, Opt(args, "owner"), Opt(args, "tag"), Opt(args, "state") ?? "open"))),
        "show" => new ToolResult(Queries.Show(root, Opt(args, "id") ?? throw new InvalidOperationException("show needs id")).Text),
        "position" => new ToolResult(Queries.Position(root)),
        "mine" => new ToolResult(seatMissing ?? Queries.Mine(root, seat)),
        "seats" => new ToolResult(Queries.SeatList(root)),
        "check" => new ToolResult(Queries.Check(root, (true, "no git reader in a script", []), []).Text),
        _ => throw new InvalidOperationException("view must be one of list, show, position, mine, seats, check"),
    };
}

ToolResult WriteAction(JsonElement args)
{
    var root = StoreDir();
    if (root.Length == 0) return new ToolResult(Missing());
    var action = Opt(args, "action");
    string Need(string k) => Opt(args, k) ?? throw new InvalidOperationException($"{action} needs {k}");
    (int Code, string Text) r;
    switch (action)
    {
        case "add":
            var item = new Item { Name = "U-0000.md" };
            item.Keys["id"] = "U-0000";
            item.Keys["title"] = Need("title");
            item.Keys["state"] = "open";
            foreach (var k in new[] { "priority", "size", "owner", "bucket" }) item.Keys[k] = Need(k);
            foreach (var k in new[] { "wave", "trigger", "source" }) if (Opt(args, k) is { } v) item.Keys[k] = v;
            if (Opt(args, "tags") is { } tags) item.Keys["tags"] = Writes.Tags(tags);
            r = Writes.Add(root, item, Tracker.Today());
            break;
        case "set": r = Writes.Set(root, Need("id"), Need("key"), Opt(args, "value") ?? ""); break;
        case "close": r = Writes.Close(root, Need("id"), Need("note"), Tracker.Today()); break;
        default: throw new InvalidOperationException("action must be one of add, set, close");
    }
    //a refusal throws, and the loop turns the message into a tool error that holds only the refused lines
    if (r.Code != 0) throw new InvalidOperationException(r.Text.TrimEnd());
    return new ToolResult(r.Text.TrimEnd());
}

Gatto.Register(
    "task_read",
    "Read the project's tracker store. view: list (items; filter by priority, owner, tag, bucket, state open|done|all), show (one item by id), position (the current era and every now or next item), mine (open items owned by this session's seat), seats (declared seats with open counts), check (rule violations, as text).",
    """
    {"type":"object","properties":{"view":{"type":"string","enum":["list","show","position","mine","seats","check"]},"id":{"type":"string"},"priority":{"type":"string","enum":["now","next","later"]},"owner":{"type":"string"},"tag":{"type":"string"},"bucket":{"type":"string"},"state":{"type":"string","enum":["open","done","all"]}},"required":["view"]}
    """,
    (args, ctx, ct) => Task.FromResult(ReadView(args)),
    readClass: true);

Gatto.Register(
    "task_write",
    "Write to the project's tracker store through its rules; a refusal comes back as an error naming the rule. action: add (title, priority, size, owner, bucket; optional wave, trigger, tags, source; starts one git process to read the id history), set (id, key, value; empty value removes an optional key), close (id, note).",
    """
    {"type":"object","properties":{"action":{"type":"string","enum":["add","set","close"]},"id":{"type":"string"},"key":{"type":"string"},"value":{"type":"string"},"title":{"type":"string"},"priority":{"type":"string"},"size":{"type":"string"},"owner":{"type":"string"},"bucket":{"type":"string"},"wave":{"type":"string"},"trigger":{"type":"string"},"tags":{"type":"string"},"source":{"type":"string"},"note":{"type":"string"}},"required":["action"]}
    """,
    (args, ctx, ct) => Task.FromResult(WriteAction(args)));
