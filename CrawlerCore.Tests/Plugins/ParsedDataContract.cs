using System.Text.Json;

namespace CrawlerCore.Tests.Plugins;

/// <summary>
/// parsedData 契约（W-25）：论文类插件上报给 Master、最终导出到 JSON 的键，是 DataAnalysis/src/data_processor.py 的输入。
/// 键名与含义见 CLAUDE.md“协议与数据契约”。T6 的 arXiv API 插件也应通过同一个断言。
/// </summary>
public static class ParsedDataContract
{
    /// <summary>DataAnalysis 读取的键</summary>
    public static readonly string[] RequiredKeys =
    [
        "ArXivId", "Title", "Authors", "AuthorsList", "Abstract", "Subjects", "SubjectsList",
        "PublicationDate", "DOI", "PdfUrl", "SourceUrl"
    ];

    /// <summary>ThesisPlugin 额外输出、DataAnalysis 目前不读的键</summary>
    public static readonly string[] OptionalKeys = ["NormalizedUrl", "Comment"];

    /// <summary>
    /// 断言上报后的 parsedData（协议中的 map&lt;string, string&gt;）满足契约：键齐全；列表键是 JSON 字符串数组，
    /// 并且与对应的拼接字段一致；标题非空；日期能被解析
    /// </summary>
    public static void AssertSatisfiedBy(IReadOnlyDictionary<string, string> parsedData)
    {
        foreach (var key in RequiredKeys)
        {
            Assert.True(parsedData.ContainsKey(key), $"parsedData 缺少契约键 {key}");
        }

        Assert.False(string.IsNullOrWhiteSpace(parsedData["Title"]), "Title 不能为空");

        var authors = ReadList(parsedData, "AuthorsList");
        Assert.Equal(string.Join(", ", authors), parsedData["Authors"]);
        var subjects = ReadList(parsedData, "SubjectsList");
        Assert.Equal(string.Join("; ", subjects), parsedData["Subjects"]);

        // data_processor.py 用 pd.to_datetime(errors='coerce') 解析年份；这里要求能被常见格式解析
        if (!string.IsNullOrEmpty(parsedData["PublicationDate"]))
        {
            Assert.True(DateTime.TryParse(parsedData["PublicationDate"], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _), $"PublicationDate 无法解析：{parsedData["PublicationDate"]}");
        }
    }

    /// <summary>按 DataAnalysis 的方式（json.loads）读取列表键</summary>
    public static List<string> ReadList(IReadOnlyDictionary<string, string> parsedData, string key)
    {
        var list = JsonSerializer.Deserialize<List<string>>(parsedData[key]);
        Assert.NotNull(list);
        return list!;
    }
}
