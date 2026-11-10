using System.Diagnostics;
using System.Globalization;
using Bot.Core.FindersFortune;
using Bot.Core.Logging;
using OpenCvSharp;

namespace FindersFortuneSolver;

/// <summary>
/// Finder's Fortune helper. "run" plays while every name is known and exits with one STATUS line
/// when it needs help, so it can be restarted mid-stage. "learn" records a new item spot.
/// "analyze" reads saved screenshots without touching the emulator.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 1;
    private const int ExitHelpNeeded = 2;
    private const string DoneStatus = "DONE";
    private const string CatalogRelativePath = ".claude/skills/finders-fortune-solver/item-locations.txt";

    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options.Command is null)
        {
            Console.WriteLine("usage: analyze <png>... | capture <png> | run [--stages N] [--map NAME] [--bot-idle] | learn <map> <item> <x> <y> [<item> <x> <y>...] [--then-run] [--stages N] [--note TEXT] [--bot-idle]");
            return ExitUsage;
        }

        using var logger = new LogService();
        using var screen = new FindersFortuneScreen(logger);
        var catalogPath = options.CatalogPath ?? FindCatalog();
        var catalog = FindersFortuneCatalog.Load(catalogPath);
        var maps = new MapRecognizer(Path.Combine(Path.GetDirectoryName(catalogPath)!, "maps"), catalog);

        if (options.Command == "analyze")
        {
            Analyzer.Run(options.Positional, screen, catalog, maps, options.Map);
            return ExitOk;
        }

        var adb = new Adb(options.AdbPath, options.Serial);
        if (options.Command == "capture")
        {
            return Capture(adb, options.Positional[0], screen, catalog, maps, options.Map);
        }

        if (!options.BotIdle && Process.GetProcessesByName("KingshotAuto").Length > 0)
        {
            Console.WriteLine("STATUS BOT_OPEN KingshotAuto.exe is open: stop its cycle, then pass --bot-idle");
            return ExitHelpNeeded;
        }

        return LearnOrPlay(options, adb, screen, catalog, catalogPath, maps);
    }

    private static int LearnOrPlay(Options options, Adb adb, FindersFortuneScreen screen, FindersFortuneCatalog catalog, string catalogPath, MapRecognizer maps)
    {
        var status = options.Command == "learn" ? new Learner(adb, screen, catalog, catalogPath).Learn(options.Positional, options.Note) : null;
        if (status != null)
        {
            Console.WriteLine($"STATUS {status}");
        }

        if (status is null || (options.ThenRun && IsDone(status)))
        {
            status = new Player(adb, screen, catalog, catalogPath, maps, options).Run();
            Console.WriteLine($"STATUS {status}");
        }

        return IsDone(status) ? ExitOk : ExitHelpNeeded;
    }

    private static bool IsDone(string status) => status.StartsWith(DoneStatus, StringComparison.Ordinal);

    /// <summary>Takes one screenshot and reads it: a live check that needs no taps.</summary>
    private static int Capture(Adb adb, string path, FindersFortuneScreen screen, FindersFortuneCatalog catalog, MapRecognizer maps, string? map)
    {
        using var shot = adb.Screenshot();
        Cv2.ImWrite(path, shot);
        Analyzer.Run(new[] { path }, screen, catalog, maps, map);
        return ExitOk;
    }

    private static string FindCatalog()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, CatalogRelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not find {CatalogRelativePath} above {AppContext.BaseDirectory}");
    }
}

internal sealed class Options
{
    private static readonly string[] Commands = { "analyze", "capture", "run", "learn" };

    public string? Command { get; private set; }
    public List<string> Positional { get; } = new();
    public string? Map { get; private set; }
    public string? Note { get; private set; }
    public string? CatalogPath { get; private set; }
    public int Stages { get; private set; }
    public bool BotIdle { get; private set; }
    public bool ThenRun { get; private set; }
    public bool NoFlash { get; private set; }
    public string AdbPath { get; private set; } = @"C:\LDPlayer\LDPlayer14\adb.exe";
    public string Serial { get; private set; } = "emulator-5554";

    public static Options Parse(string[] args)
    {
        var options = new Options();
        if (args.Length == 0 || !Commands.Contains(args[0]))
        {
            return options;
        }

        options.Command = args[0];
        var next = 1;
        while (next < args.Length)
        {
            next = options.ReadArgument(args, next);
        }

        return options;
    }

    private int ReadArgument(string[] args, int i)
    {
        if (args[i] == "--bot-idle")
        {
            BotIdle = true;
            return i + 1;
        }

        if (args[i] == "--no-flash")
        {
            NoFlash = true;
            return i + 1;
        }

        if (args[i] == "--then-run")
        {
            ThenRun = true;
            return i + 1;
        }

        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            Positional.Add(args[i]);
            return i + 1;
        }

        SetValue(args[i], args[i + 1]);
        return i + 2;
    }

    private void SetValue(string name, string value)
    {
        switch (name)
        {
            case "--map": Map = value; break;
            case "--note": Note = value; break;
            case "--file": CatalogPath = value; break;
            case "--adb": AdbPath = value; break;
            case "--serial": Serial = value; break;
            case "--stages": Stages = int.Parse(value, CultureInfo.InvariantCulture); break;
            default: throw new ArgumentException($"Unknown option {name}");
        }
    }
}

/// <summary>Talks to the emulator with the adb executable, so it never shares the bot's connection.</summary>
internal sealed class Adb
{
    private const int RawBytesPerPixel = 4;
    private const int HalBgra8888 = 5;
    private readonly string _path;
    private readonly string _serial;

    public Adb(string path, string serial)
    {
        _path = path;
        _serial = serial;
    }

    /// <summary>Raw framebuffer: a header (width, height, format, ...) and then RGBA pixels.</summary>
    public Mat Screenshot()
    {
        using var process = Start($"-s {_serial} exec-out screencap");
        using var buffer = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(buffer);
        process.WaitForExit();
        var data = buffer.ToArray();

        var width = BitConverter.ToInt32(data, 0);
        var height = BitConverter.ToInt32(data, 4);
        var headerLength = data.Length - (width * height * RawBytesPerPixel);
        using var rgba = new Mat(height, width, MatType.CV_8UC4);
        System.Runtime.InteropServices.Marshal.Copy(data, headerLength, rgba.Data, width * height * RawBytesPerPixel);
        var bgr = new Mat();
        Cv2.CvtColor(rgba, bgr, BitConverter.ToInt32(data, 8) == HalBgra8888 ? ColorConversionCodes.BGRA2BGR : ColorConversionCodes.RGBA2BGR);
        return bgr;
    }

    /// <summary>
    /// All taps in one shell command: starting adb for each tap costs more than the taps.
    /// 100 ms between taps, or the game can drop one that comes too soon after the last.
    /// </summary>
    public void Tap(IEnumerable<(int X, int Y)> points)
    {
        var script = string.Join("; sleep 0.1; ", points.Select(p => string.Create(CultureInfo.InvariantCulture, $"input tap {p.X} {p.Y}")));
        using var process = Start($"-s {_serial} shell \"{script}\"");
        process.WaitForExit();
    }

    private Process Start(string arguments) => Process.Start(new ProcessStartInfo(_path, arguments)
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    }) ?? throw new InvalidOperationException("Could not start adb");
}

/// <summary>Works out the map from a saved thumbnail, or else from a label only one map has.</summary>
internal sealed class MapRecognizer
{
    private const double SameSceneScore = 0.90;
    private static readonly Rect Scene = new(64, 84, 592, 972);
    private static readonly Size ThumbnailSize = new(74, 122);
    private readonly string _directory;
    private readonly FindersFortuneCatalog _catalog;

    public MapRecognizer(string directory, FindersFortuneCatalog catalog)
    {
        _directory = directory;
        _catalog = catalog;
    }

    public string? Recognize(Mat shot, IEnumerable<FindersFortuneLabel> labels)
    {
        using var thumbnail = MakeThumbnail(shot);
        var known = Directory.Exists(_directory) ? Directory.GetFiles(_directory, "*.png") : Array.Empty<string>();
        var byPicture = known
            .Select(f => (File: f, Score: Compare(thumbnail, f)))
            .OrderByDescending(m => m.Score)
            .FirstOrDefault(m => m.Score >= SameSceneScore);
        if (byPicture.File != null)
        {
            return Path.GetFileNameWithoutExtension(byPicture.File).Replace('_', '?');
        }

        // A map with a picture has just been ruled out, so a shared name can't pick it.
        var candidates = labels
            .Select(l => _catalog.MapsWith(l.Text).Where(m => !known.Contains(PathFor(m))).ToList())
            .Where(m => m.Count == 1)
            .Select(m => m[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    public void Remember(string map, Mat shot)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(map);
        if (!File.Exists(path))
        {
            using var thumbnail = MakeThumbnail(shot);
            Cv2.ImWrite(path, thumbnail);
        }
    }

    private string PathFor(string map) => Path.Combine(_directory, map.Replace('?', '_') + ".png");

    private static Mat MakeThumbnail(Mat shot)
    {
        using var scene = new Mat(shot, Scene);
        using var gray = new Mat();
        Cv2.CvtColor(scene, gray, ColorConversionCodes.BGR2GRAY);
        var thumbnail = new Mat();
        Cv2.Resize(gray, thumbnail, ThumbnailSize, 0, 0, InterpolationFlags.Area);
        return thumbnail;
    }

    private static double Compare(Mat thumbnail, string file)
    {
        using var saved = Cv2.ImRead(file, ImreadModes.Grayscale);
        using var result = new Mat();
        Cv2.MatchTemplate(thumbnail, saved, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out double score);
        return score;
    }
}

internal static class Analyzer
{
    public static void Run(IEnumerable<string> files, FindersFortuneScreen screen, FindersFortuneCatalog catalog, MapRecognizer maps, string? forcedMap)
    {
        foreach (var file in files)
        {
            using var shot = Cv2.ImRead(file);
            var kind = FindersFortuneScreen.Classify(shot);
            var line = $"{Path.GetFileName(file)} {kind}";
            if (kind == FindersFortuneScreenKind.Stage)
            {
                line += " " + DescribeStage(shot, screen, catalog, maps, forcedMap);
            }
            else if (kind == FindersFortuneScreenKind.MapReady)
            {
                line += $" outOfBones={FindersFortuneScreen.IsOutOfBones(shot)}";
            }

            Console.WriteLine(line);
        }
    }

    private static string DescribeStage(Mat shot, FindersFortuneScreen screen, FindersFortuneCatalog catalog, MapRecognizer maps, string? forcedMap)
    {
        var labels = screen.ReadLabels(shot);
        var map = forcedMap ?? maps.Recognize(shot, labels.Where(l => !l.Struck));
        var names = labels.Select(l => DescribeLabel(l, map, catalog));
        return $"timer={screen.ReadTimer(shot)?.ToString(CultureInfo.InvariantCulture) ?? "?"} map={map ?? "?"} {string.Join(" ", names)}";
    }

    private static string DescribeLabel(FindersFortuneLabel label, string? map, FindersFortuneCatalog catalog)
    {
        if (label.Struck)
        {
            return "(struck)";
        }

        var spot = map is null ? null : catalog.Match(map, label.Text);
        return $"\"{label.Text}\"={spot?.Item ?? "?"}";
    }
}

/// <summary>Taps a new item and, once the game has accepted it, writes it to the file.</summary>
internal sealed class Learner
{
    private const int SettleMilliseconds = 1500;
    private const int ChangedPixels = 60;
    private readonly Adb _adb;
    private readonly FindersFortuneScreen _screen;
    private readonly FindersFortuneCatalog _catalog;
    private readonly string _catalogPath;

    public Learner(Adb adb, FindersFortuneScreen screen, FindersFortuneCatalog catalog, string catalogPath)
    {
        _adb = adb;
        _screen = screen;
        _catalog = catalog;
        _catalogPath = catalogPath;
    }

    public string Learn(IReadOnlyList<string> args, string? note)
    {
        if (args.Count < 4 || (args.Count - 1) % 3 != 0)
        {
            return "USAGE learn <map> <item> <x> <y> [<item> <x> <y>...]";
        }

        var learned = new List<string>();
        for (var first = 1; first < args.Count; first += 3)
        {
            var x = int.Parse(args[first + 1], CultureInfo.InvariantCulture);
            var y = int.Parse(args[first + 2], CultureInfo.InvariantCulture);
            var result = LearnOne(args[0], args[first], x, y, note);
            if (!result.StartsWith("DONE", StringComparison.Ordinal))
            {
                return $"{result} (learned first: {string.Join("; ", learned)})";
            }

            learned.Add(result);
        }

        return string.Join("; ", learned);
    }

    private string LearnOne(string map, string item, int x, int y, string? note)
    {
        using var before = _adb.Screenshot();
        var slot = FindSlot(before, item);
        if (slot < 0)
        {
            return $"NOT_SHOWING \"{item}\" is not an active label on a stage screen; nothing tapped";
        }

        if (!FindersFortuneTapPlan.IsSafeNow(x, y, _screen.ReadTimer(before) ?? 0, FindersFortuneScreen.IsTimerRed(before)))
        {
            return $"TOO_LATE \"{item}\" at {x},{y} is over a Proceed button and the timer is low; nothing tapped";
        }

        _adb.Tap(new[] { (x, y) });
        if (!WaitForAccept(before, slot))
        {
            return $"MISS \"{item}\" at {x},{y}: the label did not change; file not updated";
        }

        // The tap the game accepted: the centre of the item's picture can fall outside its hit area.
        _catalog.Upsert(map, item, x, y, note);
        _catalog.Save(_catalogPath);
        return $"DONE learned {map} | {item} | {x},{y}";
    }

    private int FindSlot(Mat shot, string item)
    {
        if (FindersFortuneScreen.Classify(shot) != FindersFortuneScreenKind.Stage)
        {
            return -1;
        }

        var label = _screen.ReadLabels(shot).FirstOrDefault(l => !l.Struck && FindersFortuneCatalog.IsCloseMatch(item, l.Text));
        return label?.Slot ?? -1;
    }

    private bool WaitForAccept(Mat before, int slot)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < SettleMilliseconds)
        {
            using var shot = _adb.Screenshot();
            if (FindersFortuneScreen.Classify(shot) != FindersFortuneScreenKind.Stage
                || FindersFortuneScreen.IsStruck(shot, slot)
                || FindersFortuneScreen.CountLabelChange(before, shot, slot) > ChangedPixels)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Plays stages until something needs a decision. All state is re-read from the screen.</summary>
internal sealed class Player
{
    private const int MaxTapsPerSpot = 2;
    private const int LowSeconds = 3;
    private const int MaxUnreadableTimers = 40;
    private const int FlashFrames = 5;
    private const int FlashWaitMilliseconds = 20000;
    private const int MaxFlashTries = 4;
    private const int SettleMilliseconds = 1500;
    private const int ChangedPixels = 60;
    private const int PollMilliseconds = 150;
    private const int ScreenWaitMilliseconds = 20000;
    private static readonly (int X, int Y) IntroTap = (360, 900);
    private static readonly (int X, int Y) ContinueTap = (360, 920);
    private static readonly (int X, int Y) StartGameTap = (486, 1190);

    private readonly Adb _adb;
    private readonly FindersFortuneScreen _screen;
    private readonly FindersFortuneCatalog _catalog;
    private readonly string _catalogPath;
    private readonly MapRecognizer _maps;
    private readonly Options _options;
    private readonly string _shotsDirectory;
    private readonly Dictionary<string, int> _taps = new();
    private readonly Stopwatch _sinceKnownScreen = Stopwatch.StartNew();
    private string? _map;
    private string? _unknownSeen;
    private int _stagesStarted;
    private int _unreadableTimers;
    private int _shotNumber;

    public Player(Adb adb, FindersFortuneScreen screen, FindersFortuneCatalog catalog, string catalogPath, MapRecognizer maps, Options options)
    {
        _adb = adb;
        _screen = screen;
        _catalog = catalog;
        _catalogPath = catalogPath;
        _maps = maps;
        _options = options;
        _map = options.Map;
        _shotsDirectory = Path.Combine(AppContext.BaseDirectory, "shots", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_shotsDirectory);
    }

    public string Run()
    {
        while (true)
        {
            using var shot = _adb.Screenshot();
            var kind = FindersFortuneScreen.Classify(shot);
            if (kind != FindersFortuneScreenKind.Other)
            {
                _sinceKnownScreen.Restart();
            }

            var status = Handle(kind, shot);
            if (status != null)
            {
                return status;
            }
        }
    }

    private string? Handle(FindersFortuneScreenKind kind, Mat shot)
    {
        switch (kind)
        {
            case FindersFortuneScreenKind.Stage:
                return PlayRound(shot);
            case FindersFortuneScreenKind.Intro:
                _taps.Clear();
                TapAndWait(IntroTap);
                return null;
            case FindersFortuneScreenKind.Rewards:
                TapAndWait(ContinueTap);
                return null;
            case FindersFortuneScreenKind.MapReady:
                return StartNextStage(shot);
            default:
                return WaitForKnownScreen(shot);
        }
    }

    private string? WaitForKnownScreen(Mat shot)
    {
        if (_sinceKnownScreen.ElapsedMilliseconds < ScreenWaitMilliseconds)
        {
            Thread.Sleep(PollMilliseconds);
            return null;
        }

        // "Proceed?", chapter clear, a popup: nothing here is safe to tap blindly.
        return $"STOPPED_ON_OTHER_SCREEN map={_map ?? "?"} shot={Save(shot, "other")}";
    }

    private string? StartNextStage(Mat shot)
    {
        if (_stagesStarted >= _options.Stages)
        {
            return $"DONE started {_stagesStarted} stage(s); on the map";
        }

        if (FindersFortuneScreen.IsOutOfBones(shot))
        {
            return "OUT_OF_BONES";
        }

        _stagesStarted++;
        _adb.Tap(new[] { StartGameTap });
        return WaitToLeaveMap();
    }

    /// <summary>A tap during the walking animation is swallowed: no stage starts and no bones are spent.</summary>
    private string? WaitToLeaveMap()
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 2500)
        {
            using var shot = _adb.Screenshot();
            if (FindersFortuneScreen.Classify(shot) != FindersFortuneScreenKind.MapReady)
            {
                return null;
            }

            Thread.Sleep(PollMilliseconds);
        }

        _stagesStarted--;
        return null;
    }

    private void TapAndWait((int X, int Y) point)
    {
        _adb.Tap(new[] { point });
        Thread.Sleep(600);
    }

    private string? PlayRound(Mat shot)
    {
        var active = _screen.ReadLabels(shot).Where(l => !l.Struck).ToList();
        if (active.Count == 0)
        {
            Thread.Sleep(PollMilliseconds);
            return null;
        }

        var seconds = _screen.ReadTimer(shot);
        if (seconds is null or <= LowSeconds)
        {
            // No taps this late or unsure: they could land on the "Proceed?" buttons.
            return ++_unreadableTimers < MaxUnreadableTimers ? null : $"TIMER_UNREADABLE shot={Save(shot, "timer")}";
        }

        _unreadableTimers = 0;

        _map ??= _maps.Recognize(shot, active);
        if (_map is null)
        {
            return $"MAP_UNKNOWN names={Quote(active)} shot={Save(shot, "map")}";
        }

        return PlayLabels(shot, active, seconds.Value);
    }

    private string? PlayLabels(Mat shot, IReadOnlyList<FindersFortuneLabel> active, int seconds)
    {
        var known = active.Select(l => (Label: l, Spot: _catalog.Match(_map!, l.Text))).Where(p => p.Spot != null).ToList();
        var missed = known.FirstOrDefault(p => TapCount(p.Label, p.Spot!) >= MaxTapsPerSpot);
        if (missed.Spot != null)
        {
            return $"MISS map={_map} item=\"{missed.Spot.Item}\" at={missed.Spot.X},{missed.Spot.Y} timer={seconds} shot={Save(shot, "miss")}";
        }

        if (known.Count > 0)
        {
            var targets = known.Select(p => (p.Label, Spot: p.Spot!)).ToList();
            var batch = FindersFortuneTapPlan.NextBatch(targets, t => (t.Spot.X, t.Spot.Y), seconds, FindersFortuneScreen.IsTimerRed(shot));
            TapAndSettle(shot, batch);
            _unknownSeen = null;
            return null;
        }

        return ConfirmUnknown(shot, active, seconds);
    }

    /// <summary>A label caught mid-animation reads as garbage, so only trust an unknown name seen twice.</summary>
    private string? ConfirmUnknown(Mat shot, IReadOnlyList<FindersFortuneLabel> active, int seconds)
    {
        var names = Quote(active);
        if (_unknownSeen != names)
        {
            _unknownSeen = names;
            Thread.Sleep(300);
            return null;
        }

        if (!_options.NoFlash && TryLearnFromFlash(active))
        {
            _unknownSeen = null;
            return null;
        }

        return $"UNKNOWN map={_map} names={names} timer={seconds} shot={Save(shot, "unknown")}";
    }

    /// <summary>Watches for the game's own hint on the wanted item. True when the main loop should look again.</summary>
    private bool TryLearnFromFlash(IReadOnlyList<FindersFortuneLabel> cleanLabels)
    {
        var window = new List<Mat>();
        var ignored = new List<Rect>();
        var timer = Stopwatch.StartNew();
        try
        {
            while (timer.ElapsedMilliseconds < FlashWaitMilliseconds && ignored.Count < MaxFlashTries)
            {
                using var shot = _adb.Screenshot();
                if (FindersFortuneScreen.Classify(shot) != FindersFortuneScreenKind.Stage)
                {
                    return true;
                }

                window.Add(FindersFortuneFlash.ToSceneGray(shot));
                if (window.Count > FlashFrames)
                {
                    window[0].Dispose();
                    window.RemoveAt(0);
                }

                var flash = window.Count == FlashFrames ? FindersFortuneFlash.Find(window, ignored) : null;
                if (flash != null)
                {
                    if (TapFlash(shot, flash, cleanLabels))
                    {
                        return true;
                    }

                    ignored.Add(flash.Area);
                    window.ForEach(f => f.Dispose());
                    window.Clear();
                }
            }

            return false;
        }
        finally
        {
            window.ForEach(f => f.Dispose());
        }
    }

    /// <summary>Taps the flash and records it under whichever label the game then crosses off.</summary>
    private bool TapFlash(Mat shot, FindersFortuneFlashSpot flash, IReadOnlyList<FindersFortuneLabel> cleanLabels)
    {
        var seconds = _screen.ReadTimer(shot);
        if (seconds is null or <= LowSeconds || !FindersFortuneTapPlan.IsSafeNow(flash.X, flash.Y, seconds.Value, FindersFortuneScreen.IsTimerRed(shot)))
        {
            return false;
        }

        // Names read before the hint: the label sparkles during it, which garbles OCR.
        var labels = cleanLabels.Where(l => !FindersFortuneScreen.IsStruck(shot, l.Slot)).ToList();
        Save(shot, "flash");
        _adb.Tap(new[] { (flash.X, flash.Y) });
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < SettleMilliseconds)
        {
            using var after = _adb.Screenshot();
            if (FindersFortuneScreen.Classify(after) != FindersFortuneScreenKind.Stage)
            {
                RecordFlash(labels.Count == 1 ? labels[0] : null, flash);
                return true;
            }

            var hit = FindCrossedOff(shot, after, labels);
            if (hit != null)
            {
                RecordFlash(hit, flash);
                return true;
            }

            Thread.Sleep(PollMilliseconds);
        }

        return false;
    }

    /// <summary>Only a crossed-off label counts: the hint's sparkle also changes a label's pixels.</summary>
    private static FindersFortuneLabel? FindCrossedOff(Mat before, Mat after, IReadOnlyList<FindersFortuneLabel> labels)
    {
        var struck = labels.FirstOrDefault(l => FindersFortuneScreen.IsStruck(after, l.Slot));
        if (struck != null)
        {
            return struck;
        }

        // The last of three: the whole bar is replaced, so the slots that were crossed off turn active.
        var barReplaced = Enumerable.Range(0, FindersFortuneScreen.SlotCount)
            .Any(s => FindersFortuneScreen.IsStruck(before, s) && !FindersFortuneScreen.IsStruck(after, s));
        return barReplaced && labels.Count == 1 ? labels[0] : null;
    }

    private void RecordFlash(FindersFortuneLabel? label, FindersFortuneFlashSpot flash)
    {
        if (label is null || label.Text.Length == 0)
        {
            return;
        }

        _catalog.Upsert(_map!, label.Text, flash.X, flash.Y, "auto: tapped the game's flash");
        _catalog.Save(_catalogPath);
        Console.WriteLine($"FLASH learned {_map} | {label.Text} | {flash.X},{flash.Y}");
    }

    private void TapAndSettle(Mat before, IReadOnlyList<(FindersFortuneLabel Label, ItemSpot Spot)> targets)
    {
        if (targets.Count == 0)
        {
            Thread.Sleep(PollMilliseconds); // Only dangerous spots left and too little time: send nothing.
            return;
        }

        foreach (var (label, spot) in targets)
        {
            _taps[TapKey(label, spot)] = TapCount(label, spot) + 1;
        }

        _adb.Tap(targets.Select(t => (t.Spot.X, t.Spot.Y)));
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < SettleMilliseconds)
        {
            using var shot = _adb.Screenshot();
            if (IsSettled(before, shot, targets))
            {
                Save(shot, "round");
                // Only now is the map proven: a wrong --map must not be saved as this scene's picture.
                _maps.Remember(_map!, before);
                return;
            }

            Thread.Sleep(PollMilliseconds);
        }
    }

    private static bool IsSettled(Mat before, Mat shot, IEnumerable<(FindersFortuneLabel Label, ItemSpot Spot)> targets) =>
        FindersFortuneScreen.Classify(shot) != FindersFortuneScreenKind.Stage
        || targets.All(t => FindersFortuneScreen.IsStruck(shot, t.Label.Slot)
            || FindersFortuneScreen.CountLabelChange(before, shot, t.Label.Slot) > ChangedPixels);

    private int TapCount(FindersFortuneLabel label, ItemSpot spot) => _taps.GetValueOrDefault(TapKey(label, spot));

    private static string TapKey(FindersFortuneLabel label, ItemSpot spot) => $"{label.Slot}:{spot.Item}";

    private static string Quote(IEnumerable<FindersFortuneLabel> labels) => string.Join(",", labels.Select(l => $"\"{l.Text}\""));

    private string Save(Mat shot, string name)
    {
        var path = Path.Combine(_shotsDirectory, $"{++_shotNumber:D3}-{name}.png");
        Cv2.ImWrite(path, shot, new ImageEncodingParam(ImwriteFlags.PngCompression, 1));
        return path;
    }
}
