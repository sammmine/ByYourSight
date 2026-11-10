using System;
using System.Collections.Generic;
using System.Linq;

namespace ByYourSight.Perception
{
    /// <summary>One object seen in the current camera frame, for the "Ada apa saja?" summary.</summary>
    public struct ViewItem
    {
        public string Label;
        public bool IsPerson;
        public float CenterX; // 0 = left edge of the frame, 1 = right edge
        public float Confidence;
    }

    /// <summary>
    /// Builds the spoken "Ada apa saja?" sentence: people first, then objects from left to right,
    /// e.g. "Orang di depan. Kursi agak kiri. Laptop dan cangkir agak kanan. Dan 2 lainnya."
    /// Identical objects in one zone are counted ("2 kursi"). Has no Unity dependency.
    /// </summary>
    public static class ViewSummaryBuilder
    {
        private static readonly string[] ZonePhrases = { "agak kiri", "di depan", "agak kanan" };

        private class Group
        {
            public string Label;
            public bool IsPerson;
            public int Zone;
            public int Count;
            public float Confidence;
        }

        /// <param name="centerHalfWidth">Items within this distance of the frame center count as "di depan".</param>
        /// <param name="maxGroups">Maximum number of label-and-zone groups spoken in the short sentence.</param>
        public static bool TryBuild(IEnumerable<ViewItem> items, float centerHalfWidth, int maxGroups,
            out string shortText, out string fullText)
        {
            List<Group> groups = BuildGroups(items, centerHalfWidth);
            if (groups.Count == 0)
            {
                shortText = "";
                fullText = "";
                return false;
            }

            List<Group> ranked = groups
                .OrderByDescending(g => g.IsPerson)
                .ThenByDescending(g => g.Confidence)
                .ToList();

            List<Group> spoken = ranked.Take(Math.Max(1, maxGroups)).ToList();
            int hiddenObjects = ranked.Skip(spoken.Count).Sum(g => g.Count);

            shortText = Compose(spoken);
            if (hiddenObjects > 0)
                shortText += $" Dan {hiddenObjects} lainnya.";
            fullText = Compose(ranked);
            return true;
        }

        private static List<Group> BuildGroups(IEnumerable<ViewItem> items, float centerHalfWidth)
        {
            List<Group> groups = new List<Group>();
            foreach (ViewItem item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Label))
                    continue;

                string label = item.Label.Trim().ToLowerInvariant();
                int zone = item.CenterX < 0.5f - centerHalfWidth ? 0 : (item.CenterX > 0.5f + centerHalfWidth ? 2 : 1);

                Group group = groups.Find(g => g.Label == label && g.Zone == zone);
                if (group == null)
                {
                    group = new Group { Label = label, IsPerson = item.IsPerson, Zone = zone };
                    groups.Add(group);
                }
                group.Count++;
                group.Confidence = Math.Max(group.Confidence, item.Confidence);
            }
            return groups;
        }

        private static string Compose(List<Group> groups)
        {
            List<string> sentences = new List<string>();
            foreach (bool people in new[] { true, false })
            {
                for (int zone = 0; zone < ZonePhrases.Length; zone++)
                {
                    List<string> names = groups
                        .Where(g => g.IsPerson == people && g.Zone == zone)
                        .OrderByDescending(g => g.Confidence)
                        .Select(g => g.Count > 1 ? $"{g.Count} {g.Label}" : g.Label)
                        .ToList();

                    if (names.Count > 0)
                        sentences.Add($"{Capitalize(JoinNames(names))} {ZonePhrases[zone]}.");
                }
            }
            return string.Join(" ", sentences);
        }

        private static string JoinNames(List<string> names)
        {
            if (names.Count == 1)
                return names[0];
            if (names.Count == 2)
                return $"{names[0]} dan {names[1]}";
            return $"{string.Join(", ", names.Take(names.Count - 1))}, dan {names[names.Count - 1]}";
        }

        private static string Capitalize(string s)
        {
            return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }
}
