using Bot.Core.Models;
using Bot.Core.Logging;
using Bot.Core.Utils;
using Bot.Core.ImageDetection;
using Bot.Core.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;

namespace Bot.Core.Tasks.Modules
{
    /// <summary>
    /// Suggestion Box > SOS: answers every resident letter. Each letter comes from a fixed list and has one correct
    /// answer (Public Opinion +3% or 0%, a wrong one -2%; both pay the gem reward). Answers are looked up by the letter text;
    /// an unknown letter gets a guess, and the result decides which answer is stored for next time.
    /// </summary>
    public class SuggestionBoxTask : BaseTaskWithCommonPatterns, IScheduledTask
    {
        // The day's letters come with the 00:00 UTC reset: once the box was opened and answered, skip until then.
        // "No letters waiting" sets no gate (the envelope check is quick, and letters may still come)
        public DateTime? GetNextRunUtc(string accountKey, AccountSettings account) => DailyGate.NextRunUtc(TaskType, accountKey);
        public void ClearSchedule(string accountKey) => DailyGate.Clear(TaskType, accountKey);

        public override TaskType TaskType => TaskType.SuggestionBox;
        public override string Name => "Suggestion Box";

        private LocatorService? _locatorService;

        private const string EnvelopeTemplate = "envelope.png";         // envelope bubble above the town center
        private const string TitleTemplate = "title.png";               // "Suggestion Box" title
        private const string QuestionTemplate = "sos-question.png";     // red "?" of an unanswered SOS row
        private const string AnswerButtonTemplate = "answer-button.png"; // left end of an answer button: a letter is open
        private const string ConfirmTemplate = "confirm.png";
        private static readonly Rectangle EnvelopeArea = new Rectangle(150, 350, 500, 500);
        private static readonly Rectangle TitleArea = new Rectangle(150, 80, 420, 80);
        private static readonly Rectangle FirstRowIconArea = new Rectangle(40, 600, 200, 120);
        private static readonly Rectangle AnswerButtonArea = new Rectangle(60, 800, 140, 140);
        private static readonly Rectangle ConfirmArea = new Rectangle(150, 1060, 420, 140);
        private static readonly Rectangle LetterTextArea = new Rectangle(90, 260, 540, 380);
        private static readonly Rectangle[] AnswerAreas = { new Rectangle(100, 830, 510, 70), new Rectangle(100, 922, 510, 72) };
        private static readonly Rectangle OpinionArea = new Rectangle(200, 840, 320, 44);   // "Public Opinion: +3%"
        private static readonly Point SosTabPoint = new Point(520, 532);
        private static readonly Point FirstRowPoint = new Point(400, 660);
        private static readonly Point ConfirmPoint = new Point(357, 1138);
        private static readonly Point BackArrowPoint = new Point(45, 40);
        private const double EnvelopeThreshold = 0.65;  // 0.75-1.00 present (the bubble wobbles), <= 0.42 elsewhere
        private const double Threshold = 0.80;          // title, "?" row, answer button, Confirm: 1.00 present, <= 0.49 elsewhere
        private const double LetterMatch = 0.6;         // word overlap of a known letter with the OCR'd one
        private const int MaxLetters = 100;     // a backlog of months fills the list

        private static readonly string AnswerFile = Path.Combine(AppContext.BaseDirectory, "data", "suggestion_box_answers.json");
        private static readonly object _fileLock = new();

        public class AnswerEntry
        {
            public string Letter { get; set; } = "";
            public string Answer { get; set; } = "";
            // the answer scored 0% and the other one was never tried: it may be the +3% one
            public bool TryOther { get; set; }
        }

        // Answered by hand; learned answers are added to data/suggestion_box_answers.json
        private static readonly AnswerEntry[] KnownAnswers =
        {
            new() { Letter = "Caught some workers sneaking grain to the black market, messing with our kitchen supplies. You can't let them get away with it.",
                    Answer = "These opportunists must face severe punishment!" },
            new() { Letter = "My roommate has been in tears every night because he can't let go of his thoughts about his family. He really wants to look for them but is afraid to leave the safety of the City.",
                    Answer = "I'm personally backing him with three silvers." },
            new() { Letter = "Recently there have been frequent fights and brawls in front of my home. These barbaric acts have happened multiple times in the City and affect not only me, but also my children, who live in fear.",
                    Answer = "I will send someone to handle it." },
            new() { Letter = "No offense, our chef might cook well, but his swearing in the kitchen is unbearable, far worse than any chatter you'd hear in the market.",
                    Answer = "That's quite a unique trait, I'd say." },
            new() { Letter = "Many people around me have been receiving anonymous blackmail letters, which often contain detailed information about them and their dark history.",
                    Answer = "I'll expose them." },
            // 0%; "It's dangerous out there, don't throw your life away." is -2%
            new() { Letter = "My roommate and I want to join an adventure. Dreamt of exploring the wilds forever. We need your permission... Will you grant our wish?",
                    Answer = "If you're set on risking it, I won't stop you." },
            new() { Letter = "My neighbors won't stop having petty arguments and it's driving me mad. They're going to sort out their problems with their fists.",
                    Answer = "I'll handle it." },
            new() { Letter = "A woman accused her roommate of stealing her jewelry, but her roommate won't admit to doing it. Now, when they meet, they do nothing but quarrel.",
                    Answer = "I'll go find out what's going on." },
            new() { Letter = "Saw a creature with human legs by the graveyard last night. It's no ordinary beast, for certain! Catch it before it hurts someone...",
                    Answer = "I'll send someone to check it out right now." },
            new() { Letter = "Some folks found a tomb full of treasures, and now everyone's green with envy... Hope it doesn't mess up our harmony.",
                    Answer = "I'm seizing these assets." },
            new() { Letter = "Ever heard of the Cesares? Those folks standing up to the Sect of Suffering. They're recruiting. Shall we join them?",
                    Answer = "Don't worry, I've got your back." },
            new() { Letter = "Today, I suddenly realized that these children have reached the age for schooling. But they don't have a qualified teacher yet.",
                    Answer = "I'll try to find a good teacher." },
            new() { Letter = "The exiles are stirring near our Town, might be gearing up to raid us. Better beef up our defenses, or we'll be in a real bind!",
                    Answer = "I'll teach them a lesson!" },
            new() { Letter = "Some seafarers told tales of a distant Town called Song, where the celebration never stops. Everyone's dreaming about it now.",
                    Answer = "Huh? I'd love to hear those stories too." },
            new() { Letter = "Heard about Robin Hood of the Unjust League, robs from the rich to help the poor. Maybe we could reach out to them.",
                    Answer = "I'll try reaching out to them." },
            // "Surely they won't actually come to blows." is -2%
            new() { Letter = "Ever since those ex-mercenaries moved in, the atmosphere in the City has become terribly tense. These people are lawless, cause trouble all day long, and refuse to take their work seriously!",
                    Answer = "I'll remind them to watch their behavior." },
            new() { Letter = "The spirit's low around here. Work's hard, sure, but we need to keep at it to eat. Maybe you could give a pep talk?",
                    Answer = "I'll propose a day-off bill." },
            new() { Letter = "This is my fifth letter. Ron stole my belongings while I was away. You must punish him, and right this wrong.",
                    Answer = "The wrongdoer must be punished harshly." },
            // "Hmm? Be glad there's food at all." is 0%
            new() { Letter = "The other day, several residents showed signs of food poisoning. They all said they felt dizzy and vomited right after eating at the Kitchen.",
                    Answer = "I'll take back control of the Kitchen." },
            // "Best not wander out at night." is 0%
            new() { Letter = "There's someone creepy peeking around my door at night, messing up my workday.",
                    Answer = "I'll have extra patrols out tonight." },
        };

        protected override async Task<TaskExecutionDetails> ExecuteTaskLogicAsync(AccountSettings account, LogService logger, CancellationToken cancellationToken, bool isReRun = false, IUserNotificationService? userNotifications = null)
        {
            try
            {
                string accountKey = Schedule.AccountKey(AccountDetectionTask.GetCachedAccountId(account.InstanceNumber), account);
                if (DailyGate.DoneToday(TaskType, accountKey))
                    return TaskExecutionDetails.SkippedBecause("Already done today");

                _locatorService = new LocatorService(logger, account, TaskType);
                int instance = account.InstanceNumber;
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                // The envelope only shows while letters wait; it wobbles, so look at a few frames
                (bool found, Rectangle rect, double confidence) envelope = default;
                for (int i = 0; i < 3 && !envelope.found; i++)
                {
                    envelope = await MatchAsync(EnvelopeTemplate, EnvelopeArea, EnvelopeThreshold, instance, logger);
                    if (!envelope.found) await Task.Delay(700, cancellationToken);
                }
                if (!envelope.found)
                    return TaskExecutionDetails.Succeeded("No letters waiting");

                await ClickCenterInRectAsync(instance, logger, envelope.rect);
                await Task.Delay(2500, cancellationToken);
                if (!(await MatchAsync(TitleTemplate, TitleArea, Threshold, instance, logger)).found)
                {
                    logger.LogWarning($"[{account.AccountName}] Suggestion Box did not open");
                    await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);
                    return TaskExecutionDetails.Failed("Suggestion Box did not open");
                }
                // The box reopens on the last tab used
                await ClickAsync(instance, logger, SosTabPoint);
                await Task.Delay(2000, cancellationToken);

                var answers = LoadAnswers(logger);
                int answered = 0, right = 0, wrong = 0;
                while (answered < MaxLetters)
                {
                    await WaitIfPausedAsync(cancellationToken);
                    // Answered rows disappear, so the next letter is always in the first row
                    if (!(await MatchAsync(QuestionTemplate, FirstRowIconArea, Threshold, instance, logger)).found) break;

                    await ClickAsync(instance, logger, FirstRowPoint);
                    await Task.Delay(2500, cancellationToken);
                    if (!(await MatchAsync(AnswerButtonTemplate, AnswerButtonArea, Threshold, instance, logger)).found)
                    {
                        logger.LogWarning($"[{account.AccountName}] Suggestion Box: letter did not open");
                        break;
                    }

                    var shot = await TakeScreenshotAsync(instance, logger);
                    if (shot == null) break;
                    string letter = ReadText(shot, LetterTextArea, white: false, logger);
                    string[] options = AnswerAreas.Select(a => ReadText(shot, a, white: true, logger)).ToArray();

                    int pick = PickAnswer(answers, letter, options, out AnswerEntry? entry);
                    bool known = entry != null;
                    bool tryingOther = entry?.TryOther == true;
                    if (tryingOther) pick = 1 - pick;
                    if (!known)
                        logger.LogWarning($"[{account.AccountName}] Suggestion Box: NEW LETTER '{letter}', answers '{options[0]}' / '{options[1]}', guessing the first");
                    else
                        logger.LogInfo($"[{account.AccountName}] Suggestion Box: known letter, answering '{options[pick]}' (other: '{options[1 - pick]}'){(tryingOther ? ", trying the answer that was not tried yet" : "")}");
                    await ClickCenterInRectAsync(instance, logger, AnswerAreas[pick]);
                    await Task.Delay(2500, cancellationToken);

                    var result = await TakeScreenshotAsync(instance, logger);
                    string opinion = result == null ? "" : ReadText(result, OpinionArea, white: true, logger);
                    // "Public Opinion: +3%" for the right answer, "-2%" for the wrong one, "0%" for a neutral one
                    // (some letters have no +3% answer)
                    int? score = opinion.Contains('-') ? -1 : opinion.Contains('+') ? 1 : opinion.Contains('%') ? 0 : null;
                    if (score > 0) right++;
                    if (score < 0) wrong++;
                    logger.LogInfo($"[{account.AccountName}] Suggestion Box: result '{opinion}' ({(score == null ? "unread" : score > 0 ? "right" : score < 0 ? "wrong" : "neutral")})");

                    AnswerEntry? learned = null;
                    if (score != null && !known && Words(letter).Count > 0)
                        learned = new AnswerEntry { Letter = letter, Answer = options[score < 0 ? 1 - pick : pick], TryOther = score == 0 };
                    else if (score != null && tryingOther)
                        // the other answer was tried after a 0%: keep it only when it did better
                        learned = new AnswerEntry { Letter = entry!.Letter, Answer = score > 0 ? options[pick] : entry.Answer };
                    else if (score < 0 && known)
                        learned = new AnswerEntry { Letter = entry!.Letter, Answer = options[1 - pick] };

                    if (learned != null && Words(learned.Answer).Count > 0)
                    {
                        // learned entries come after the built-in ones and win a tie
                        if (entry != null) answers.Remove(entry);
                        answers.Add(learned);
                        logger.LogWarning($"[{account.AccountName}] Suggestion Box: {(known ? "stored answer is now" : "learned")} '{learned.Answer}'{(learned.TryOther ? " (0%, the other answer is tried next time)" : "")}");
                        SaveLearned(answers.Except(KnownAnswers).ToList(), logger);
                    }

                    var confirm = await MatchAsync(ConfirmTemplate, ConfirmArea, Threshold, instance, logger);
                    if (confirm.found) await ClickCenterInRectAsync(instance, logger, confirm.rect);
                    else await ClickAsync(instance, logger, ConfirmPoint);
                    await Task.Delay(2500, cancellationToken);
                    answered++;
                }

                await ClickAsync(instance, logger, BackArrowPoint);
                await Task.Delay(2000, cancellationToken);
                await _locatorService.EnsureViewAsync(ViewType.BaseView, instance, cancellationToken);

                string summary = $"{answered} letter(s) answered, {right} right, {wrong} wrong";
                logger.LogInfo($"[{account.AccountName}] Suggestion Box done: {summary}");
                DailyGate.MarkDone(TaskType, accountKey);
                return TaskExecutionDetails.Succeeded(summary);
            }
            catch (OperationCanceledException)
            {
                return TaskExecutionDetails.Failed("Task was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError($"[{account.AccountName}] Error in Suggestion Box: {ex.Message}");
                return TaskExecutionDetails.Failed(ex.Message);
            }
        }

        /// <summary>
        /// Index of the answer to tap: the option closest to the stored answer of the best matching letter, else the first.
        /// </summary>
        private static int PickAnswer(List<AnswerEntry> answers, string letter, string[] options, out AnswerEntry? entry)
        {
            var letterWords = Words(letter);
            // of the matching entries the last one wins: learned entries come after the built-in ones,
            // so a corrected answer replaces a built-in one
            entry = letterWords.Count < 5 ? null : answers.LastOrDefault(e => Overlap(Words(e.Letter), letterWords) >= LetterMatch);
            if (entry == null) return 0;
            var answerWords = Words(entry.Answer);
            return Overlap(answerWords, Words(options[1])) > Overlap(answerWords, Words(options[0])) ? 1 : 0;
        }

        /// <summary>
        /// Share of the smaller word set found in the other one, so a letter read only in part still matches.
        /// </summary>
        private static double Overlap(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0;
            return (double)a.Intersect(b).Count() / Math.Min(a.Count, b.Count);
        }

        private static HashSet<string> Words(string text) =>
            Regex.Matches(text.ToLowerInvariant().Replace('’', '\''), "[a-z']{4,}").Select(m => m.Value.Trim('\'')).Where(w => w.Length >= 4).ToHashSet();

        /// <summary>
        /// OCR of white (answers, opinion) or dark (letter) text: the text colour is masked into white-on-black first.
        /// </summary>
        private static string ReadText(byte[] screenshot, Rectangle area, bool white, LogService logger)
        {
            byte[] masked;
            using (var image = OpenCvSharp.Cv2.ImDecode(screenshot, OpenCvSharp.ImreadModes.Color))
            {
                var roi = new OpenCvSharp.Rect(area.X, area.Y, area.Width, area.Height).Intersect(new OpenCvSharp.Rect(0, 0, image.Width, image.Height));
                using var region = new OpenCvSharp.Mat(image, roi);
                using var hsv = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.CvtColor(region, hsv, OpenCvSharp.ColorConversionCodes.BGR2HSV);
                using var mask = new OpenCvSharp.Mat();
                if (white) OpenCvSharp.Cv2.InRange(hsv, new OpenCvSharp.Scalar(0, 0, 210), new OpenCvSharp.Scalar(180, 60, 255), mask);
                else OpenCvSharp.Cv2.InRange(hsv, new OpenCvSharp.Scalar(0, 0, 0), new OpenCvSharp.Scalar(180, 255, 120), mask);
                using var bgr = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.CvtColor(mask, bgr, OpenCvSharp.ColorConversionCodes.GRAY2BGR);
                masked = bgr.ToBytes(".png");
            }
            var ocrConfig = new OCRConfiguration { ScaleFactor = white ? 1.5f : 2, PageSegMode = Tesseract.PageSegMode.SingleBlock };
            using var ocr = new OCRService(logger, ocrConfig);
            string text = ocr.ExtractTextFromScreenArea(masked, new Rectangle(0, 0, area.Width, area.Height)) ?? "";
            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        private static List<AnswerEntry> LoadAnswers(LogService logger)
        {
            var answers = KnownAnswers.ToList();
            lock (_fileLock)
            {
                try
                {
                    if (File.Exists(AnswerFile))
                        answers.AddRange(JsonSerializer.Deserialize<List<AnswerEntry>>(File.ReadAllText(AnswerFile)) ?? new());
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Suggestion Box: could not read learned answers, using the built-in table only: {ex.Message}");
                }
            }
            return answers;
        }

        private static void SaveLearned(List<AnswerEntry> learned, LogService logger)
        {
            lock (_fileLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(AnswerFile)!);
                    File.WriteAllText(AnswerFile, JsonSerializer.Serialize(learned, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Suggestion Box: could not save answers: {ex.Message}");
                }
            }
        }

        private async Task<(bool found, Rectangle rect, double confidence)> MatchAsync(
            string templateName, Rectangle area, double threshold, int instance, LogService logger)
        {
            var screenshot = await TakeScreenshotAsync(instance, logger);
            if (screenshot == null) return (false, Rectangle.Empty, 0);

            if (_templateMatcher == null) _templateMatcher = new UnifiedTemplateMatchingService(logger);
            var path = Path.Combine(ImageTemplateFolder, templateName);
            var result = _templateMatcher.MatchTemplate(
                screenshot, path, instance,
                threshold: threshold,
                scales: new[] { 1.0 },
                verboseLogging: false,
                searchArea: area);
            return (result.found, result.matchRect, result.confidence);
        }
    }
}
