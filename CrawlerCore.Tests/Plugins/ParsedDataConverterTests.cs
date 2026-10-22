using CrawlerCore.Common;
using System.Text.Json;

namespace CrawlerCore.Tests.Plugins;

/// <summary>parsedData 值到协议字符串的转换规则（W-25、W-32 ②）</summary>
public class ParsedDataConverterTests
{
    [Fact(DisplayName = "字符串原样、null 为空串")]
    public void StringsAndNull()
    {
        Assert.Equal("abc", ParsedDataConverter.ToProtoValue("abc"));
        Assert.Equal(string.Empty, ParsedDataConverter.ToProtoValue(null));
    }

    [Fact(DisplayName = "标量不带引号，按不变区域性格式化；时间用往返格式")]
    public void Scalars()
    {
        Assert.Equal("42", ParsedDataConverter.ToProtoValue(42));
        Assert.Equal("1.5", ParsedDataConverter.ToProtoValue(1.5));
        Assert.Equal("2.25", ParsedDataConverter.ToProtoValue(2.25m));
        Assert.Equal("True", ParsedDataConverter.ToProtoValue(true));
        Assert.Equal("Monday", ParsedDataConverter.ToProtoValue(DayOfWeek.Monday));
        var time = new DateTime(2026, 10, 22, 8, 30, 0, DateTimeKind.Utc);
        Assert.Equal("2026-10-22T08:30:00.0000000Z", ParsedDataConverter.ToProtoValue(time));
        var id = Guid.NewGuid();
        Assert.Equal(id.ToString(), ParsedDataConverter.ToProtoValue(id));
    }

    [Fact(DisplayName = "列表序列化为 JSON 数组，能被读回")]
    public void ListsAreJsonArrays()
    {
        var value = ParsedDataConverter.ToProtoValue(new List<string> { "A \"quoted\" name", "Zoë" });
        Assert.Equal(new[] { "A \"quoted\" name", "Zoë" }, JsonSerializer.Deserialize<List<string>>(value));
    }

    [Fact(DisplayName = "W-32 ①：运行时类型就是 object 的值不会无限递归，转换器读取不递归")]
    public void ObjectConverter_DoesNotRecurse()
    {
        Assert.Equal("{}", ParsedDataConverter.ToProtoValue(new object()));
        Assert.Equal("{\"a\":{}}", ParsedDataConverter.ToProtoValue(new Dictionary<string, object> { ["a"] = new object() }));

        var options = new JsonSerializerOptions { Converters = { new SystemObjectJsonConverter() } };
        var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>("{\"n\":1,\"s\":\"x\"}", options);
        Assert.Equal(1, ((JsonElement)parsed!["n"]).GetInt32());
        Assert.Equal("x", ((JsonElement)parsed["s"]).GetString());
    }
}
