using System;
using System.Collections.Generic;
using Zentwik.Core.Ipc;
using Zentwik.Core.Services;
using Zentwik.Worker;
using Xunit;

namespace Zentwik.Tests
{
    /// <summary>snapshot-logs 的窗口与首行统计：窗口大小、字节预算、最近一条异常。</summary>
    public sealed class SnapshotLogsTests
    {
        private static LogEntry Entry(int minute, LogLevel level, string message) => new()
        {
            Time = new DateTime(2026, 11, 18, 9, minute % 60, 0),
            Level = level,
            Message = message
        };

        private static List<LogEntry> Many(int count)
        {
            List<LogEntry> entries = new(count);

            for (int i = 0; i < count; i++)
                entries.Add(Entry(i, LogLevel.Info, $"line-{i}"));

            return entries;
        }

        [Fact]
        public void Window_keeps_the_newest_entries_up_to_the_limit()
        {
            List<LogEntry> entries = Many(260);

            int start = PipeServerService.SelectSnapshotStart(
                entries, PipeServerService.SnapshotLogLimit, PipeServerService.SnapshotLogBudgetBytes);

            Assert.Equal(60, start);
            Assert.Equal(200, entries.Count - start);
        }

        [Fact]
        public void Window_stops_at_the_byte_budget()
        {
            List<LogEntry> entries = new()
            {
                Entry(1, LogLevel.Info, new string('a', 5000)),
                Entry(2, LogLevel.Info, new string('b', 5000)),
                Entry(3, LogLevel.Info, new string('c', 5000)),
            };

            // 每条估算 33 + 5000×3 ≈ 15 KB，40 KB 预算只放得下两条。
            int start = PipeServerService.SelectSnapshotStart(entries, 200, 40 * 1024);

            Assert.Equal(1, start);
        }

        [Fact]
        public void Stats_line_carries_the_last_issue()
        {
            List<LogEntry> entries = Many(3);

            string payload = PipeServerService.BuildSnapshotPayload(
                entries, 0, 1200, 1000, 150, 48, 2,
                Entry(40, LogLevel.Error, "拒绝访问\n第二行"));

            string[] lines = payload.Split('\n');
            string[] stats = lines[0].Split('|');

            Assert.Equal(9, stats.Length);
            Assert.Equal("#stats", stats[0]);
            Assert.Equal("1200", stats[1]);
            Assert.Equal("48", stats[4]);
            Assert.Equal("ERROR", stats[7]);
            Assert.Equal("拒绝访问\\n第二行", stats[8]);
            Assert.Equal(4, lines.Length);
        }

        [Fact]
        public void Stats_line_leaves_issue_fields_empty_when_there_is_none()
        {
            string payload = PipeServerService.BuildSnapshotPayload(
                Many(1), 0, 1, 1, 0, 0, 0, issue: null);

            Assert.Equal("#stats|1|1|0|0|0|||", payload.Split('\n')[0]);
        }

        [Fact]
        public void Issue_message_is_clipped_so_the_stats_line_stays_short()
        {
            string payload = PipeServerService.BuildSnapshotPayload(
                Array.Empty<LogEntry>(), 0, 1, 0, 0, 1, 9, Entry(1, LogLevel.Error, new string('x', 900)));

            string message = payload.Split('|', 9)[8];

            Assert.Equal(301, message.Length);
            Assert.EndsWith("…", message, StringComparison.Ordinal);
        }
    }
}
