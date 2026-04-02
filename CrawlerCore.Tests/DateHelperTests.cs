using Xunit;
using CrawlerCore.Common;
using System;

namespace CrawlerCore.Tests.Common
{
    public class DateHelperTests
    {
        private readonly DateTime _testDate = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void ToUtcString_ValidDate_ReturnsFormattedString()
        {
            string result = DateHelper.ToUtcString(_testDate);
            Assert.Equal("2025-01-01 10:00:00", result);
        }

        [Fact]
        public void ToUtcString_NullDate_ReturnsEmpty()
        {
            string result = DateHelper.ToUtcString(null);
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void GetTimeSpanSeconds_TwoDates_ReturnsCorrectSeconds()
        {
            DateTime end = _testDate.AddMinutes(5);
            double result = DateHelper.GetTimeSpanSeconds(_testDate, end);
            Assert.Equal(300, result); // 5分钟=300秒
        }

        [Fact]
        public void ToUtcString_LocalDateTime_ConvertsToUtc()
        {
            // 本地时间（假设本地时区为东8区）
            DateTime localDate = new DateTime(2025, 1, 1, 18, 0, 0, DateTimeKind.Local);
            string result = DateHelper.ToUtcString(localDate);
            Assert.Equal("2025-01-01 10:00:00", result); // 东8区→UTC+0，减8小时
        }

        [Fact]
        public void GetTimeSpanSeconds_EndBeforeStart_ReturnsNegative()
        {
            DateTime start = new DateTime(2025, 1, 1, 10, 0, 0);
            DateTime end = new DateTime(2025, 1, 1, 9, 0, 0);
            double result = DateHelper.GetTimeSpanSeconds(start, end);
            Assert.Equal(-3600, result); // 相差-3600秒（1小时）
        }

        [Fact]
        public void ToUtcString_CustomFormat_ReturnsFormattedString()
        {
            DateTime testDate = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);
            string result = DateHelper.ToUtcString(testDate, "yyyy-MM-dd");
            Assert.Equal("2025-01-01", result);
        }
    }
}