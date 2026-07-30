using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace XHeadSender
{
    internal sealed class SourceScheduleEntry
    {
        public DateTime? Start { get; set; }
        public TimeSpan? DailyTime { get; set; }
        public string Path { get; set; }

        public DateTime EffectiveStart(DateTime now)
        {
            if (Start.HasValue) return Start.Value;
            DateTime today = now.Date.Add(DailyTime.Value);
            return today <= now ? today : today.AddDays(-1);
        }
    }

    internal static class SourceSchedule
    {
        public static List<SourceScheduleEntry> Load(string schedulePath)
        {
            var result = new List<SourceScheduleEntry>();
            int lineNumber = 0;
            foreach (string rawLine in File.ReadAllLines(schedulePath))
            {
                lineNumber++;
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int separator = line.IndexOf('|');
                if (separator <= 0 || separator == line.Length - 1)
                    throw new InvalidDataException($"{lineNumber}行目: 「時刻|ファイル」の形式ではありません。");
                string when = line.Substring(0, separator).Trim();
                string path = Environment.ExpandEnvironmentVariables(line.Substring(separator + 1).Trim().Trim('"'));
                if (!System.IO.Path.IsPathRooted(path))
                    path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(schedulePath)), path));
                if (!File.Exists(path))
                    throw new FileNotFoundException($"{lineNumber}行目の素材が見つかりません。", path);

                var entry = new SourceScheduleEntry { Path = path };
                if (when.StartsWith("毎日", StringComparison.Ordinal))
                {
                    if (!TimeSpan.TryParseExact(when.Substring(2).Trim(), @"hh\:mm\:ss",
                        CultureInfo.InvariantCulture, out TimeSpan daily))
                        throw new InvalidDataException($"{lineNumber}行目: 毎日の時刻はHH:mm:ssで指定してください。");
                    entry.DailyTime = daily;
                }
                else if (DateTime.TryParseExact(when,
                    new[] { "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss" },
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime start))
                {
                    entry.Start = start;
                }
                else
                {
                    throw new InvalidDataException($"{lineNumber}行目: 日時を解釈できません: {when}");
                }
                result.Add(entry);
            }
            if (result.Count == 0) throw new InvalidDataException("有効なスケジュール項目がありません。");
            return result;
        }

        public static SourceScheduleEntry GetActive(IEnumerable<SourceScheduleEntry> entries, DateTime now)
        {
            return entries
                .Select(e => new { Entry = e, Start = e.EffectiveStart(now) })
                .Where(x => x.Start <= now)
                .OrderByDescending(x => x.Start)
                .Select(x => x.Entry)
                .FirstOrDefault();
        }
    }
}
