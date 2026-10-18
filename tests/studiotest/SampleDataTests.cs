using System.IO;
using CluadeX.Services.UiStudio.Sketch;

namespace StudioTest;

/// <summary>
/// The sample data the renderer feeds a Flutter app's requests while it draws its screens (FlutterSampleData, through
/// the REAL CluadeX.dll): the requests its code makes, what each reply is read into, the envelope its client unwraps,
/// and each model's JSON shape as its fromJson reads it.
/// </summary>
internal static partial class Program
{
    const string SampleClient = @"import 'package:dio/dio.dart';

/// The envelope every reply comes in: { success, data, message }.
class ApiEnvelope {
  ApiEnvelope(this.success, this.data, this.message);
  final bool success;
  final dynamic data;
  final String? message;

  factory ApiEnvelope.fromJson(Map<String, dynamic> json) =>
      ApiEnvelope(json['success'] == true, json['data'], json['message'] as String?);
}

/// The app's client: gives back what an envelope carries.
class ApiClient {
  ApiClient(this.dio);
  final Dio dio;

  Future<dynamic> get(String path, {Map<String, dynamic>? query}) async {
    final res = await dio.get<Map<String, dynamic>>(path, queryParameters: query);
    return ApiEnvelope.fromJson(res.data ?? const {}).data;
  }
}

class Paged<T> {
  Paged(this.items);
  final List<T> items;

  static Paged<T> parse<T>(dynamic raw, T Function(Map<String, dynamic>) item) {
    final list = raw is List ? raw : (raw is Map ? raw['data'] as List? ?? const [] : const []);
    return Paged<T>(list.whereType<Map>().map((e) => item(e.cast<String, dynamic>())).toList());
  }
}
";

    const string SampleModels = @"part 'models.g.dart';

Map<String, dynamic> _m(dynamic v) => v is Map ? v.cast<String, dynamic>() : const {};

List<Map<String, dynamic>> _list(dynamic v) =>
    v is List ? v.whereType<Map>().map((e) => e.cast<String, dynamic>()).toList() : const [];

class Fmt {
  static int toInt(dynamic v) => v is num ? v.toInt() : int.tryParse('$v') ?? 0;
  static double toDouble(dynamic v) => v is num ? v.toDouble() : double.tryParse('$v') ?? 0;
  static DateTime? date(dynamic v) => v == null ? null : DateTime.tryParse('$v');
}

class Stats {
  const Stats(this.orders, this.revenue, this.items, this.updated, this.mode, this.live, this.split);
  final int orders;
  final double revenue;
  final List<Item> items;
  final DateTime? updated;
  final String mode;
  final bool live;
  final double split;

  factory Stats.fromJson(Map<String, dynamic> j) {
    final top = _m(j['top_metrics']);
    final rev = j['revenue_today'] is Map ? _m(j['revenue_today']) : const <String, dynamic>{};
    return Stats(
      Fmt.toInt(top['orders_count']),
      Fmt.toDouble(top['revenue_thb'] ?? top['revenue']),
      _list(j['items']).map(Item.fromJson).toList(),
      Fmt.date(j['updated_at']),
      (j['mode'] ?? 'smart').toString(),
      j['is_live'] == true,
      Fmt.toDouble((rev['split'] as Map?)?['shop']),
    );
  }
}

class Item {
  const Item(this.name, this.count, this.paid, this.points);
  final String name;
  final int count;
  final bool paid;
  final List<Point> points;

  factory Item.fromJson(Map<String, dynamic> j) =>
      Item(j['name'] as String, j['count'] as int, j['status'] == 'paid', _points(j['trend']));

  static List<Point> _points(dynamic raw) {
    if (raw is! List) return const [];
    return [
      for (final e in raw.whereType<Map>()) Point(DateTime.parse(e['date'] as String), (e['value'] as num).toDouble()),
    ];
  }
}

class Point {
  const Point(this.day, this.value);
  final DateTime day;
  final double value;
}

class Profile {
  Profile({required this.userName, required this.email, required this.age, this.avatarUrl, this.errors = const []});
  final String userName;
  final String email;
  final int age;
  final String? avatarUrl;
  final List<String> errors;

  factory Profile.fromJson(Map<String, dynamic> json) => _$ProfileFromJson(json);
}
";

    const string SampleModelsG = @"part of 'models.dart';

Profile _$ProfileFromJson(Map<String, dynamic> json) => Profile(
      userName: json['user_name'] as String,
      email: json['email'] as String,
      age: (json['age'] as num).toInt(),
      avatarUrl: json['avatar_url'] as String?,
      errors: (json['errors'] as List<dynamic>?)?.map((e) => e as String).toList() ?? const [],
    );
";

    const string SampleRepo = @"import 'package:dio/dio.dart';
import '../core/api_client.dart';
import 'models.dart';

class ShopRepository {
  ShopRepository(this._api, this._dio);
  final ApiClient _api;
  final Dio _dio;

  Future<Stats> stats() async {
    final data = await _api.get('/shop/stats', query: {'period': 'month'});
    return Stats.fromJson(_m(data));
  }

  Future<List<Item>> items(int shopId) async {
    final data = await _api.get('/shops/$shopId/items?page=1');
    return _list(data).map(Item.fromJson).toList();
  }

  Future<Paged<Item>> page() async => Paged.parse(await _api.get('/items'), Item.fromJson);

  Future<Profile> me() async {
    try {
      final res = await _dio.get('https://api.example.test/v2/me');
      return Profile.fromJson(res.data['data']);
    } catch (_) {
      rethrow;
    }
  }

  Future<void> logout() async {
    await _api.get('/auth/logout');
  }

  Future<dynamic> _fetch(String path) async {
    final res = await _dio.get<dynamic>(path);
    final body = _m(res.data);
    if (body['success'] != true) throw Exception('failed');
    return body['data'];
  }

  Future<Stats> summary() async => Stats.fromJson(_m(await _fetch('/summary')));
}
";

    static readonly DateTime SampleNow = new(2026, 10, 18, 9, 30, 0, DateTimeKind.Utc);

    static string Json(System.Text.Json.Nodes.JsonNode? n) =>
        n?.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";

    /// <summary>The sample replies read from a small app's code: its envelope, a model through _m / _list / a converter
    /// class / a static method declared after it / json_serializable's _$XFromJson, a list endpoint, Paged.parse with a
    /// tear-off, a raw Dio reply read as res.data['data'], a helper that takes the path, a request whose reply isn't
    /// read; the values made up from the key names.</summary>
    static void SampleDatas(string temp)
    {
        Console.WriteLine("UI Studio: sample replies read from the app's own code (what a data screen is drawn with)");
        string root = Path.Combine(temp, "sample-app");
        W(root, "pubspec.yaml", "name: sampleapp\n");
        W(root, "lib/core/api_client.dart", SampleClient);
        W(root, "lib/data/models.dart", SampleModels);
        W(root, "lib/data/models.g.dart", SampleModelsG);
        W(root, "lib/data/shop_repository.dart", SampleRepo);
        var data = FlutterSampleData.Build(root, SampleNow);
        SampleReply? R(string path) => data.Replies.FirstOrDefault(r => r.Path == path);

        // what the code reads: the envelope, the requests, what each is read into
        Check("samples: the envelope its client unwraps (json['success'] + json['data'])", data.SuccessKey == "success" && data.DataKey == "data", $"{data.SuccessKey} + {data.DataKey}");
        string list = string.Join(", ", data.Replies.Select(r => $"{r.Method} {r.Path} → {r.Model}"));
        Check("samples: every request with a written path, and what its reply is read into",
              list == "GET /shop/stats → Stats, GET /shops/*/items → [Item], GET /items → [Item], GET /v2/me → Profile, GET /summary → Stats", list);
        Check("samples: a request whose reply isn't read gets none (it keeps failing as before)", string.Join(", ", data.Unread.Select(u => $"{u.Method} {u.Path}")) == "GET /auth/logout",
              string.Join(", ", data.Unread.Select(u => $"{u.Method} {u.Path}")));

        // the replies, exactly: made up from the key names
        const string trend = "[{\"date\":\"2026-10-18T09:30:00Z\",\"value\":1250.5},{\"date\":\"2026-10-17T08:53:00Z\",\"value\":1560.5},{\"date\":\"2026-10-16T08:16:00Z\",\"value\":1870.5}]";
        string Item(int i) => $"{{\"name\":\"{(i == 0 ? "ตัวอย่าง" : "ตัวอย่าง " + (i + 1))}\",\"count\":{12 + 5 * i},\"status\":\"paid\",\"trend\":{trend}}}";
        string items = "[" + string.Join(",", Enumerable.Range(0, 3).Select(Item)) + "]";
        string stats = "{\"top_metrics\":{\"orders_count\":12,\"revenue_thb\":1250.5,\"revenue\":1250.5},\"revenue_today\":{\"split\":{\"shop\":1250.5}},\"items\":" + items
                       + ",\"updated_at\":\"2026-10-18T09:30:00Z\",\"mode\":\"smart\",\"is_live\":true}";
        string got = Json(R("/shop/stats")?.Body);
        Check("samples: a model in the envelope — nested maps (_m), a converter class (Fmt.toInt / toDouble / date), a list of models (_list(…).map(Item.fromJson)), a static method declared after its factory (_points), a value it compares with ('paid'), its ?? default ('smart'), a flag (== true), an amount the keys around it name (revenue_today/split)",
              got == "{\"success\":true,\"data\":" + stats + "}", got);
        got = Json(R("/shops/*/items")?.Body);
        Check("samples: a list endpoint (_list(data).map(Item.fromJson)) — three items, each its own", got == "{\"success\":true,\"data\":" + items + "}", got);
        got = Json(R("/items")?.Body);
        Check("samples: Paged.parse(…, Item.fromJson) — a list of the tear-off's model", got == "{\"success\":true,\"data\":" + items + "}", got);
        got = Json(R("/v2/me")?.Body);
        Check("samples: a raw Dio reply read as res.data['data'] (a whole URL; json_serializable's _$ProfileFromJson in the .g.dart) — no envelope added, a person's name, an email, no picture, no errors",
              got == "{\"data\":{\"user_name\":\"สมชาย ใจดี\",\"email\":\"user1@example.com\",\"age\":12,\"avatar_url\":null,\"errors\":[]}}", got);
        got = Json(R("/summary")?.Body);
        Check("samples: a helper that takes the path (_fetch(String path)) — what it gives back (body['data']) read at its call", got == "{\"success\":true,\"data\":" + stats + "}", got);

        // how the renderer matches a request to its reply
        var shop = R("/shops/*/items");
        var re = shop != null ? new System.Text.RegularExpressions.Regex(shop.Pattern) : null;
        Check("samples: a path's pattern — an interpolated segment matches any one, after the base URL's path",
              shop is { Pattern: "(^|/)shops/[^/]+/items/?$", Weight: 2 } && re!.IsMatch("/api/v1/shops/7/items") && !re.IsMatch("/api/myshops/7/items") && !re.IsMatch("/shops/7/items/more"),
              $"{shop?.Pattern} {shop?.Weight}");
        try
        {
            using var file = System.Text.Json.JsonDocument.Parse(data.ToJson());
            var first = file.RootElement[0];
            Check("samples: the file the test reads — method, pattern, weight and body for each reply",
                  file.RootElement.GetArrayLength() == 5 && first.GetProperty("method").GetString() == "GET" && first.GetProperty("pattern").GetString() == "(^|/)shop/stats/?$"
                  && first.GetProperty("weight").GetInt32() == 2 && first.GetProperty("body").GetProperty("data").GetProperty("mode").GetString() == "smart");
        }
        catch (Exception ex) { Check("samples: the file the test reads is JSON", false, ex.Message); }

        // an app without an envelope: its replies are the models themselves
        string bare = Path.Combine(temp, "sample-bare");
        W(bare, "pubspec.yaml", "name: bare\n");
        W(bare, "lib/data/models.dart", SampleModels.Replace("part 'models.g.dart';", ""));
        W(bare, "lib/data/repo.dart", "import 'models.dart';\n\nclass Repo {\n  Repo(this.client);\n  final dynamic client;\n\n  Future<Stats> stats() async {\n    final data = await client.get('/stats');\n    return Stats.fromJson(data as Map<String, dynamic>);\n  }\n}\n");
        var bareData = FlutterSampleData.Build(bare, SampleNow);
        got = Json(bareData.Replies.FirstOrDefault()?.Body);
        Check("samples: no envelope in the code — the reply is the model itself", bareData.DataKey == null && got == stats, got);
        Check("samples: an app without a lib folder has none", FlutterSampleData.Build(Path.Combine(temp, "no-such-app"), SampleNow).Replies.Count == 0);
    }

    static System.Text.Json.Nodes.JsonNode? N(string json) => System.Text.Json.Nodes.JsonNode.Parse(json);

    /// <summary>One value of the owner's override file over the made-up one (FlutterSampleData.MergeValue), exactly, and
    /// where a value kept the sample's.</summary>
    static string MergeOf(string made, string over, out string kept)
    {
        var where = new List<string>();
        string got = Json(FlutterSampleData.MergeValue(N(made), N(over), where));
        kept = string.Join(" ", where);
        return got;
    }

    /// <summary>The owner's realistic values (the AI's override file, kept in the Studio's store) merged over the replies
    /// made up from the code: value by value, so a value of another kind than the code reads can't make the app fail; a
    /// reply given without the app's envelope; requests the code doesn't make left out; the file read, cached, and refused
    /// with its reason when it can't be used; the models' files each reply is read into.</summary>
    static void SampleOverrides(string temp)
    {
        Console.WriteLine("UI Studio: realistic values over the sample replies (the AI's override file, merged exactly)");

        // ── one value over another ──
        string got = MergeOf("{\"a\":1,\"b\":\"x\",\"c\":true}", "{\"b\":\"y\",\"d\":\"new\"}", out var kept);
        Check("overrides: a map key by key — the sample's order, the keys it leaves out keep their value, a key the sample hasn't is added",
              got == "{\"a\":1,\"b\":\"y\",\"c\":true,\"d\":\"new\"}" && kept == "", got + " | " + kept);
        const string two = "[{\"n\":\"a\",\"k\":1},{\"n\":\"b\",\"k\":2}]";
        got = MergeOf(two, "[{\"n\":\"x\"},{\"n\":\"y\",\"k\":5},{\"n\":\"z\"}]", out kept);
        Check("overrides: a longer list — each item over the sample item at its place, past the end over the last one",
              got == "[{\"n\":\"x\",\"k\":1},{\"n\":\"y\",\"k\":5},{\"n\":\"z\",\"k\":2}]" && kept == "", got);
        got = MergeOf(two, "[{\"n\":\"only\"}]", out _);
        Check("overrides: a shorter list has that many items", got == "[{\"n\":\"only\",\"k\":1}]", got);
        got = MergeOf(two, "[]", out kept);
        Check("overrides: [] makes the list empty (an empty state can be designed)", got == "[]" && kept == "", got);
        got = MergeOf("{\"errors\":[]}", "{\"errors\":[\"ระบบล่ม\"]}", out kept);
        Check("overrides: a list that is empty in the samples stays empty (errors, failures)", got == "{\"errors\":[]}" && kept == "/errors", got + " | " + kept);

        const string kinds = "{\"count\":12,\"amount\":1250.5,\"live\":true,\"name\":\"ตัวอย่าง\",\"at\":\"2026-10-18T09:30:00Z\",\"qty\":\"12\",\"price\":\"1250.50\",\"phone\":\"0812345678\",\"pic\":null}";
        got = MergeOf(kinds, "{\"count\":\"สิบสอง\",\"amount\":1500,\"live\":\"yes\",\"name\":\"ข้าวมันไก่\",\"at\":\"18 ต.ค.\",\"qty\":\"12.5\",\"price\":\"99\",\"phone\":\"081-234-5678\",\"pic\":\"https://cdn.example.test/a.png\"}", out kept);
        Check("overrides: a plain value only of the kind the code reads — text for a number, a word for a flag, a Thai date, a fraction as an int's text and a picture address keep the sample's; a whole amount stays a fraction (1500.0); a phone number is text",
              got == "{\"count\":12,\"amount\":1500.0,\"live\":true,\"name\":\"ข้าวมันไก่\",\"at\":\"2026-10-18T09:30:00Z\",\"qty\":\"12\",\"price\":\"99\",\"phone\":\"081-234-5678\",\"pic\":null}"
              && kept == "/count /live /at /qty /pic", got + " | " + kept);
        got = MergeOf(kinds, "{\"count\":48,\"amount\":1250.75,\"live\":false,\"at\":\"2026-10-01 08:00:00+07:00\",\"qty\":\"7\",\"price\":\"1500.25\",\"name\":null}", out kept);
        Check("overrides: the same kinds are taken — a whole count, a fraction, false, a date Dart's DateTime.parse reads, numbers as text; null over text keeps the text",
              got == "{\"count\":48,\"amount\":1250.75,\"live\":false,\"name\":\"ตัวอย่าง\",\"at\":\"2026-10-01 08:00:00+07:00\",\"qty\":\"7\",\"price\":\"1500.25\",\"phone\":\"0812345678\",\"pic\":null}"
              && kept == "/name", got + " | " + kept);
        got = MergeOf("{\"n\":12,\"d\":\"2026-10-18T09:30:00Z\"}", "{\"n\":12.5,\"d\":\"2026-10-03\"}", out kept);
        Check("overrides: a fraction where the code reads a whole number keeps the sample's; a date without a time is a date", got == "{\"n\":12,\"d\":\"2026-10-03\"}" && kept == "/n", got + " | " + kept);
        got = MergeOf("{\"items\":[{\"a\":1}],\"top\":{\"x\":1}}", "{\"items\":{\"a\":2},\"top\":\"x\"}", out kept);
        Check("overrides: a map for a list, text for a map — the structure the code reads stays", got == "{\"items\":[{\"a\":1}],\"top\":{\"x\":1}}" && kept == "/items /top", got + " | " + kept);

        // ── over the replies read from an app's code ──
        string root = Path.Combine(temp, "sample-app");
        var made = FlutterSampleData.Build(root, SampleNow);
        string madeStats = Json(made.Replies.First(r => r.Path == "/shop/stats").Body);
        const string overridesText = """
            {
              // the AI's file: realistic Thai values for some of the requests (comments and a trailing comma are forgiven)
              "GET /shop/stats": {"data": {"top_metrics": {"orders_count": 48, "revenue_thb": 15890},
                                  "items": [{"name": "ข้าวมันไก่ตอน", "count": 31}, {"name": "ชาไทยเย็น"}, {"name": "ขนมครก", "status": "pending"}, {"name": "ผัดไทยกุ้งสด", "count": "สิบ"}]}},
              "GET /shops/*/items": [{"name": "ส้มตำไทย"}],
              "GET /v2/me": {"user_name": "ณัฐวุฒิ แสงทอง", "age": 34},
              "GET /auth/logout": {"ok": true},
              "POST /nowhere": {},
            }
            """;
        var overrides = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(overridesText,
            documentOptions: new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!;
        var merged = FlutterSampleData.Merge(made, overrides);
        SampleReply? M(string path) => merged.Replies.FirstOrDefault(r => r.Path == path);
        const string trend = "[{\"date\":\"2026-10-18T09:30:00Z\",\"value\":1250.5},{\"date\":\"2026-10-17T08:53:00Z\",\"value\":1560.5},{\"date\":\"2026-10-16T08:16:00Z\",\"value\":1870.5}]";
        string Item(string name, int count, string status = "paid") => $"{{\"name\":\"{name}\",\"count\":{count},\"status\":\"{status}\",\"trend\":{trend}}}";
        got = Json(M("/shop/stats")?.Body);
        Check("overrides: a reply in the app's envelope, given with it — the counts and names taken, a whole amount as a fraction, four items over three (each takes the keys it leaves out), a word for a count kept, the rest as it was",
              got == "{\"success\":true,\"data\":{\"top_metrics\":{\"orders_count\":48,\"revenue_thb\":15890.0,\"revenue\":1250.5},\"revenue_today\":{\"split\":{\"shop\":1250.5}},\"items\":["
                     + Item("ข้าวมันไก่ตอน", 31) + "," + Item("ชาไทยเย็น", 17) + "," + Item("ขนมครก", 22, "pending") + "," + Item("ผัดไทยกุ้งสด", 22)
                     + "],\"updated_at\":\"2026-10-18T09:30:00Z\",\"mode\":\"smart\",\"is_live\":true}}", got);
        got = Json(M("/shops/*/items")?.Body);
        Check("overrides: a list given without the envelope is its data", got == "{\"success\":true,\"data\":[" + Item("ส้มตำไทย", 12) + "]}", got);
        got = Json(M("/v2/me")?.Body);
        Check("overrides: a raw reply (res.data['data']) given as its data — a map without the data key",
              got == "{\"data\":{\"user_name\":\"ณัฐวุฒิ แสงทอง\",\"email\":\"user1@example.com\",\"age\":34,\"avatar_url\":null,\"errors\":[]}}", got);
        Check("overrides: the replies it doesn't name are the made-up ones, untouched",
              ReferenceEquals(M("/items"), made.Replies.First(r => r.Path == "/items")) && ReferenceEquals(M("/summary"), made.Replies.First(r => r.Path == "/summary")));
        Check("overrides: which replies changed, which requests are left out (not answered by the code / not made), which values kept the sample's",
              merged.Overridden == 3 && string.Join(", ", merged.Replies.Where(r => r.Overridden).Select(r => r.Key)) == "GET /shop/stats, GET /shops/*/items, GET /v2/me"
              && string.Join(", ", merged.Ignored) == "GET /auth/logout, POST /nowhere" && string.Join(", ", merged.Kept) == "GET /shop/stats /data/items/3/count",
              $"{merged.Overridden} · {string.Join(", ", merged.Ignored)} · {string.Join(", ", merged.Kept)}");
        Check("overrides: the made-up replies are not changed by a merge (they are cached and shared)", Json(made.Replies.First(r => r.Path == "/shop/stats").Body) == madeStats);
        string file = merged.ToJson();
        Check("overrides: the file the generated test reads has the merged replies (the fraction written as one), the same patterns",
              file.Contains("\"revenue_thb\":15890.0", StringComparison.Ordinal) && file.Contains("ข้าวมันไก่ตอน", StringComparison.Ordinal)
              && file.Contains("\"pattern\":\"(^|/)shop/stats/?$\"", StringComparison.Ordinal) && merged.Unread.Count == 1, file.Length > 300 ? file[..300] : file);
        string replies = made.RepliesJson();
        try
        {
            var keyed = System.Text.Json.Nodes.JsonNode.Parse(replies)!.AsObject();
            Check("overrides: the replies as the override file is written (what the AI gets) — one request a line, in order, each its reply",
                  replies.StartsWith("{\n\"GET /shop/stats\": {\"success\":true,", StringComparison.Ordinal) && replies.Split('\n').Length == 7
                  && string.Join(", ", keyed.Select(kv => kv.Key)) == "GET /shop/stats, GET /shops/*/items, GET /items, GET /v2/me, GET /summary"
                  && Json(keyed["GET /shop/stats"]) == madeStats, replies.Length > 200 ? replies[..200] : replies);
            Check("overrides: … and the made-up replies merged with themselves change nothing", FlutterSampleData.Merge(made, keyed).Overridden == 0);
        }
        catch (Exception ex) { Check("overrides: the replies as the override file is written are JSON", false, ex.Message); }

        // ── the models' files each reply is read into ──
        string shop = Path.Combine(temp, "sample-models");
        W(shop, "pubspec.yaml", "name: models\n");
        W(shop, "lib/models/customer.dart", "class Customer {\n  const Customer(this.name);\n  final String name;\n  factory Customer.fromJson(Map<String, dynamic> j) => Customer(j['customer_name'] as String);\n}\n");
        W(shop, "lib/models/order.dart", "import 'customer.dart';\n\nclass Order {\n  const Order(this.total, this.customer);\n  final double total;\n  final Customer customer;\n" +
                                         "  factory Order.fromJson(Map<String, dynamic> j) => Order((j['total'] as num).toDouble(), Customer.fromJson(j['customer'] as Map<String, dynamic>));\n}\n");
        W(shop, "lib/data/orders_repo.dart", "import '../models/order.dart';\n\nclass OrdersRepo {\n  OrdersRepo(this.client);\n  final dynamic client;\n\n" +
                                             "  Future<List<Order>> orders() async {\n    final data = await client.get('/orders');\n    return (data as List).map((e) => Order.fromJson(e as Map<String, dynamic>)).toList();\n  }\n}\n");
        var orders = FlutterSampleData.Build(shop, SampleNow).Replies.FirstOrDefault();
        string files = orders == null ? "" : string.Join(", ", orders.ModelFiles.Select(f => Path.GetRelativePath(shop, f).Replace('\\', '/')));
        Check("overrides: each reply names its models' files — the reply's model first, then the models inside it (what the AI reads)",
              orders != null && orders.Key == "GET /orders" && orders.Model == "[Order]" && files == "lib/models/order.dart, lib/models/customer.dart", $"{orders?.Key} {orders?.Model} {files}");
        Check("overrides: … the fixture's models are all in one file", string.Join(", ", made.Replies.Select(r => string.Join("+", r.ModelFiles.Select(Path.GetFileName))))
              == "models.dart, models.dart, models.dart, models.dart, models.dart", string.Join(", ", made.Replies.Select(r => string.Join("+", r.ModelFiles.Select(Path.GetFileName)))));

        // ── the file in the Studio's store: read, cached, refused with its reason ──
        string store = Path.Combine(temp, "studio-store");
        Directory.CreateDirectory(store);
        string path = Path.Combine(store, "sample_overrides.json");
        var plain = FlutterSampleData.For(root);
        Check("overrides: no file — the made-up replies themselves", ReferenceEquals(FlutterSampleData.For(root, path), plain) && ReferenceEquals(FlutterSampleData.For(root, (string?)null), plain)
              && FlutterSampleData.OverridesStamp(path) == "");
        File.WriteAllText(path, overridesText);
        var read = FlutterSampleData.For(root, path);
        Check("overrides: the file is read and merged over the made-up replies", read.Overridden == 3 && read.OverridesError == null && Json(read.Replies.First(r => r.Path == "/v2/me").Body).Contains("ณัฐวุฒิ แสงทอง"),
              $"{read.Overridden} {read.OverridesError}");
        Check("overrides: read again only when the file changes", ReferenceEquals(FlutterSampleData.For(root, path), read));
        File.WriteAllText(path, "{\"GET /items\": [{\"name\": \"ไข่เจียว\"}]}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        var again = FlutterSampleData.For(root, path);
        Check("overrides: a changed file is read again", !ReferenceEquals(again, read) && again.Overridden == 1 && Json(again.Replies.First(r => r.Path == "/items").Body).Contains("ไข่เจียว"));
        void Refused(string text, string why, string name)
        {
            File.WriteAllText(path, text);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(10 + Refusals++));
            var r = FlutterSampleData.For(root, path);
            Check(name, r.OverridesError == why && r.Overridden == 0 && r.Replies.Count == plain.Replies.Count && r.Replies.Select(x => x.Body).SequenceEqual(plain.Replies.Select(x => x.Body)),
                  $"{r.OverridesError} {r.Overridden}");
        }
        Refused("{\"GET /items\": [", "studio.samples.badJson", "overrides: a file that isn't JSON is refused — the made-up replies, and why");
        Refused("[{\"name\": \"x\"}]", "studio.samples.notObject", "overrides: JSON that isn't a map of requests is refused");
        Refused("{\"GET /items\": \"" + new string('x', FlutterSampleData.MaxOverridesBytes) + "\"}", "studio.samples.tooBig", "overrides: a file over 4 MB is not read");
        File.Delete(path);
        Check("overrides: the file deleted — the made-up replies again", ReferenceEquals(FlutterSampleData.For(root, path), plain));
    }

    static int Refusals;
    /// <summary>studiotest samples &lt;flutter folder&gt; [path filter] — READ-ONLY on the project.</summary>
    static int SampleReport(string root, string? filter)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var data = FlutterSampleData.Build(root, new DateTime(2026, 10, 18, 9, 30, 0, DateTimeKind.Local));
        string Rel(string f) => Path.GetRelativePath(root, f).Replace('\\', '/');
        foreach (var r in data.Replies)
        {
            if (filter != null && !r.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            string body = r.Body?.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";
            Console.WriteLine($"  {r.Method} {r.Path} → {r.Model} · {Rel(r.File)}:{r.Line}" + (r.ModelFiles.Count > 0 ? " · models: " + string.Join(", ", r.ModelFiles.Select(Rel)) : ""));
            Console.WriteLine("      " + (filter != null || body.Length <= 300 ? body : body[..300] + "…"));
        }
        foreach (var u in data.Unread) Console.WriteLine($"  (no sample) {u.Method} {u.Path} · {Rel(u.File)}:{u.Line}");
        int json = data.RepliesJson().Length;
        Console.WriteLine($"\n{data.Replies.Count} requests answered · {data.Unread.Count} without a sample · envelope: {(data.DataKey != null ? $"{data.SuccessKey} + {data.DataKey}" : "none")} · read in {sw.Elapsed.TotalSeconds:0.0}s");
        Console.WriteLine($"for the AI: {json:N0} chars of sample replies — " + (json > CluadeX.ViewModels.UiStudioViewModel.MaxPromptSamples ? "too many for the prompt: written beside the renderer's test for it to read" : "in the prompt"));
        Console.WriteLine($"models' files: {data.Replies.SelectMany(r => r.ModelFiles).Distinct(StringComparer.OrdinalIgnoreCase).Count()} · replies without one: {data.Replies.Count(r => r.ModelFiles.Count == 0)}");
        return 0;
    }
}
