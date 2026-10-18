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
            Console.WriteLine($"  {r.Method} {r.Path} → {r.Model} · {Rel(r.File)}:{r.Line}");
            Console.WriteLine("      " + (filter != null || body.Length <= 300 ? body : body[..300] + "…"));
        }
        foreach (var u in data.Unread) Console.WriteLine($"  (no sample) {u.Method} {u.Path} · {Rel(u.File)}:{u.Line}");
        Console.WriteLine($"\n{data.Replies.Count} requests answered · {data.Unread.Count} without a sample · envelope: {(data.DataKey != null ? $"{data.SuccessKey} + {data.DataKey}" : "none")} · read in {sw.Elapsed.TotalSeconds:0.0}s");
        return 0;
    }
}
