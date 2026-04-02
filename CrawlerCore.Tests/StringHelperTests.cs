using Xunit;
using CrawlerCore.Common;

namespace CrawlerCore.Tests.Common
{
    public class StringHelperTests
    {
        [Fact]
        public void IsNullOrEmpty_EmptyOrWhitespace_ReturnsTrue()
        {
            Assert.True(StringHelper.IsNullOrEmpty(null));
            Assert.True(StringHelper.IsNullOrEmpty(""));
            Assert.True(StringHelper.IsNullOrEmpty("   "));
        }

        [Fact]
        public void Md5Hash_InputTest_ReturnsCorrectHash()
        {
            string result = StringHelper.Md5Hash("test");
            Assert.Equal("098f6bcd4621d373cade4e832627b4f6", result); // 标准MD5值
        }

        [Fact]
        public void Truncate_LongString_AppendsEllipsis()
        {
            string input = "这是一个超长字符串用于测试截断功能";
            string result = StringHelper.Truncate(input, 10);
            Assert.Equal("这是一个超长字...", result); // 长度10-3=7个字符+...
        }

        [Fact]
        public void Md5Hash_NullInput_ReturnsEmptyStringHash()
        {
            string result = StringHelper.Md5Hash(null);
            Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", result); // 空字符串的MD5值
        }

        [Fact]
        public void Truncate_MaxLength3_ReturnsEllipsisOnly()
        {
            string input = "123456";
            string result = StringHelper.Truncate(input, 3);
            Assert.Equal("...", result);
        }

        [Fact]
        public void Truncate_NullInput_ReturnsNull()
        {
            string result = StringHelper.Truncate(null, 10);
            Assert.Null(result);
        }
    }
}