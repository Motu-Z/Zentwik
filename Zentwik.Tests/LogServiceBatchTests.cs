using System.Globalization;
using System.IO;
using Zentwik.Core.Services;
using Xunit;

namespace Zentwik.Tests
{
    /// <summary>落盘链路：写 14 条 → Flush → 当天文件 14 行。500ms 批次窗口不在此测（后台线程无法用替身时钟驱动）。</summary>
    public sealed class LogServiceBatchTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "Zentwik.Tests", Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        private LogService NewLog() => new(new LogOptions { Directory = _dir, FilePrefix = "test-", RetentionDays = 1 });

        [Fact]
        public void Flush_writes_all_pending_entries_to_todays_file()
        {
            using LogService log = NewLog();
            for (int i = 0; i < 10; i++) log.Info($"info-{i}");
            for (int i = 0; i < 3; i++) log.Warn($"warn-{i}");
            log.Critical("critical-0");
            log.Flush();

            string file = Path.Combine(_dir, $"test-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
            string[] lines = ReadUntil(file, expected: 14);
            string text = string.Join('\n', lines);
            Assert.Equal(14, lines.Length);
            for (int i = 0; i < 10; i++) Assert.Contains($"info-{i}", text);
            for (int i = 0; i < 3; i++) Assert.Contains($"warn-{i}", text);
            Assert.Contains("critical-0", text);
        }

        [Fact]
        public void Snapshot_and_counters_reflect_writes()
        {
            using LogService log = NewLog();
            log.Info("a"); log.Warn("b"); log.Critical("c");
            Assert.Equal(3, log.Snapshot().Count);
            Assert.Equal(1, log.CountWarn);
        }

        /// <summary>累计条数不能跟着缓冲一起缩水：界面拿它显示「总共多少条」。</summary>
        [Fact]
        public void Total_counts_ignore_ring_buffer_trim()
        {
            using LogService log = NewLog();

            for (int i = 0; i < 300; i++)
                log.Info($"i-{i}");

            Assert.Equal(300, log.TotalCount);
            Assert.Equal(300, log.TotalInfo);
            Assert.True(log.Count < 300, "缓冲应已挤出窗口，否则这条测试没有覆盖到裁剪路径");
        }

        [Fact]
        public void Clear_resets_total_counts()
        {
            using LogService log = NewLog();
            log.Info("a");
            log.Warn("b");

            Assert.Equal(2, log.TotalCount);

            log.Clear();

            Assert.Equal(0, log.TotalCount);
            Assert.Equal(0, log.TotalWarn);
        }

        /// <summary>最近异常在缓冲之外另记一份：挤掉窗口以后，界面的「最新异常」还得有东西可显示。</summary>
        [Fact]
        public void Last_issue_survives_ring_buffer_trim()
        {
            using LogService log = NewLog();

            log.Warn("warn-0");
            Assert.Equal("WARN", log.LastIssue?.LevelText);

            log.Error("error-0");

            for (int i = 0; i < 400; i++)
                log.Info($"i-{i}");

            Assert.DoesNotContain(log.Snapshot(), e => e.Level == LogLevel.Error);
            Assert.Equal("ERROR", log.LastIssue?.LevelText);
            Assert.Equal("error-0", log.LastIssue?.Message);

            log.Clear();
            Assert.Null(log.LastIssue);
        }

        /// <summary>等到文件凑够 expected 行：Flush 已提交内容，这里只吃掉「批次被取走但未落盘」的窗口。</summary>
        private static string[] ReadUntil(string file, int expected)
        {
            string text = string.Empty;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                try { text = File.ReadAllText(file); } catch (IOException) { }
                if (text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length >= expected)
                    break;
                Thread.Sleep(50);
            }
            return text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
