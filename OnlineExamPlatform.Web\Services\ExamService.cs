using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using OnlineExamPlatform.Web.Data;
using OnlineExamPlatform.Web.Models;
using OnlineExamPlatform.Web.Models.ViewModels;

namespace OnlineExamPlatform.Web.Services;

public class ExamService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<ExamService> _logger;
    private readonly IMemoryCache? _cache;

    // The IMemoryCache is optional so unit tests can construct the service with just
    // a context + logger. When present, it holds the per-exam grading graph that
    // GradingService reads; eviction is handled centrally by GradingGraphCacheInvalidator.
    public ExamService(ApplicationDbContext context, ILogger<ExamService> logger, IMemoryCache? cache = null)
    {
        _context = context;
        _logger = logger;
        _cache = cache;
    }

    // Shared cache key for an exam's grading graph (questions/options/answer keys).
    // Defined here so GradingService (population) and GradingGraphCacheInvalidator
    // (eviction) agree on the exact key.
    public static string GradingGraphCacheKey(Guid examId) => $"grading-graph:{examId}";

    public async Task<List<Exam>> GetAllExamsAsync()
    {
        return await _context.Exams
            .AsNoTracking()
            .Include(e => e.CreatedBy)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync();
    }

    // Full graph (questions, options, answer keys, assigned students) with change
    // tracking ON — used by admin edit/manage paths that mutate the loaded entity.
    // Read-only student paths should use the lighter loaders below instead.
    //
    // Ordering is applied in the query rather than by reassigning the loaded
    // collections: this graph is tracked, so swapping Questions/Options for new
    // List instances would be mutating tracked navigation state purely for display.
    public async Task<Exam?> GetExamByIdAsync(Guid id)
    {
        return await _context.Exams
            .Include(e => e.Questions.OrderBy(q => q.DisplayOrder))
                .ThenInclude(q => q.Options.OrderBy(o => o.DisplayOrder))
            .Include(e => e.Questions.OrderBy(q => q.DisplayOrder))
                .ThenInclude(q => q.AnswerKey)
            .Include(e => e.ExamStudents)
                .ThenInclude(es => es.Student)
            .FirstOrDefaultAsync(e => e.Id == id);
    }

    // Everything the take-exam page needs, projected into DTOs.
    //
    // This is a projection rather than an entity load for two reasons:
    //   1. The result is reordered and shuffled per student. Doing that to a DTO is
    //      safe; doing it to an entity graph only worked because nothing saved
    //      afterwards, and would silently start persisting shuffled DisplayOrders if
    //      the query ever became tracked.
    //   2. QuestionOption.IsCorrect and the AnswerKey are never selected, so the
    //      correct answers are not merely unrendered — they never leave the database.
    public async Task<TakeExamViewModel?> GetTakeExamAsync(Guid id)
    {
        return await _context.Exams
            .AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new TakeExamViewModel
            {
                Id = e.Id,
                Title = e.Title,
                DurationMinutes = e.DurationMinutes,
                StartDate = e.StartDate,
                EndDate = e.EndDate,
                TotalMarks = e.TotalMarks,
                Subjects = e.Subjects,
                ShuffleQuestions = e.ShuffleQuestions,
                ShuffleOptions = e.ShuffleOptions,
                EnableProctoring = e.EnableProctoring,
                RequireFullscreen = e.RequireFullscreen,
                MaxProctoringWarnings = e.MaxProctoringWarnings,
                Questions = e.Questions
                    .OrderBy(q => q.DisplayOrder)
                    .Select(q => new TakeExamQuestion
                    {
                        Id = q.Id,
                        DisplayOrder = q.DisplayOrder,
                        Subject = q.Subject,
                        QuestionType = q.QuestionType,
                        Marks = q.Marks,
                        QuestionText = q.QuestionText,
                        ImagePath = q.ImagePath,
                        Options = q.Options
                            .OrderBy(o => o.DisplayOrder)
                            .Select(o => new TakeExamOption
                            {
                                Id = o.Id,
                                OptionLabel = o.OptionLabel,
                                OptionText = o.OptionText,
                                ImagePath = o.ImagePath,
                                DisplayOrder = o.DisplayOrder
                            }).ToList()
                    }).ToList()
            })
            .FirstOrDefaultAsync();
    }

    // Graph for the post-exam review screen: questions + options + answer keys
    // (needed to show the correct answer/solution), but not the student roster.
    // Read-only, and ordered in the query so the loaded graph is never mutated.
    public async Task<Exam?> GetExamForReviewAsync(Guid id)
    {
        return await _context.Exams
            .AsNoTracking()
            .Include(e => e.Questions.OrderBy(q => q.DisplayOrder))
                .ThenInclude(q => q.Options.OrderBy(o => o.DisplayOrder))
            .Include(e => e.Questions.OrderBy(q => q.DisplayOrder))
                .ThenInclude(q => q.AnswerKey)
            .FirstOrDefaultAsync(e => e.Id == id);
    }

    public async Task<Exam> CreateExamAsync(Exam exam)
    {
        exam.Id = Guid.NewGuid();
        _context.Exams.Add(exam);
        await _context.SaveChangesAsync();
        return exam;
    }

    public async Task UpdateExamAsync(Exam exam)
    {
        exam.UpdatedAt = DateTime.UtcNow;
        _context.Exams.Update(exam);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteExamAsync(Guid id)
    {
        var exam = await _context.Exams.FindAsync(id);
        if (exam != null)
        {
            _context.Exams.Remove(exam);
            await _context.SaveChangesAsync();
        }
    }

    public async Task AssignStudentsToExamAsync(Guid examId, List<Guid> studentIds)
    {
        var existing = await _context.ExamStudents
            .Where(es => es.ExamId == examId)
            .Select(es => es.StudentId)
            .ToListAsync();

        var newAssignments = studentIds.Except(existing)
            .Select(sid => new ExamStudent
            {
                Id = Guid.NewGuid(),
                ExamId = examId,
                StudentId = sid
            });

        _context.ExamStudents.AddRange(newAssignments);
        await _context.SaveChangesAsync();
    }

    // Assigns every exam belonging to a batch to the given students, skipping pairs that
    // already exist.
    //
    // Existing pairs are fetched once and matched in memory rather than issuing an
    // AnyAsync per (exam x student) combination: a bulk upload of 500 students into a
    // batch with 10 exams would otherwise be 5,000 sequential round trips.
    public async Task AssignExamsToNewStudentsAsync(List<Guid> studentIds, Guid batchId)
    {
        if (studentIds.Count == 0) return;

        var batchExams = await _context.Exams
            .Where(e => e.BatchId == batchId)
            .Select(e => e.Id)
            .ToListAsync();

        if (batchExams.Count == 0) return;

        // Scoped to this batch's exams and these students so the result set stays small
        // regardless of how large exam_students grows overall.
        var existingPairs = (await _context.ExamStudents
                .Where(es => batchExams.Contains(es.ExamId) && studentIds.Contains(es.StudentId))
                .Select(es => new { es.ExamId, es.StudentId })
                .ToListAsync())
            .Select(p => (p.ExamId, p.StudentId))
            .ToHashSet();

        var newAssignments = new List<ExamStudent>();
        foreach (var examId in batchExams)
        {
            foreach (var studentId in studentIds)
            {
                if (existingPairs.Contains((examId, studentId))) continue;

                newAssignments.Add(new ExamStudent
                {
                    Id = Guid.NewGuid(),
                    ExamId = examId,
                    StudentId = studentId
                });
            }
        }

        if (newAssignments.Count > 0)
        {
            _context.ExamStudents.AddRange(newAssignments);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<List<Exam>> GetExamsForStudentAsync(Guid studentId)
    {
        return await _context.ExamStudents
            .AsNoTracking()
            .Where(es => es.StudentId == studentId && es.Exam.IsPublished)
            .Include(es => es.Exam)
            .Select(es => es.Exam)
            .OrderByDescending(e => e.StartDate)
            .ToListAsync();
    }

    public async Task<bool> IsStudentAssignedToExamAsync(Guid examId, Guid studentId)
    {
        return await _context.ExamStudents
            .AnyAsync(es => es.ExamId == examId && es.StudentId == studentId);
    }

    public async Task<ExamAttempt?> GetAttemptWithExamAsync(Guid attemptId)
    {
        return await _context.ExamAttempts
            .Include(ea => ea.Exam)
            .FirstOrDefaultAsync(ea => ea.Id == attemptId);
    }

    // Remaining time is always derived from the server clock and the attempt's
    // StartedAt — never from a client-supplied value. Negative result = expired.
    public static int ComputeRemainingSeconds(ExamAttempt attempt, int durationMinutes)
    {
        var deadline = attempt.StartedAt.AddMinutes(durationMinutes);
        return (int)(deadline - DateTime.UtcNow).TotalSeconds;
    }

    public static int ComputeRemainingSeconds(ExamAttempt attempt)
        => ComputeRemainingSeconds(attempt, attempt.Exam.DurationMinutes);

    // Proctoring auto-submit rule: a student may receive up to MaxProctoringWarnings
    // violation warnings; the violation that pushes the count *past* that allowance
    // locks the attempt. Only applies when proctoring is enabled and a positive
    // threshold is configured (0 = warn only, never auto-submit).
    public static bool ExceedsProctoringThreshold(int violationCount, Exam exam)
        => exam.EnableProctoring
           && exam.MaxProctoringWarnings > 0
           && violationCount > exam.MaxProctoringWarnings;

    public async Task<ExamAttempt?> GetActiveAttemptAsync(Guid examId, Guid studentId)
    {
        return await _context.ExamAttempts
            .Include(ea => ea.StudentAnswers)
            .FirstOrDefaultAsync(ea => ea.ExamId == examId
                && ea.StudentId == studentId
                && ea.Status == AttemptStatus.InProgress);
    }

    public async Task<ExamAttempt?> GetCompletedAttemptAsync(Guid examId, Guid studentId)
    {
        return await _context.ExamAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(ea => ea.ExamId == examId
                && ea.StudentId == studentId
                && (ea.Status == AttemptStatus.Submitted || ea.Status == AttemptStatus.TimedOut));
    }

    public async Task<ExamAttempt?> GetCompletedAttemptWithAnswersAsync(Guid examId, Guid studentId)
    {
        return await _context.ExamAttempts
            .AsNoTracking()
            .Include(ea => ea.StudentAnswers)
            .FirstOrDefaultAsync(ea => ea.ExamId == examId
                && ea.StudentId == studentId
                && (ea.Status == AttemptStatus.Submitted || ea.Status == AttemptStatus.TimedOut));
    }

    public async Task<HashSet<Guid>> GetCompletedExamIdsForStudentAsync(Guid studentId)
    {
        var ids = await _context.ExamAttempts
            .Where(ea => ea.StudentId == studentId
                && (ea.Status == AttemptStatus.Submitted || ea.Status == AttemptStatus.TimedOut))
            .Select(ea => ea.ExamId)
            .ToListAsync();
        return ids.ToHashSet();
    }

    public async Task<ExamAttempt> StartExamAttemptAsync(Guid examId, Guid studentId, int durationMinutes)
    {
        var attempt = new ExamAttempt
        {
            Id = Guid.NewGuid(),
            ExamId = examId,
            StudentId = studentId,
            TimeRemainingSeconds = durationMinutes * 60,
            Status = AttemptStatus.InProgress,
            ShuffleSeed = Random.Shared.Next(1, int.MaxValue)
        };

        _context.ExamAttempts.Add(attempt);
        try
        {
            await _context.SaveChangesAsync();
            return attempt;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent Start (e.g. a double-click) created the attempt first —
            // the unique index rejected ours, so continue with the existing one.
            _logger.LogInformation(
                "Concurrent exam start for student {StudentId} on exam {ExamId} — reusing existing attempt.",
                studentId, examId);
            _context.Entry(attempt).State = EntityState.Detached;
            var existing = await GetActiveAttemptAsync(examId, studentId);
            if (existing != null) return existing;
            throw;
        }
    }

    public async Task SaveAnswerAsync(Guid attemptId, Guid questionId, Guid? selectedOptionId, string? answerText, bool markedForReview = false)
    {
        var existing = await _context.StudentAnswers
            .FirstOrDefaultAsync(sa => sa.AttemptId == attemptId && sa.QuestionId == questionId);

        if (existing != null)
        {
            existing.SelectedOptionId = selectedOptionId;
            existing.AnswerText = answerText;
            existing.MarkedForReview = markedForReview;
            existing.AnsweredAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return;
        }

        var answer = new StudentAnswer
        {
            Id = Guid.NewGuid(),
            AttemptId = attemptId,
            QuestionId = questionId,
            SelectedOptionId = selectedOptionId,
            AnswerText = answerText,
            MarkedForReview = markedForReview
        };
        _context.StudentAnswers.Add(answer);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent autosave inserted the row between our check and our
            // insert — the unique index rejected ours, so apply it as an update.
            _logger.LogDebug(
                "Concurrent autosave for attempt {AttemptId}, question {QuestionId} — applying as update.",
                attemptId, questionId);
            _context.Entry(answer).State = EntityState.Detached;
            var winner = await _context.StudentAnswers
                .FirstOrDefaultAsync(sa => sa.AttemptId == attemptId && sa.QuestionId == questionId);
            if (winner == null) return;

            winner.SelectedOptionId = selectedOptionId;
            winner.AnswerText = answerText;
            winner.MarkedForReview = markedForReview;
            winner.AnsweredAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }
    }

    // Persists pending changes on attempt entities already tracked by this
    // scoped context (e.g. TabSwitchCount incremented in the controller).
    public Task SaveAttemptAsync() => _context.SaveChangesAsync();

    // Reorders questions/options per attempt using the attempt's stored seed,
    // so the order is stable across page refreshes but differs per student.
    // Questions shuffle within their subject group; subject sections keep
    // their original relative order. Seed 0 (legacy attempts) = no shuffle.
    //
    // Operates on the take-exam DTOs rather than entities: these are throwaway
    // objects with no DbContext behind them, so reassigning their collections
    // cannot ever be mistaken for a persisted DisplayOrder change.
    public static List<TakeExamQuestion> ApplyShuffle(
        List<TakeExamQuestion> orderedQuestions, TakeExamViewModel exam, int seed)
    {
        if (seed == 0 || (!exam.ShuffleQuestions && !exam.ShuffleOptions))
            return orderedQuestions;

        var result = orderedQuestions;

        if (exam.ShuffleQuestions)
        {
            var rng = new Random(seed);
            result = orderedQuestions
                .GroupBy(q => q.Subject ?? "")
                .SelectMany(g => FisherYates(g.ToList(), rng))
                .ToList();
        }

        if (exam.ShuffleOptions)
        {
            foreach (var q in result.Where(q => q.QuestionType == QuestionType.MCQ))
            {
                // Per-question rng derived from the question id so adding or
                // removing one question doesn't reshuffle every other one.
                var qRng = new Random(seed ^ BitConverter.ToInt32(q.Id.ToByteArray(), 0));
                q.Options = FisherYates(q.Options.OrderBy(o => o.DisplayOrder).ToList(), qRng);
            }
        }

        return result;
    }

    private static List<T> FisherYates<T>(List<T> list, Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    public async Task<Question> AddQuestionAsync(Question question)
    {
        question.Id = Guid.NewGuid();
        _context.Questions.Add(question);
        await _context.SaveChangesAsync();
        return question;
    }

    public async Task DeleteQuestionAsync(Guid questionId)
    {
        var question = await _context.Questions.FindAsync(questionId);
        if (question != null)
        {
            _context.Questions.Remove(question);
            await _context.SaveChangesAsync();
        }
    }

    public async Task RecalculateTotalMarksAsync(Guid examId)
    {
        var total = await _context.Questions
            .Where(q => q.ExamId == examId)
            .SumAsync(q => q.Marks);

        var exam = await _context.Exams.FindAsync(examId);
        if (exam != null)
        {
            exam.TotalMarks = total;
            await _context.SaveChangesAsync();
        }

        // Belt-and-braces: GradingGraphCacheInvalidator already evicted this exam when the
        // question rows were saved. Kept because it is free and covers the case where this
        // method is called after a write made through some other context.
        _cache?.Remove(GradingGraphCacheKey(examId));
    }

    public async Task<List<Question>> GetBankQuestionsAsync(
        string? subject = null, string? difficulty = null,
        string? type = null, string? search = null, string? topic = null)
    {
        var query = _context.Questions
            .AsNoTracking()
            .Where(q => q.IsInBank)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(subject))
            query = query.Where(q => q.Subject == subject);
        if (!string.IsNullOrWhiteSpace(topic))
            query = query.Where(q => q.Topic == topic);
        if (!string.IsNullOrWhiteSpace(difficulty))
            query = query.Where(q => q.DifficultyLevel == difficulty);
        if (!string.IsNullOrWhiteSpace(type) && Enum.TryParse<QuestionType>(type, out var qt))
            query = query.Where(q => q.QuestionType == qt);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(q => q.QuestionText != null && q.QuestionText.Contains(search));

        return await query.OrderBy(q => q.Subject).ThenBy(q => q.CreatedAt).ToListAsync();
    }

    public async Task<int> CloneFromBankAsync(Guid examId, List<Guid> bankQuestionIds)
    {
        var bankQuestions = await _context.Questions
            .Include(q => q.Options)
            .Include(q => q.AnswerKey)
            .Where(q => q.IsInBank && bankQuestionIds.Contains(q.Id))
            .ToListAsync();

        var maxOrder = await _context.Questions
            .Where(q => q.ExamId == examId)
            .MaxAsync(q => (int?)q.DisplayOrder) ?? 0;

        foreach (var bq in bankQuestions)
        {
            var clone = new Question
            {
                Id = Guid.NewGuid(),
                ExamId = examId,
                IsInBank = false,
                Subject = bq.Subject,
                Topic = bq.Topic,
                DifficultyLevel = bq.DifficultyLevel,
                QuestionText = bq.QuestionText,
                QuestionType = bq.QuestionType,
                ImagePath = bq.ImagePath,
                Marks = bq.Marks,
                HasNegativeMarking = bq.HasNegativeMarking,
                NegativeMarks = bq.NegativeMarks,
                HasNegativeMarksIfUnattempted = bq.HasNegativeMarksIfUnattempted,
                NegativeMarksIfUnattempted = bq.NegativeMarksIfUnattempted,
                Solution = bq.Solution,
                DisplayOrder = ++maxOrder,
                CreatedAt = DateTime.UtcNow
            };
            _context.Questions.Add(clone);

            foreach (var opt in bq.Options)
            {
                _context.QuestionOptions.Add(new QuestionOption
                {
                    Id = Guid.NewGuid(),
                    QuestionId = clone.Id,
                    OptionLabel = opt.OptionLabel,
                    OptionText = opt.OptionText,
                    ImagePath = opt.ImagePath,
                    IsCorrect = opt.IsCorrect,
                    DisplayOrder = opt.DisplayOrder
                });
            }

            if (bq.AnswerKey != null)
            {
                _context.AnswerKeys.Add(new AnswerKey
                {
                    Id = Guid.NewGuid(),
                    QuestionId = clone.Id,
                    CorrectAnswer = bq.AnswerKey.CorrectAnswer
                });
            }
        }

        await _context.SaveChangesAsync();
        return bankQuestions.Count;
    }

    public async Task CloneToBankAsync(Guid examQuestionId)
    {
        var source = await _context.Questions
            .Include(q => q.Options)
            .Include(q => q.AnswerKey)
            .FirstOrDefaultAsync(q => q.Id == examQuestionId);
        if (source == null || source.IsInBank) return;

        var clone = new Question
        {
            Id = Guid.NewGuid(),
            ExamId = null,
            IsInBank = true,
            Subject = source.Subject,
            Topic = source.Topic,
            DifficultyLevel = source.DifficultyLevel,
            QuestionText = source.QuestionText,
            QuestionType = source.QuestionType,
            ImagePath = source.ImagePath,
            Marks = source.Marks,
            HasNegativeMarking = source.HasNegativeMarking,
            NegativeMarks = source.NegativeMarks,
            HasNegativeMarksIfUnattempted = source.HasNegativeMarksIfUnattempted,
            NegativeMarksIfUnattempted = source.NegativeMarksIfUnattempted,
            Solution = source.Solution,
            DisplayOrder = 0,
            CreatedAt = DateTime.UtcNow
        };
        _context.Questions.Add(clone);

        foreach (var opt in source.Options)
        {
            _context.QuestionOptions.Add(new QuestionOption
            {
                Id = Guid.NewGuid(),
                QuestionId = clone.Id,
                OptionLabel = opt.OptionLabel,
                OptionText = opt.OptionText,
                ImagePath = opt.ImagePath,
                IsCorrect = opt.IsCorrect,
                DisplayOrder = opt.DisplayOrder
            });
        }

        if (source.AnswerKey != null)
        {
            _context.AnswerKeys.Add(new AnswerKey
            {
                Id = Guid.NewGuid(),
                QuestionId = clone.Id,
                CorrectAnswer = source.AnswerKey.CorrectAnswer
            });
        }

        await _context.SaveChangesAsync();
    }

    // Copies questions from any exam (or bank) to a target exam without removing the originals.
    public async Task<int> CopyQuestionsToExamAsync(List<Guid> questionIds, Guid targetExamId)
    {
        var questions = await _context.Questions
            .Include(q => q.Options)
            .Include(q => q.AnswerKey)
            .Where(q => questionIds.Contains(q.Id))
            .ToListAsync();

        var maxOrder = await _context.Questions
            .Where(q => q.ExamId == targetExamId)
            .MaxAsync(q => (int?)q.DisplayOrder) ?? 0;

        foreach (var source in questions)
        {
            var clone = new Question
            {
                Id = Guid.NewGuid(),
                ExamId = targetExamId,
                IsInBank = false,
                Subject = source.Subject,
                Topic = source.Topic,
                DifficultyLevel = source.DifficultyLevel,
                QuestionText = source.QuestionText,
                QuestionType = source.QuestionType,
                ImagePath = source.ImagePath,
                Marks = source.Marks,
                HasNegativeMarking = source.HasNegativeMarking,
                NegativeMarks = source.NegativeMarks,
                HasNegativeMarksIfUnattempted = source.HasNegativeMarksIfUnattempted,
                NegativeMarksIfUnattempted = source.NegativeMarksIfUnattempted,
                Solution = source.Solution,
                DisplayOrder = ++maxOrder,
                CreatedAt = DateTime.UtcNow
            };
            _context.Questions.Add(clone);

            foreach (var opt in source.Options)
            {
                _context.QuestionOptions.Add(new QuestionOption
                {
                    Id = Guid.NewGuid(),
                    QuestionId = clone.Id,
                    OptionLabel = opt.OptionLabel,
                    OptionText = opt.OptionText,
                    ImagePath = opt.ImagePath,
                    IsCorrect = opt.IsCorrect,
                    DisplayOrder = opt.DisplayOrder
                });
            }

            if (source.AnswerKey != null)
            {
                _context.AnswerKeys.Add(new AnswerKey
                {
                    Id = Guid.NewGuid(),
                    QuestionId = clone.Id,
                    CorrectAnswer = source.AnswerKey.CorrectAnswer
                });
            }
        }

        await _context.SaveChangesAsync();
        return questions.Count;
    }

    // Clones each question to the bank then removes it from the exam in a single SaveChanges.
    public async Task<int> BulkMoveToBankAsync(List<Guid> questionIds)
    {
        var questions = await _context.Questions
            .Include(q => q.Options)
            .Include(q => q.AnswerKey)
            .Where(q => questionIds.Contains(q.Id) && !q.IsInBank)
            .ToListAsync();

        foreach (var source in questions)
        {
            var clone = new Question
            {
                Id = Guid.NewGuid(),
                ExamId = null,
                IsInBank = true,
                Subject = source.Subject,
                Topic = source.Topic,
                DifficultyLevel = source.DifficultyLevel,
                QuestionText = source.QuestionText,
                QuestionType = source.QuestionType,
                ImagePath = source.ImagePath,
                Marks = source.Marks,
                HasNegativeMarking = source.HasNegativeMarking,
                NegativeMarks = source.NegativeMarks,
                HasNegativeMarksIfUnattempted = source.HasNegativeMarksIfUnattempted,
                NegativeMarksIfUnattempted = source.NegativeMarksIfUnattempted,
                Solution = source.Solution,
                DisplayOrder = 0,
                CreatedAt = DateTime.UtcNow
            };
            _context.Questions.Add(clone);

            foreach (var opt in source.Options)
            {
                _context.QuestionOptions.Add(new QuestionOption
                {
                    Id = Guid.NewGuid(),
                    QuestionId = clone.Id,
                    OptionLabel = opt.OptionLabel,
                    OptionText = opt.OptionText,
                    ImagePath = opt.ImagePath,
                    IsCorrect = opt.IsCorrect,
                    DisplayOrder = opt.DisplayOrder
                });
            }

            if (source.AnswerKey != null)
            {
                _context.AnswerKeys.Add(new AnswerKey
                {
                    Id = Guid.NewGuid(),
                    QuestionId = clone.Id,
                    CorrectAnswer = source.AnswerKey.CorrectAnswer
                });
            }

            _context.Questions.Remove(source);
        }

        await _context.SaveChangesAsync();
        return questions.Count;
    }
}
