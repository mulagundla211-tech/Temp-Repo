using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OnlineExamPlatform.Web.Data;
using OnlineExamPlatform.Web.Models;

namespace OnlineExamPlatform.Web.Services;

public class GradingService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<GradingService> _logger;
    private readonly IMemoryCache? _cache;

    // How long a cached exam grading graph stays valid. Invalidation is structural,
    // not scheduled: GradingGraphCacheInvalidator (a SaveChangesInterceptor) evicts the
    // entry whenever any Question/QuestionOption/AnswerKey row for the exam is written,
    // from any code path. This expiry is only a backstop for changes made outside EF
    // (raw SQL, direct DB edits) and for bounding memory on long-lived exams.
    //
    // NOTE: IMemoryCache is per-process, so this is safe for a single app instance only
    // — see GradingGraphCacheInvalidator for the scale-out constraint.
    private static readonly TimeSpan GradingGraphCacheDuration = TimeSpan.FromMinutes(10);

    // The IMemoryCache is optional so unit tests can construct the service with just
    // a context + logger; when absent, grading always reads straight from the database.
    public GradingService(ApplicationDbContext context, ILogger<GradingService> logger, IMemoryCache? cache = null)
    {
        _context = context;
        _logger = logger;
        _cache = cache;
    }

    public async Task GradeAndSubmitAsync(Guid attemptId, bool timedOut = false)
    {
        var attempt = await _context.ExamAttempts
            .Include(a => a.StudentAnswers)
            .FirstOrDefaultAsync(a => a.Id == attemptId);

        // Only an in-progress attempt can be graded — prevents re-grading a
        // submitted attempt (e.g. via a replayed or forged Submit request).
        if (attempt == null || attempt.Status != AttemptStatus.InProgress)
        {
            _logger.LogDebug(
                "Grade skipped for attempt {AttemptId}: {Reason}",
                attemptId, attempt == null ? "not found" : $"status {attempt.Status}");
            return;
        }

        // Grade against the exam's full question list, not just the saved answer
        // rows — a question the student never opened has no StudentAnswer row but
        // must still receive the unattempted penalty. The graph is static once the
        // exam is live, so it's cached per exam to spare the DB during the
        // synchronized end-of-exam submit burst (same rows re-read once per student).
        var questions = await GetGradingGraphAsync(attempt.ExamId);

        // If autosave races ever produced duplicate rows for a question, score it once.
        var answersByQuestion = attempt.StudentAnswers
            .GroupBy(sa => sa.QuestionId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(sa => sa.AnsweredAt).First());

        decimal totalScore = 0;

        foreach (var question in questions)
        {
            answersByQuestion.TryGetValue(question.Id, out var answer);

            if (question.QuestionType == QuestionType.Subjective)
            {
                if (answer != null)
                {
                    answer.IsCorrect = null;
                    answer.MarksObtained = null;
                }
                continue;
            }

            bool attempted = IsAttempted(question, answer);

            if (!attempted)
            {
                var penalty = UnattemptedPenaltyFor(question);
                if (answer != null)
                {
                    answer.IsCorrect = false;
                    answer.MarksObtained = penalty;
                }
                totalScore += penalty;
                continue;
            }

            bool isCorrect = question.QuestionType switch
            {
                QuestionType.MCQ =>
                    question.Options.FirstOrDefault(o => o.Id == answer!.SelectedOptionId)?.IsCorrect ?? false,
                QuestionType.MultiMCQ =>
                    CheckMultiMCQCorrectness(question, answer!.AnswerText),
                QuestionType.Numerical =>
                    question.AnswerKey != null
                    && IsNumericallyEqual(answer!.AnswerText!, question.AnswerKey.CorrectAnswer),
                // TrueFalse renders as radio options. The client normally posts the
                // literal "True"/"False" as AnswerText, but because the option/text
                // split is decided client-side, an option id can arrive instead — in
                // which case score from that option's IsCorrect flag rather than
                // dereferencing a null AnswerText.
                QuestionType.TrueFalse =>
                    !string.IsNullOrWhiteSpace(answer!.AnswerText)
                        ? MatchesAnswerKey(question, answer.AnswerText)
                        : question.Options.FirstOrDefault(o => o.Id == answer.SelectedOptionId)?.IsCorrect ?? false,
                _ =>
                    MatchesAnswerKey(question, answer!.AnswerText),
            };

            answer!.IsCorrect = isCorrect;
            answer.MarksObtained = isCorrect
                ? question.Marks
                : (question.HasNegativeMarking ? -question.NegativeMarks : 0m);
            totalScore += answer.MarksObtained.Value;
        }

        attempt.TotalScore = totalScore;
        attempt.Status = timedOut ? AttemptStatus.TimedOut : AttemptStatus.Submitted;
        attempt.FinishedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Graded attempt {AttemptId} (student {StudentId}, exam {ExamId}): score {Score}, status {Status}",
            attempt.Id, attempt.StudentId, attempt.ExamId, totalScore, attempt.Status);
    }

    // Penalty applied when an auto-graded question is left unattempted.
    //
    // The rule is deliberately uniform across every auto-graded question type
    // (MCQ, MultiMCQ, TrueFalse, FillInTheBlank, Numerical): leaving a question
    // blank is only penalised when the author explicitly opted in on that
    // question via HasNegativeMarksIfUnattempted. It is NOT a type-driven rule.
    //
    // In particular, a blank Numerical or FillInTheBlank answer scores 0 (not
    // negative) unless the author set the flag — so the common expectation that
    // "blank is neutral" holds by default, and a negative unattempted penalty is
    // always a conscious per-question choice made in the question editor.
    //
    // Subjective questions never reach here: they are skipped earlier and left
    // ungraded (IsCorrect/MarksObtained = null) for a human grader, so they can
    // never incur an unattempted penalty.
    private static decimal UnattemptedPenaltyFor(Question question)
        => question.HasNegativeMarksIfUnattempted
            ? -question.NegativeMarksIfUnattempted
            : 0m;

    // Loads the exam's questions + options + answer keys used for grading. The
    // result is read-only (AsNoTracking) and immutable while the exam is live, so
    // it's cached per exam under a shared key. GradingGraphCacheInvalidator evicts the
    // entry on any write to those rows; it otherwise expires after
    // GradingGraphCacheDuration. Falls back to a direct query when no cache is
    // configured (e.g. unit tests).
    private async Task<List<Question>> GetGradingGraphAsync(Guid examId)
    {
        if (_cache == null)
            return await QueryGradingGraphAsync(examId);

        return (await _cache.GetOrCreateAsync(ExamService.GradingGraphCacheKey(examId), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = GradingGraphCacheDuration;
            return QueryGradingGraphAsync(examId);
        }))!;
    }

    private Task<List<Question>> QueryGradingGraphAsync(Guid examId) =>
        _context.Questions
            .AsNoTracking()
            .Include(q => q.Options)
            .Include(q => q.AnswerKey)
            .Where(q => q.ExamId == examId)
            .ToListAsync();

    // Whether the student supplied a response, decided explicitly per question type
    // rather than by an MCQ/else split.
    //
    // Which column carries the response depends on how the type is rendered:
    //   MCQ            -> SelectedOptionId (a single option row)
    //   MultiMCQ       -> AnswerText (comma-separated option ids from checkboxes)
    //   TrueFalse      -> AnswerText ("True"/"False" radio values)
    //   FillInTheBlank -> AnswerText (free text input)
    //   Numerical      -> AnswerText (numeric text input)
    //   Subjective     -> never reaches here; skipped earlier for a human grader
    //
    // The option-vs-text split is decided on the client (exam-timer.js sniffs the
    // radio value for a GUID), so the option-rendered types accept EITHER signal.
    // Without that, a TrueFalse question whose radios ever carried option ids would
    // be scored as unattempted and could take the unattempted penalty despite the
    // student having answered it. The text-only input types accept text alone,
    // because they have no option UI that could produce a SelectedOptionId.
    private static bool IsAttempted(Question question, StudentAnswer? answer)
    {
        if (answer == null) return false;

        bool hasOption = answer.SelectedOptionId != null;
        bool hasText = !string.IsNullOrWhiteSpace(answer.AnswerText);

        return question.QuestionType switch
        {
            QuestionType.MCQ => hasOption || hasText,
            QuestionType.MultiMCQ => hasText || hasOption,
            QuestionType.TrueFalse => hasText || hasOption,
            QuestionType.FillInTheBlank => hasText,
            QuestionType.Numerical => hasText,
            _ => hasText || hasOption
        };
    }

    // Case-insensitive, trimmed comparison against the question's answer key.
    // Null-safe on both sides: a missing key or missing answer is simply "not correct"
    // rather than an exception.
    private static bool MatchesAnswerKey(Question question, string? answerText)
        => question.AnswerKey != null
           && answerText != null
           && string.Equals(
               answerText.Trim(),
               question.AnswerKey.CorrectAnswer.Trim(),
               StringComparison.OrdinalIgnoreCase);

    private static bool CheckMultiMCQCorrectness(Question question, string? answerText)
    {
        if (string.IsNullOrWhiteSpace(answerText)) return false;

        var correctOptionIds = question.Options
            .Where(o => o.IsCorrect)
            .Select(o => o.Id)
            .ToHashSet();

        if (correctOptionIds.Count == 0) return false;

        var studentSelectedIds = answerText
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Guid.TryParse(s.Trim(), out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToHashSet();

        return correctOptionIds.SetEquals(studentSelectedIds);
    }

    // "7.50" must match an answer key of "7.5"; tolerance absorbs rounding to
    // two decimal places. Falls back to text comparison when either side is not
    // a number, so an accidental non-numeric key doesn't mark everyone wrong.
    private static bool IsNumericallyEqual(string studentAnswer, string correctAnswer)
    {
        var style = System.Globalization.NumberStyles.Float;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (decimal.TryParse(studentAnswer.Trim(), style, culture, out var s)
            && decimal.TryParse(correctAnswer.Trim(), style, culture, out var c))
            return Math.Abs(s - c) <= 0.005m;

        return string.Equals(studentAnswer.Trim(), correctAnswer.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }
}
