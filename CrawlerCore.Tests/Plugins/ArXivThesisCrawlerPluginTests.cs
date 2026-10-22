using CrawlerCore.Common;
using CrawlerCore.Entities;
using CrawlerPlugins.ThesisPlugin;

namespace CrawlerCore.Tests.Plugins;

/// <summary>
/// ArXivThesisPlugin 的解析测试（W-05、W-06、W-25）。夹具是手写的最小 HTML，只模仿 arXiv 详情页的结构，
/// 标题、作者、摘要等内容都是虚构的，不复制任何真实页面。
/// </summary>
public class ArXivThesisCrawlerPluginTests
{
    private const string AbsUrl = "https://arxiv.org/abs/2401.01234";

    /// <summary>完整结构：多位作者（含后缀与 HTML 实体）、摘要中有链接、有 Comments 行、有元数据</summary>
    private const string FullPage = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <title>[2401.01234] A Toy Study of Example Graphs</title>
          <meta name="citation_title" content="A Toy Study of Example Graphs" />
          <meta name="citation_author" content="Example, Alice" />
          <meta name="citation_author" content="Sample, Bob" />
          <meta name="citation_author" content="Placeholder, Carol" />
          <meta name="citation_date" content="2024/01/05" />
          <meta name="citation_arxiv_id" content="2401.01234" />
          <meta name="citation_abstract" content="We study toy graphs." />
        </head>
        <body>
          <div class="header-breadcrumbs"><a href="/">arXiv</a> &gt; <a href="/list/cs/recent">cs</a> &gt; arXiv:2401.01234</div>
          <div id="abs">
            <div class="dateline">[Submitted on 5 Jan 2024 (<a href="/abs/2401.01234v1">v1</a>), last revised 9 Feb 2024 (this version, v2)]</div>
            <h1 class="title mathjax"><span class="descriptor">Title:</span>A Toy Study of
              Example Graphs</h1>
            <div class="authors"><span class="descriptor">Authors:</span><a href="/a/example_a_1">Alice Example</a>, <a href="/a/sample_b_1">Bob Sample Jr.</a>, <a href="/a/placeholder_c_1">Carol Plac&#xe9;holder</a></div>
            <blockquote class="abstract mathjax">
              <span class="descriptor">Abstract:</span>We study toy graphs with three &amp; more nodes.
              Code is available at <a href="https://example.org/code">this https URL</a>. Results hold.
            </blockquote>
            <div class="metatable"><table summary="Additional metadata">
              <tr><td class="tablecell label">Comments:</td><td class="tablecell comments mathjax">12 pages, 3 figures</td></tr>
              <tr><td class="tablecell label">Subjects:</td><td class="tablecell subjects"><span class="primary-subject">Machine Learning (cs.LG)</span>; Discrete Mathematics (cs.DM)</td></tr>
              <tr><td class="tablecell label">Cite as:</td><td class="tablecell arxivid"><span class="arxivid"><a href="/abs/2401.01234">arXiv:2401.01234</a> [cs.LG]</span></td></tr>
              <tr><td class="tablecell label">&nbsp;</td><td class="tablecell arxivdoi"><a href="https://doi.org/10.48550/arXiv.2401.01234" id="arxiv-doi-link">https://doi.org/10.48550/arXiv.2401.01234</a></td></tr>
            </table></div>
          </div>
        </body>
        </html>
        """;

    /// <summary>只有页面结构、没有元数据：单一作者，摘要中没有链接，没有 Comments 行</summary>
    private const string StructureOnlyPage = """
        <html><body>
          <div id="abs">
            <div class="dateline">[Submitted on 17 Mar 2023]</div>
            <h1 class="title"><span class="descriptor">Title:</span>Notes on Imaginary Lattices</h1>
            <div class="authors"><span class="descriptor">Authors:</span><a href="/a/solo_d_1">Dana Solo</a></div>
            <blockquote class="abstract"><span class="descriptor">Abstract:</span>  A short abstract
               that spans two lines.  </blockquote>
            <table>
              <tr><td class="tablecell label">Subjects:</td><td class="tablecell subjects">Combinatorics (math.CO)</td></tr>
              <tr><td class="tablecell label">Cite as:</td><td class="tablecell arxivid"><span class="arxivid"><a href="/abs/2303.04567v3">arXiv:2303.04567v3</a> [math.CO]</span></td></tr>
            </table>
          </div>
        </body></html>
        """;

    /// <summary>页面结构缺失，只有给机器读取的 citation_* 元数据</summary>
    private const string MetaOnlyPage = """
        <html><head>
          <meta name="citation_title" content="Fallback Title &amp; More" />
          <meta name="citation_author" content="Fallback, Erin" />
          <meta name="citation_author" content="Mononym" />
          <meta name="citation_date" content="2022/11/30" />
          <meta name="citation_arxiv_id" content="2211.09999" />
          <meta name="citation_abstract" content="Abstract taken from metadata." />
        </head><body><p>Layout changed.</p></body></html>
        """;

    private static Task<PluginResult> ParseAsync(string html, string url = AbsUrl)
        => new ArXivThesisCrawlerPlugin().ExecuteAsync(new PluginContext { Html = html, Url = url });

    private static string Text(PluginResult result, string key) => Assert.IsType<string>(result.Data[key]);

    private static List<string> List(PluginResult result, string key) => Assert.IsType<List<string>>(result.Data[key]);

    [Fact(DisplayName = "W-05：多位作者全部解析出来，保持“名 姓”格式与后缀，解码 HTML 实体")]
    public async Task Authors_AllAuthorsFromAuthorLinks()
    {
        var result = await ParseAsync(FullPage);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "Alice Example", "Bob Sample Jr.", "Carol Placéholder" }, List(result, "AuthorsList"));
        Assert.Equal("Alice Example, Bob Sample Jr., Carol Placéholder", Text(result, "Authors"));
    }

    [Fact(DisplayName = "W-05：单一作者")]
    public async Task Authors_SingleAuthor()
    {
        var result = await ParseAsync(StructureOnlyPage, "https://arxiv.org/abs/2303.04567");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "Dana Solo" }, List(result, "AuthorsList"));
        Assert.Equal("Dana Solo", Text(result, "Authors"));
    }

    [Fact(DisplayName = "W-06：摘要中有链接时保留完整摘要（链接文字原样保留），不混入 Comments 行，不带 Abstract: 标签")]
    public async Task Abstract_WithLink_IsComplete()
    {
        var result = await ParseAsync(FullPage);

        Assert.Equal("We study toy graphs with three & more nodes. Code is available at this https URL. Results hold.", Text(result, "Abstract"));
        Assert.Equal("12 pages, 3 figures", Text(result, "Comment"));
    }

    [Fact(DisplayName = "W-06：摘要没有链接、页面没有 Comments 行")]
    public async Task Abstract_WithoutLinkOrComments()
    {
        var result = await ParseAsync(StructureOnlyPage, "https://arxiv.org/abs/2303.04567");

        Assert.Equal("A short abstract that spans two lines.", Text(result, "Abstract"));
        Assert.Equal(string.Empty, Text(result, "Comment"));
    }

    [Fact(DisplayName = "标题去掉 Title: 标签并折叠换行")]
    public async Task Title_FromHeading()
    {
        var result = await ParseAsync(FullPage);
        Assert.Equal("A Toy Study of Example Graphs", Text(result, "Title"));
    }

    [Fact(DisplayName = "其余字段：编号、分类、提交日期、DOI、PDF 链接、来源")]
    public async Task OtherFields()
    {
        var result = await ParseAsync(FullPage);

        Assert.Equal("2401.01234", Text(result, "ArXivId"));
        Assert.Equal(new[] { "Machine Learning (cs.LG)", "Discrete Mathematics (cs.DM)" }, List(result, "SubjectsList"));
        Assert.Equal("Machine Learning (cs.LG); Discrete Mathematics (cs.DM)", Text(result, "Subjects"));
        Assert.Equal("5 Jan 2024", Text(result, "PublicationDate"));
        Assert.Equal("10.48550/arXiv.2401.01234", Text(result, "DOI"));
        Assert.Equal("https://arxiv.org/pdf/2401.01234.pdf", Text(result, "PdfUrl"));
        Assert.Equal(AbsUrl, Text(result, "SourceUrl"));
        Assert.Equal(AbsUrl, Text(result, "NormalizedUrl"));
    }

    [Fact(DisplayName = "编号从 Cite as 取得时去掉版本号；没有 DOI 链接时为空")]
    public async Task ArXivId_FromCiteAs_WithoutVersion()
    {
        var result = await ParseAsync(StructureOnlyPage, "https://arxiv.org/abs/2303.04567v3");

        Assert.Equal("2303.04567", Text(result, "ArXivId"));
        Assert.Equal("17 Mar 2023", Text(result, "PublicationDate"));
        Assert.Equal(string.Empty, Text(result, "DOI"));
        Assert.Equal("https://arxiv.org/pdf/2303.04567.pdf", Text(result, "PdfUrl"));
    }

    [Fact(DisplayName = "页面结构缺失时回退到 citation_* 元数据，作者由“姓, 名”转为“名 姓”")]
    public async Task FallsBackToCitationMetadata()
    {
        var result = await ParseAsync(MetaOnlyPage, "https://arxiv.org/abs/2211.09999");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("Fallback Title & More", Text(result, "Title"));
        Assert.Equal(new[] { "Erin Fallback", "Mononym" }, List(result, "AuthorsList"));
        Assert.Equal("Abstract taken from metadata.", Text(result, "Abstract"));
        Assert.Equal("30 Nov 2022", Text(result, "PublicationDate"));
        Assert.Equal("2211.09999", Text(result, "ArXivId"));
    }

    [Fact(DisplayName = "既没有页面结构也没有元数据时解析失败，而不是输出错误数据")]
    public async Task UnknownStructure_Fails()
    {
        var result = await ParseAsync("<html><body><p>Title: Something Authors: Someone Abstract: text Subjects: x</p></body></html>");

        Assert.False(result.Success);
        Assert.Contains("页面结构不匹配", result.ErrorMessage);
    }

    [Theory(DisplayName = "只解析 /abs/ 详情页")]
    [InlineData("https://arxiv.org/list/cs.LG/recent")]
    [InlineData("https://arxiv.org/search/?query=x")]
    public async Task NonAbstractPage_Fails(string url)
    {
        var result = await ParseAsync(FullPage, url);
        Assert.False(result.Success);
    }

    [Theory(DisplayName = "作用域：只跟随论文详情页")]
    [InlineData("https://arxiv.org/abs/2401.01234", true)]
    [InlineData("http://arxiv.org/abs/2401.01234", true)]
    [InlineData("https://arxiv.org/search/?query=graphs", false)]
    [InlineData("https://arxiv.org/list/cs.LG/recent", false)]
    [InlineData("https://arxiv.org/pdf/2401.01234", false)]
    [InlineData("https://example.org/abs/2401.01234", false)]
    public void ShouldFollowLink_OnlyAbstractPages(string url, bool expected)
    {
        Assert.Equal(expected, new ArXivThesisCrawlerPlugin().ShouldFollowLink(url, 1));
    }

    [Fact(DisplayName = "W-25 契约：插件输出经上报转换后满足 DataAnalysis 的输入契约")]
    public async Task Output_SatisfiesParsedDataContract()
    {
        var result = await ParseAsync(FullPage);

        var map = ParsedDataConverter.ToProtoMap(result.Data);

        ParsedDataContract.AssertSatisfiedBy(map);
        Assert.Equal(new[] { "Alice Example", "Bob Sample Jr.", "Carol Placéholder" }, ParsedDataContract.ReadList(map, "AuthorsList"));
        Assert.Equal(new[] { "Machine Learning (cs.LG)", "Discrete Mathematics (cs.DM)" }, ParsedDataContract.ReadList(map, "SubjectsList"));
    }
}
