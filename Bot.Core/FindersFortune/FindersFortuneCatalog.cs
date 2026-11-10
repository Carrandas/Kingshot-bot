using System.Globalization;
using System.Text;

namespace Bot.Core.FindersFortune
{
    /// <summary>A tap point for one item on one Finder's Fortune map.</summary>
    public sealed record ItemSpot(string Map, string Item, int X, int Y, string? Note);

    /// <summary>
    /// The item spots in item-locations.txt ("map | item | x,y | note"). Comments and order
    /// survive a save, because the file is also read and edited by hand.
    /// </summary>
    public sealed class FindersFortuneCatalog
    {
        private const char Separator = '|';
        private readonly List<string> _lines;
        private readonly List<ItemSpot> _spots = new();

        private FindersFortuneCatalog(IEnumerable<string> lines)
        {
            _lines = lines.ToList();
            _spots.AddRange(_lines.Select(ParseLine).OfType<ItemSpot>());
        }

        public IReadOnlyList<ItemSpot> Spots => _spots;

        public static FindersFortuneCatalog Load(string path) => new(File.ReadAllLines(path));

        public static FindersFortuneCatalog Parse(string text) => new(text.Split('\n').Select(l => l.TrimEnd('\r')));

        public IEnumerable<string> Lines => _lines;

        public void Save(string path) => File.WriteAllLines(path, _lines);

        /// <summary>The spot for an OCR'd label on this map, or null when no name is close enough.</summary>
        public ItemSpot? Match(string map, string ocrText) =>
            BestMatch(_spots.Where(s => SameMap(s.Map, map)), ocrText);

        /// <summary>Maps with an item this label could be. One result means the label identifies the map.</summary>
        public IReadOnlyList<string> MapsWith(string ocrText) =>
            _spots.GroupBy(s => s.Map, StringComparer.OrdinalIgnoreCase)
                .Where(g => BestMatch(g, ocrText) != null)
                .Select(g => g.Key)
                .ToList();

        /// <summary>Adds the item, or moves it when the map already has it.</summary>
        public void Upsert(string map, string item, int x, int y, string? note)
        {
            var spot = new ItemSpot(map, item, x, y, note);
            var text = FormatLine(spot);
            var index = _lines.FindIndex(l => ParseLine(l) is { } s && SameMap(s.Map, map) && SameName(s.Item, item));
            if (index >= 0)
            {
                _lines[index] = text;
                _spots[_spots.FindIndex(s => SameMap(s.Map, map) && SameName(s.Item, item))] = spot;
                return;
            }

            var last = _lines.FindLastIndex(l => ParseLine(l) is { } s && SameMap(s.Map, map));
            if (last >= 0)
            {
                _lines.Insert(last + 1, text);
            }
            else
            {
                _lines.Add(string.Empty);
                _lines.Add(text);
            }

            _spots.Add(spot);
        }

        /// <summary>Lower-case letters and digits only, so OCR spacing and quotes don't matter.</summary>
        public static string Normalize(string text)
        {
            var result = new StringBuilder(text.Length);
            foreach (var c in text.Where(char.IsLetterOrDigit))
            {
                result.Append(char.ToLower(c, CultureInfo.InvariantCulture));
            }

            return result.ToString();
        }

        /// <summary>Whether OCR text is close enough to count as this name.</summary>
        public static bool IsCloseMatch(string name, string ocrText)
        {
            var wanted = Normalize(name);
            return EditDistance(wanted, Normalize(ocrText)) <= AllowedEdits(wanted.Length);
        }

        private static ItemSpot? BestMatch(IEnumerable<ItemSpot> spots, string ocrText)
        {
            var read = Normalize(ocrText);
            if (read.Length == 0)
            {
                return null;
            }

            var ranked = spots
                .Select(s => (Spot: s, Wanted: Normalize(s.Item)))
                .Select(c => (c.Spot, Edits: EditDistance(c.Wanted, read), Allowed: AllowedEdits(c.Wanted.Length)))
                .Where(c => c.Edits <= c.Allowed)
                .OrderBy(c => c.Edits)
                .ToList();

            // Two names equally close means the read proves neither.
            var isTie = ranked.Count > 1 && ranked[0].Edits == ranked[1].Edits;
            return ranked.Count == 0 || isTie ? null : ranked[0].Spot;
        }

        /// <summary>Strict on purpose: short names are one letter from other items (Clock/Cloth).</summary>
        private static int AllowedEdits(int length) => length switch
        {
            <= 5 => 0,
            <= 9 => 1,
            _ => 2,
        };

        private static int EditDistance(string a, string b)
        {
            var previous = Enumerable.Range(0, b.Length + 1).ToArray();
            for (var i = 1; i <= a.Length; i++)
            {
                var current = new int[b.Length + 1];
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                    current[j] = Math.Min(substitution, Math.Min(previous[j], current[j - 1]) + 1);
                }

                previous = current;
            }

            return previous[b.Length];
        }

        private static ItemSpot? ParseLine(string line)
        {
            if (line.TrimStart().StartsWith('#'))
            {
                return null;
            }

            var parts = line.Split(Separator, StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                return null;
            }

            var xy = parts[2].Split(',');
            var hasPoint = xy.Length == 2
                && int.TryParse(xy[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                && int.TryParse(xy[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
            if (!hasPoint)
            {
                return null;
            }

            var note = parts.Length > 3 ? parts[3] : null;
            return new ItemSpot(
                parts[0],
                parts[1],
                int.Parse(xy[0], CultureInfo.InvariantCulture),
                int.Parse(xy[1], CultureInfo.InvariantCulture),
                note);
        }

        private static string FormatLine(ItemSpot spot)
        {
            var point = string.Create(CultureInfo.InvariantCulture, $"{spot.X},{spot.Y}");
            var text = $"{spot.Map} {Separator} {spot.Item} {Separator} {point}";
            return string.IsNullOrEmpty(spot.Note) ? text : $"{text} {Separator} {spot.Note}";
        }

        private static bool SameMap(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static bool SameName(string a, string b) => Normalize(a) == Normalize(b);
    }
}
