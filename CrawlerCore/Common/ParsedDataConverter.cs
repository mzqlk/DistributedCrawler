using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace CrawlerCore.Common
{
    /// <summary>
    /// 插件解析结果（<c>Dictionary&lt;string, object&gt;</c>）到协议 <c>map&lt;string, string&gt;</c> 的转换规则。
    /// 这是插件与 DataAnalysis 之间 parsedData 契约的一部分（见 CLAUDE.md），Worker 上报结果与契约测试共用这一实现：
    /// <list type="bullet">
    /// <item>null → 空串；字符串原样；</item>
    /// <item>其他标量（数字、布尔、枚举、Guid 等）用不变区域性转成字符串，不带引号；DateTime/DateTimeOffset 用往返格式 “O”；</item>
    /// <item>集合与复杂类型序列化为 JSON（例如 AuthorsList、SubjectsList 是 JSON 字符串数组，DataAnalysis 用 json.loads 读取）。</item>
    /// </list>
    /// </summary>
    public static class ParsedDataConverter
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            Converters = { new SystemObjectJsonConverter() }
        };

        /// <summary>转换单个值</summary>
        public static string ToProtoValue(object? value)
        {
            switch (value)
            {
                case null:
                    return string.Empty;
                case string text:
                    return text;
                case DateTime dateTime:
                    return dateTime.ToString("O", CultureInfo.InvariantCulture);
                case DateTimeOffset dateTimeOffset:
                    return dateTimeOffset.ToString("O", CultureInfo.InvariantCulture);
                case Enum enumValue:
                    return enumValue.ToString();
                case bool boolean:
                    return boolean ? "True" : "False";
                case JsonElement element:
                    return element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText();
            }

            var type = value.GetType();
            if (type.IsPrimitive || value is decimal || value is Guid || value is TimeSpan)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }

            return JsonSerializer.Serialize(value, type, JsonOptions);
        }

        /// <summary>转换整份解析结果；null 返回空字典</summary>
        public static Dictionary<string, string> ToProtoMap(IReadOnlyDictionary<string, object>? parsedData)
            => parsedData == null
                ? new Dictionary<string, string>()
                : parsedData.ToDictionary(kv => kv.Key, kv => ToProtoValue(kv.Value));
    }
}
