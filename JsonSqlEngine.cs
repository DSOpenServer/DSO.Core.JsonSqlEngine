using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace DSO.Core.JsonSqlEngine
{
    public class JsonSqlEngineQueryDto
    {
        public string TableName { get; set; } = string.Empty;
        public List<string> Colums { get; set; } = new() { "*" };
        public Dictionary<string, object> Parameters { get; set; } = new();
        public int? Page { get; set; }
        public int? PageSize { get; set; }
        //public string OrderBy { get; set; } = string.Empty;
        public Dictionary<string, int> OrderBy { get; set; } = new();
    }

    public class FilterCondition
    {
        public string Operator { get; set; }
        public object Value { get; set; }

        // Veritabanı fonksiyonel filtreleme özellikleri
        public string Func { get; set; }
        public object DefaultValue { get; set; }
    }

    public class JsonSqlEngineResult
    {
        public string CountQuery { get; set; } = string.Empty;
        public string DataQuery { get; set; } = string.Empty;
        public Dictionary<string, object> Parameters { get; set; } = new();
    }

    public class JsonSqlEngine
    {
        private int _paramCounter = 0;

        public JsonSqlEngineResult Build(JsonSqlEngineQueryDto dto)
        {
            var result = new JsonSqlEngineResult();
            if (dto == null || string.IsNullOrWhiteSpace(dto.TableName)) return result;

            _paramCounter = 0;
            var whereSb = new StringBuilder();

            if (dto.Parameters != null && dto.Parameters.Any())
            {
                BuildClause(dto.Parameters, whereSb, result.Parameters, "AND");
            }

            string whereClause = whereSb.Length > 0 ? $" WHERE {whereSb}" : string.Empty;

            result.CountQuery = $"SELECT COUNT(1) FROM {dto.TableName}{whereClause};";

            var columns = (dto.Colums != null && dto.Colums.Any()) ? string.Join(", ", dto.Colums) : "*";
            var dataSb = new StringBuilder();
            dataSb.Append($"SELECT {columns} FROM {dto.TableName}{whereClause}");

            bool hasPaging = dto.Page.HasValue && dto.PageSize.HasValue && dto.Page.Value > 0 && dto.PageSize.Value > 0;

            if (hasPaging || dto.OrderBy.Any())
            {
                StringBuilder sb_Orders = new StringBuilder();

                foreach (var item in dto.OrderBy)
                {
                    if (item.Value == 0) continue;

                    if (sb_Orders.Length > 0)
                    {
                        sb_Orders.Append(", ");
                    }

                    if (item.Value == 1)
                    {
                        sb_Orders.AppendFormat("{0} ASC", item.Key);
                    }
                    else if (item.Value == 2)
                    {
                        sb_Orders.AppendFormat("{0} DESC", item.Key);
                    }
                }

                string orderByClause = sb_Orders.Length > 0 ? sb_Orders.ToString() : "Id";
                dataSb.Append($" ORDER BY {orderByClause}");

                if (hasPaging)
                {
                    int offset = (dto.Page.Value - 1) * dto.PageSize.Value;
                    result.Parameters.Add("@p_sys_offset", offset);
                    result.Parameters.Add("@p_sys_fetch", dto.PageSize.Value);
                    dataSb.Append($" OFFSET @p_sys_offset ROWS FETCH NEXT @p_sys_fetch ROWS ONLY");
                }
            }

            dataSb.Append(";");
            result.DataQuery = dataSb.ToString();

            return result;
        }

        private void BuildClause(Dictionary<string, object> node, StringBuilder sb, Dictionary<string, object> parameters, string logicalOperator)
        {
            var conditions = new List<string>();

            foreach (var item in node)
            {
                string key = item.Key.Trim();
                string keyLower = key.ToLower();

                if (keyLower == "and" || keyLower == "or")
                {
                    if (item.Value is JsonElement groupElement && groupElement.ValueKind == JsonValueKind.Object)
                    {
                        var subNode = JsonSerializer.Deserialize<Dictionary<string, object>>(groupElement.GetRawText());
                        if (subNode != null && subNode.Any())
                        {
                            var subSb = new StringBuilder();
                            BuildClause(subNode, subSb, parameters, keyLower.ToUpper());
                            if (subSb.Length > 0) conditions.Add($"({subSb})");
                        }
                    }
                    continue;
                }

                // Kolon adı white-list güvenliği (Sadece harf, rakam ve alt çizgi)
                string safeField = string.Concat(key.Where(c => char.IsLetterOrDigit(c) || c == '_'));
                if (string.IsNullOrEmpty(safeField)) continue;

                if (item.Value is JsonElement jsonVal)
                {
                    if (jsonVal.ValueKind == JsonValueKind.Object)
                    {
                        var condition = JsonSerializer.Deserialize<FilterCondition>(jsonVal.GetRawText(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                        if (condition != null)
                        {
                            // Fonksiyonel veya standart operatörlü yapıyı işleme alıyoruz
                            string conditionSql = ProcessOperatorCondition(safeField, condition, parameters);
                            if (!string.IsNullOrEmpty(conditionSql)) conditions.Add(conditionSql);
                        }
                    }
                    else
                    {
                        _paramCounter++;
                        string paramName = $"@p_{safeField}_{_paramCounter}";
                        parameters.Add(paramName, GetValueFromElement(jsonVal));
                        conditions.Add($"{safeField} = {paramName}");
                    }
                }
            }

            if (conditions.Count > 0)
                sb.Append(string.Join($" {logicalOperator} ", conditions));
        }

        private string ProcessOperatorCondition(string field, FilterCondition condition, Dictionary<string, object> parameters)
        {
            string opLower = condition.Operator?.ToLower().Trim() ?? string.Empty;
            string funcUpper = condition.Func?.ToUpper().Trim() ?? string.Empty;

            // Öncelikli olarak kolon adını fonksiyon sarmalına alıp almayacağımızı belirliyoruz
            string targetExpression = field;
            if (!string.IsNullOrEmpty(funcUpper))
            {
                if (funcUpper == "ISNULL" || funcUpper == "COALESCE")
                {
                    string defValStr = "''"; // Varsayılan fallback

                    // dynamic veya object olarak binen JSON düğümünü doğru kontrol ediyoruz
                    if (condition.DefaultValue is JsonElement defaultJsonElem)
                    {
                        defValStr = defaultJsonElem.ValueKind switch
                        {
                            JsonValueKind.String => $"'{defaultJsonElem.GetString()}'",
                            JsonValueKind.Number => defaultJsonElem.GetRawText(),
                            JsonValueKind.True => "1",
                            JsonValueKind.False => "0",
                            JsonValueKind.Null => "NULL",
                            _ => $"'{defaultJsonElem.GetRawText()}'"
                        };
                    }

                    targetExpression = $"{funcUpper}({field}, {defValStr})";
                }
                else
                {
                    // LOWER, UPPER vb. tek parametreli standart fonksiyonlar için
                    targetExpression = $"{funcUpper}({field})";
                }
            }

            // NULL ve NOT NULL kontrolü
            if (opLower == "isnull" || opLower == "isnotnull")
            {
                return opLower == "isnull" ? $"{targetExpression} IS NULL" : $"{targetExpression} IS NOT NULL";
            }

            _paramCounter++;
            string paramName = $"@p_{field}_{_paramCounter}";

            if (condition.Value is JsonElement jsonElement)
            {
                // BETWEEN Yönetimi
                if (opLower == "between" && jsonElement.ValueKind == JsonValueKind.Array)
                {
                    var limits = JsonSerializer.Deserialize<List<JsonElement>>(jsonElement.GetRawText());
                    if (limits != null && limits.Count == 2)
                    {
                        string pMin = $"{paramName}_min";
                        string pMax = $"{paramName}_max";
                        parameters.Add(pMin, GetValueFromElement(limits[0]));
                        parameters.Add(pMax, GetValueFromElement(limits[1]));
                        return $"{targetExpression} BETWEEN {pMin} AND {pMax}";
                    }
                    return string.Empty;
                }

                // IN / NOT IN Yönetimi
                if ((opLower == "in" || opLower == "notin") && jsonElement.ValueKind == JsonValueKind.Array)
                {
                    var elements = JsonSerializer.Deserialize<List<JsonElement>>(jsonElement.GetRawText());
                    if (elements != null && elements.Any())
                    {
                        var inParamNames = new List<string>();
                        for (int i = 0; i < elements.Count; i++)
                        {
                            string pIn = $"{paramName}_in_{i}";
                            parameters.Add(pIn, GetValueFromElement(elements[i]));
                            inParamNames.Add(pIn);
                        }
                        string sqlOp = opLower == "notin" ? "NOT IN" : "IN";
                        return $"{targetExpression} {sqlOp} ({string.Join(", ", inParamNames)})";
                    }
                    return string.Empty;
                }

                // Standart Operatörler
                string sqlOperator = opLower switch
                {
                    "=" => "=",
                    "equals" => "=",
                    "!=" => "<>",
                    "notequals" => "<>",
                    ">" => ">",
                    "<" => "<",
                    ">=" => ">=",
                    "<=" => "<=",
                    "contains" => "LIKE",
                    "startswith" => "LIKE",
                    "endswith" => "LIKE",
                    _ => throw new NotSupportedException($"'{opLower}' operatörü desteklenmiyor.")
                };

                object finalValue = GetValueFromElement(jsonElement);

                if (sqlOperator == "LIKE")
                {
                    finalValue = opLower switch
                    {
                        "contains" => $"%{finalValue}%",
                        "startswith" => $"{finalValue}%",
                        "endswith" => $"%{finalValue}",
                        _ => finalValue
                    };
                }

                parameters.Add(paramName, finalValue);
                return $"{targetExpression} {sqlOperator} {paramName}";
            }

            return string.Empty;
        }

        private object GetValueFromElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out long l) ? l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => element.GetRawText()
            };
        }
    }
}
