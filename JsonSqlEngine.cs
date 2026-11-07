#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSO.Core.JsonSqlEngine
{
    /// <summary>
    /// dui.grid'in gönderdiği sorgu: { CurrentPage, PageItemCount, OrderBy, Parameters }.
    /// TableName, Colums ve FixedParameters JSON'dan okunmaz; controller belirler.
    /// </summary>
    public class JsonSqlEngineQueryDto
    {
        /// <summary>Sunucu belirler. JSON'dan gelmez.</summary>
        [JsonIgnore]
        public string TableName { get; set; } = string.Empty;

        /// <summary>Sunucu belirler. JSON'dan gelmez. Boşsa *.</summary>
        [JsonIgnore]
        public List<string> Colums { get; set; } = new() { "*" };

        /// <summary>
        /// Sunucunun eklediği koşullar (ör. bayi/şirket kısıtı). JSON'dan gelmez; istemcinin koşullarıyla VE ile birleşir,
        /// istemci bunları değiştiremez. Biçim Parameters ile aynıdır.
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, object?> FixedParameters { get; set; } = new();

        /// <summary>
        /// Koşullar. Alan → düz değer (=) | düz değer dizisi (IN) | koşul | koşul dizisi (hepsi VE).
        /// "and" / "or" anahtarları grup açar; grup değeri nesne ya da nesne dizisi olabilir.
        /// </summary>
        public Dictionary<string, object?> Parameters { get; set; } = new();

        /// <summary>1'den başlar.</summary>
        public int? CurrentPage { get; set; }

        /// <summary>Sayfadaki kayıt sayısı. 0 ya da boş: sayfalama yok (hepsi).</summary>
        public int? PageItemCount { get; set; }

        /// <summary>Alan → 1 artan, 2 azalan. Ekleme sırası öncelik sırasıdır.</summary>
        public Dictionary<string, int> OrderBy { get; set; } = new();

        /// <summary>Genel toplamlar: alan → sum | avg | count | min | max. Doluysa TotalsQuery üretilir.</summary>
        public Dictionary<string, string>? Aggregates { get; set; }

        /// <summary>Alanın farklı değerleri (başlık filtresi menüsü). Doluysa DistinctQuery ve DistinctCountQuery üretilir.</summary>
        public string? Distinct { get; set; }
    }

    /// <summary>Tek koşul: { operator, value, func, defaultValue }</summary>
    public class FilterCondition
    {
        /// <summary>= != &gt; &lt; &gt;= &lt;= contains notcontains startswith endswith between in notin isnull isnotnull (equals, notequals, &lt;&gt; da olur)</summary>
        public string? Operator { get; set; }
        public object? Value { get; set; }

        /// <summary>isnull coalesce lower upper trim ltrim rtrim len year month day</summary>
        public string? Func { get; set; }

        /// <summary>isnull / coalesce için alanın boş olduğunda kullanılacak değer</summary>
        public object? DefaultValue { get; set; }
    }

    public class JsonSqlEngineResult
    {
        public string CountQuery { get; set; } = string.Empty;
        public string DataQuery { get; set; } = string.Empty;

        /// <summary>
        /// Aggregates verildiyse tek satırlık toplam sorgusu (aynı WHERE): SELECT SUM([Bakiye]) AS [Bakiye], ... FROM tablo WHERE ...;
        /// Sütun adları Aggregates'in anahtarlarıdır; sonuç DataResult.Totals'a aynı adlarla yazılır. Yoksa boş.
        /// </summary>
        public string TotalsQuery { get; set; } = string.Empty;

        /// <summary>
        /// Distinct verildiyse: SELECT [Alan] AS [Value], COUNT(1) AS [Count] FROM tablo WHERE ... GROUP BY [Alan] ORDER BY [Alan]
        /// (+ sayfalama). Sonuç DataResult.Data'ya [{ Value, Count }] olarak gider. Yoksa boş.
        /// </summary>
        public string DistinctQuery { get; set; } = string.Empty;

        /// <summary>Distinct verildiyse farklı değer sayısı (TotalDataCount için). Yoksa boş.</summary>
        public string DistinctCountQuery { get; set; } = string.Empty;

        public Dictionary<string, object?> Parameters { get; set; } = new();
    }

    public class JsonSqlEngineOptions
    {
        /// <summary>
        /// İzin verilen alanlar (büyük/küçük harf duyarsız). Boş bırakılırsa harf, rakam ve alt çizgiden oluşan her ad kabul edilir.
        /// Listede olmayan bir alanla gelen koşul ya da sıralama ArgumentException verir.
        /// </summary>
        public IEnumerable<string>? AllowedFields { get; set; }

        /// <summary>Sıralama gelmediğinde sayfalama için kullanılır (ör. "Id" ya da "Tarih DESC"). Boşsa ORDER BY (SELECT NULL).</summary>
        public string? DefaultOrderBy { get; set; }

        /// <summary>Alan adlarını [Ad] biçiminde yazar (ayrılmış sözcükler ve Türkçe harfler için güvenli).</summary>
        public bool QuoteFields { get; set; } = true;

        /// <summary>2024-03-05 ve 2024-03-05T14:30:00 biçimindeki metinleri DateTime parametresine çevirir (DATEFORMAT / dil ayarından etkilenmez).</summary>
        public bool ParseDates { get; set; } = true;

        /// <summary>Grup iç içe geçme sınırı.</summary>
        public int MaxDepth { get; set; } = 8;

        /// <summary>SQL Server'ın 2100 parametre sınırının altında kalmak için.</summary>
        public int MaxParameters { get; set; } = 2000;

        /// <summary>0: sınırsız. Verilirse sayfa boyutu (ve "hepsi" isteği) bu sayıyla sınırlanır.</summary>
        public int MaxPageItemCount { get; set; }
    }

    /// <summary>
    /// JSON sorgusundan parametreli SELECT üretir. Durum tutmaz; aynı instance birden çok istekte kullanılabilir.
    /// </summary>
    public class JsonSqlEngine
    {
        /// <summary>Sorguyu okurken kullanılacak ayar: alan adları büyük/küçük harf duyarsız.</summary>
        public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private static readonly HashSet<string> Funcs = new(StringComparer.OrdinalIgnoreCase)
        {
            "ISNULL", "COALESCE", "LOWER", "UPPER", "TRIM", "LTRIM", "RTRIM", "LEN", "YEAR", "MONTH", "DAY"
        };

        private static readonly Dictionary<string, string> CompareOps = new(StringComparer.Ordinal)
        {
            ["="] = "=",
            ["!="] = "<>",
            [">"] = ">",
            ["<"] = "<",
            [">="] = ">=",
            ["<="] = "<="
        };

        private static readonly string[] DateFormats =
        {
            "yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF"
        };

        private readonly JsonSqlEngineOptions _options;
        private readonly HashSet<string>? _allowed;

        public JsonSqlEngine() : this(new JsonSqlEngineOptions()) { }

        public JsonSqlEngine(JsonSqlEngineOptions options)
        {
            _options = options ?? new JsonSqlEngineOptions();
            _allowed = _options.AllowedFields == null ? null : new HashSet<string>(_options.AllowedFields, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Grid'in gönderdiği JSON'u okur (TableName ve Colums JSON'dan alınmaz).</summary>
        public static JsonSqlEngineQueryDto Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JsonSqlEngineQueryDto();
            return JsonSerializer.Deserialize<JsonSqlEngineQueryDto>(json, JsonOptions) ?? new JsonSqlEngineQueryDto();
        }

        private sealed class Context
        {
            public int Counter;
            public Dictionary<string, object?> Parameters = new();
        }

        public JsonSqlEngineResult Build(JsonSqlEngineQueryDto dto)
        {
            var result = new JsonSqlEngineResult();
            if (dto == null || string.IsNullOrWhiteSpace(dto.TableName)) return result;

            var ctx = new Context { Parameters = result.Parameters };

            string client = BuildNode(ToElement(dto.Parameters), ctx, "AND", 0);
            string server = BuildNode(ToElement(dto.FixedParameters), ctx, "AND", 0);
            string where;
            if (client.Length > 0 && server.Length > 0) where = $" WHERE ({client}) AND ({server})";
            else if (client.Length > 0 || server.Length > 0) where = " WHERE " + client + server;
            else where = string.Empty;

            result.CountQuery = $"SELECT COUNT(1) FROM {dto.TableName}{where};";

            var columns = dto.Colums != null && dto.Colums.Count > 0 ? string.Join(", ", dto.Colums) : "*";
            var data = new StringBuilder();
            data.Append($"SELECT {columns} FROM {dto.TableName}{where}");

            int page = dto.CurrentPage ?? 0;
            int size = dto.PageItemCount ?? 0;
            if (_options.MaxPageItemCount > 0 && (size <= 0 || size > _options.MaxPageItemCount))
            {
                size = _options.MaxPageItemCount;
                if (page <= 0) page = 1;
            }
            bool hasPaging = page > 0 && size > 0;

            string orderBy = BuildOrderBy(dto.OrderBy);
            if (orderBy.Length == 0 && hasPaging)
                orderBy = string.IsNullOrWhiteSpace(_options.DefaultOrderBy) ? "(SELECT NULL)" : _options.DefaultOrderBy!;
            if (orderBy.Length > 0) data.Append(" ORDER BY ").Append(orderBy);

            if (hasPaging)
            {
                result.Parameters["@p_sys_offset"] = (long)(page - 1) * size;
                result.Parameters["@p_sys_fetch"] = size;
                data.Append(" OFFSET @p_sys_offset ROWS FETCH NEXT @p_sys_fetch ROWS ONLY");
            }

            data.Append(';');
            result.DataQuery = data.ToString();

            // genel toplamlar: aynı koşullarla tek satır
            if (dto.Aggregates != null && dto.Aggregates.Count > 0)
            {
                var parts = new List<string>();
                foreach (var a in dto.Aggregates)
                {
                    var f = Field(a.Key);
                    parts.Add(AggregateSql(a.Value, f.Sql, f.Name) + " AS " + Alias(f.Name));
                }
                result.TotalsQuery = $"SELECT {string.Join(", ", parts)} FROM {dto.TableName}{where};";
            }

            // farklı değerler: değere göre sıralı, sayılarıyla; sayfalama veri sorgusuyla aynı
            if (!string.IsNullOrWhiteSpace(dto.Distinct))
            {
                var f = Field(dto.Distinct!);
                var dq = new StringBuilder();
                dq.Append($"SELECT {f.Sql} AS {Alias("Value")}, COUNT(1) AS {Alias("Count")} FROM {dto.TableName}{where} GROUP BY {f.Sql} ORDER BY {f.Sql}");
                if (hasPaging) dq.Append(" OFFSET @p_sys_offset ROWS FETCH NEXT @p_sys_fetch ROWS ONLY");
                dq.Append(';');
                result.DistinctQuery = dq.ToString();
                result.DistinctCountQuery = $"SELECT COUNT(1) FROM (SELECT {f.Sql} AS {Alias("Value")} FROM {dto.TableName}{where} GROUP BY {f.Sql}) AS d;";
            }
            return result;
        }

        /// <summary>Toplam türü → SQL. avg ve sum ondalıklı hesaplanır (tam sayı sütununda AVG yuvarlamasın, SUM taşmasın).</summary>
        private static string AggregateSql(string? type, string fieldSql, string name)
        {
            switch ((type ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "sum": return $"SUM(CAST({fieldSql} AS DECIMAL(38, 6)))";
                case "avg": return $"AVG(CAST({fieldSql} AS DECIMAL(38, 6)))";
                case "count": return $"COUNT({fieldSql})";
                case "min": return $"MIN({fieldSql})";
                case "max": return $"MAX({fieldSql})";
                default: throw new NotSupportedException($"{name}: '{type}' toplamı desteklenmiyor (sum, avg, count, min, max).");
            }
        }

        private string Alias(string name) => _options.QuoteFields ? "[" + name + "]" : name;

        private string BuildOrderBy(Dictionary<string, int>? orderBy)
        {
            if (orderBy == null || orderBy.Count == 0) return string.Empty;
            var list = new List<string>();
            foreach (var item in orderBy)
            {
                if (item.Value != 1 && item.Value != 2) continue;
                list.Add(Field(item.Key).Sql + (item.Value == 2 ? " DESC" : " ASC"));
            }
            return string.Join(", ", list);
        }

        // ---- koşul ağacı
        private string BuildNode(JsonElement node, Context ctx, string logic, int depth)
        {
            if (depth > _options.MaxDepth) throw new ArgumentException("Koşul grupları çok derin.");
            var parts = new List<string>();

            if (node.ValueKind == JsonValueKind.Array)
            {
                // [{...}, {...}]: her eleman kendi içinde VE, elemanlar grubun operatörüyle
                foreach (var item in node.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("Grup dizisinin elemanları nesne olmalı.");
                    string s = BuildNode(item, ctx, "AND", depth + 1);
                    if (s.Length > 0) parts.Add("(" + s + ")");
                }
            }
            else if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in node.EnumerateObject())
                {
                    string key = prop.Name.Trim();
                    string lower = key.ToLowerInvariant();
                    if (lower == "and" || lower == "or")
                    {
                        string g = BuildNode(prop.Value, ctx, lower.ToUpperInvariant(), depth + 1);
                        if (g.Length > 0) parts.Add("(" + g + ")");
                        continue;
                    }
                    // "StokAdi|StokKod": aynı koşul alanlardan herhangi birinde (VEYA)
                    string f = key.IndexOf('|') >= 0 ? BuildMultiField(key, prop.Value, ctx) : BuildField(key, prop.Value, ctx);
                    if (f.Length > 0) parts.Add(f);
                }
            }
            else if (node.ValueKind != JsonValueKind.Null && node.ValueKind != JsonValueKind.Undefined)
            {
                throw new ArgumentException("Koşul grubu nesne ya da dizi olmalı.");
            }

            return string.Join(" " + logic + " ", parts);
        }

        private string BuildMultiField(string key, JsonElement value, Context ctx)
        {
            var parts = new List<string>();
            foreach (var name in key.Split('|'))
            {
                if (name.Trim().Length == 0) continue;
                string s = BuildField(name.Trim(), value, ctx);
                if (s.Length > 0) parts.Add(s);
            }
            if (parts.Count == 0) return string.Empty;
            return parts.Count == 1 ? parts[0] : "(" + string.Join(" OR ", parts) + ")";
        }

        private string BuildField(string key, JsonElement value, Context ctx)
        {
            var field = Field(key);
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    return BuildCondition(field, ReadCondition(value), ctx);

                case JsonValueKind.Array:
                    bool conditions = value.GetArrayLength() > 0 && value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Object);
                    if (!conditions) return BuildIn(field.Sql, "IN", value, ctx);   // düz değer dizisi: IN
                    var list = new List<string>();
                    foreach (var c in value.EnumerateArray())
                    {
                        string s = BuildCondition(field, ReadCondition(c), ctx);
                        if (s.Length > 0) list.Add(s);
                    }
                    if (list.Count == 0) return string.Empty;
                    return list.Count == 1 ? list[0] : "(" + string.Join(" AND ", list) + ")";

                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return field.Sql + " IS NULL";

                default:
                    return field.Sql + " = " + AddParam(ctx, ToValue(value));
            }
        }

        private static FilterCondition ReadCondition(JsonElement e)
        {
            return JsonSerializer.Deserialize<FilterCondition>(e.GetRawText(), JsonOptions) ?? new FilterCondition();
        }

        private string BuildCondition((string Sql, string Name) field, FilterCondition condition, Context ctx)
        {
            string op = NormalizeOperator(condition.Operator);
            string target = field.Sql;

            string func = (condition.Func ?? string.Empty).Trim().ToUpperInvariant();
            if (func.Length > 0)
            {
                if (!Funcs.Contains(func)) throw new NotSupportedException($"'{condition.Func}' fonksiyonu desteklenmiyor.");
                if (func == "ISNULL" || func == "COALESCE")
                {
                    object? def = condition.DefaultValue == null ? string.Empty : ToValue(AsElement(condition.DefaultValue));
                    target = $"{func}({field.Sql}, {AddParam(ctx, def)})";
                }
                else
                {
                    target = $"{func}({field.Sql})";
                }
            }

            if (op == "isnull") return target + " IS NULL";
            if (op == "isnotnull") return target + " IS NOT NULL";

            JsonElement value = AsElement(condition.Value);

            if (op == "between")
            {
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
                    throw new ArgumentException($"{field.Name}: between iki değerli dizi bekler.");
                object? lo = ToValue(value[0]), hi = ToValue(value[1]);
                bool noLo = IsBlank(lo), noHi = IsBlank(hi);
                if (noLo && noHi) return string.Empty;
                if (noLo) return $"{target} <= {AddParam(ctx, hi)}";
                if (noHi) return $"{target} >= {AddParam(ctx, lo)}";
                return $"{target} BETWEEN {AddParam(ctx, lo)} AND {AddParam(ctx, hi)}";
            }

            if (op == "in" || op == "notin")
                return BuildIn(target, op == "in" ? "IN" : "NOT IN", value, ctx);

            if (op == "contains" || op == "notcontains" || op == "startswith" || op == "endswith")
            {
                // metin olduğu gibi kullanılır (tarihe çevrilmez)
                object? raw = value.ValueKind == JsonValueKind.String ? value.GetString() : ToValue(value);
                if (IsBlank(raw)) return string.Empty;
                string text = EscapeLike(Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty);
                string pattern = op == "contains" || op == "notcontains" ? "%" + text + "%" : op == "startswith" ? text + "%" : "%" + text;
                // NOT LIKE: boş (NULL) alanlar SQL'de olduğu gibi gelmez; gelsin istenirse func: isnull
                return $"{target} {(op == "notcontains" ? "NOT LIKE" : "LIKE")} {AddParam(ctx, pattern)} ESCAPE '\\'";
            }

            // = != > < >= <=
            object? v = ToValue(value);
            if (v == null)
            {
                if (op == "=") return target + " IS NULL";
                if (op == "!=") return target + " IS NOT NULL";
                return string.Empty;
            }
            return $"{target} {CompareOps[op]} {AddParam(ctx, v)}";
        }

        private string BuildIn(string target, string sqlOp, JsonElement value, Context ctx)
        {
            var items = new List<object?>();
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in value.EnumerateArray()) items.Add(ToValue(e));
            }
            else if (value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined)
            {
                items.Add(ToValue(value));
            }
            // listede null: IN boş alanları da getirir ([Alan] IN (...) OR [Alan] IS NULL),
            // NOT IN boş alanları getirmez (SQL'de NULL NOT IN (...) zaten sağlanmaz; burada açıkça yazılır)
            bool hasNull = items.Any(x => x == null);
            items = items.Where(x => x != null).ToList();

            if (items.Count == 0)
            {
                if (hasNull) return target + (sqlOp == "IN" ? " IS NULL" : " IS NOT NULL");
                // boş liste: IN hiçbir kaydı, NOT IN bütün kayıtları getirir
                return sqlOp == "IN" ? "1 = 0" : "1 = 1";
            }

            var names = items.Select(x => AddParam(ctx, x)).ToList();
            string list = $"{target} {sqlOp} ({string.Join(", ", names)})";
            if (!hasNull) return list;
            return sqlOp == "IN" ? $"({list} OR {target} IS NULL)" : $"({list} AND {target} IS NOT NULL)";
        }

        // ---- yardımcılar
        private static string NormalizeOperator(string? op)
        {
            string o = (op ?? "=").Trim().ToLowerInvariant();
            switch (o)
            {
                case "":
                case "=":
                case "equals":
                    return "=";
                case "!=":
                case "<>":
                case "notequals":
                    return "!=";
                case ">":
                case "<":
                case ">=":
                case "<=":
                case "contains":
                case "notcontains":
                case "startswith":
                case "endswith":
                case "between":
                case "in":
                case "notin":
                case "isnull":
                case "isnotnull":
                    return o;
                default:
                    throw new NotSupportedException($"'{op}' operatörü desteklenmiyor.");
            }
        }

        /// <summary>Alan adı: sadece harf, rakam ve alt çizgi; izinli alan listesi varsa onda olmalı.</summary>
        private (string Sql, string Name) Field(string key)
        {
            string safe = new string((key ?? string.Empty).Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            if (safe.Length == 0) throw new ArgumentException($"Geçersiz alan adı: '{key}'.");
            if (_allowed != null && !_allowed.Contains(safe)) throw new ArgumentException($"'{safe}' alanında sorgu yapılamaz.");
            return (_options.QuoteFields ? "[" + safe + "]" : safe, safe);
        }

        private string AddParam(Context ctx, object? value)
        {
            if (ctx.Parameters.Count >= _options.MaxParameters) throw new ArgumentException("Sorguda çok fazla değer var.");
            string name = "@p" + (++ctx.Counter).ToString(CultureInfo.InvariantCulture);
            ctx.Parameters[name] = value;
            return name;
        }

        private static string EscapeLike(string s)
        {
            return s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
        }

        private static bool IsBlank(object? v) => v == null || (v is string s && s.Trim().Length == 0);

        private static JsonElement ToElement(object? v)
        {
            if (v is JsonElement e) return e;
            return JsonSerializer.SerializeToElement<object?>(v);
        }

        private static JsonElement AsElement(object? v) => ToElement(v);

        private object? ToValue(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String:
                    string s = e.GetString() ?? string.Empty;
                    if (_options.ParseDates && s.Length >= 10 && s.Length <= 27 && char.IsDigit(s[0]) && s[4] == '-' &&
                        DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                        return d;
                    return s;
                case JsonValueKind.Number:
                    if (e.TryGetInt64(out long l)) return l;
                    if (e.TryGetDecimal(out decimal m)) return m;
                    return e.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                default:
                    throw new ArgumentException("Koşul değeri düz bir değer olmalı (metin, sayı, mantıksal ya da tarih).");
            }
        }
    }
}
