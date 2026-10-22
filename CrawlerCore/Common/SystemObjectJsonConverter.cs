using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrawlerCore.Common
{
    /// <summary>
    /// 按运行时类型序列化声明为 object 的值（例如插件解析结果中的列表）。
    /// 读取时返回 <see cref="JsonElement"/>；不能再用同一个 options 反序列化 object，否则会递归进入自己（W-32）
    /// </summary>
    public class SystemObjectJsonConverter : JsonConverter<object>
    {
        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.Clone();
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
                return;
            }

            // 获取运行时类型，并使用对应类型的序列化器进行写入；运行时类型就是 object 时没有可写的成员，写空对象，避免递归
            Type runtimeType = value.GetType();
            if (runtimeType == typeof(object))
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
                return;
            }
            JsonSerializer.Serialize(writer, value, runtimeType, options);
        }
    }
}
