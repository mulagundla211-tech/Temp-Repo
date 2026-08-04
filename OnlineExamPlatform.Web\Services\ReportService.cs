using Microsoft.EntityFrameworkCore;
using OnlineExamPlatform.Web.Controllers.Admin;
using OnlineExamPlatform.Web.Data;
using OnlineExamPlatform.Web.Models;

namespace OnlineExamPlatform.Web.Services;

public class ReportService
{
    private readonly ApplicationDbContext _context;

    public ReportService(ApplicationDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Builds the exam tiles for the Result Analysis landing page.
    /// </summary>
    /// <param name="schoolId">When set, restricts attempts and assignments to that school.</param>
    /// <param name="schoolBatchIds">When set, restricts exams to those batches.</param>
    /// <remarks>
    /// Every figure here is aggregated by the database and returns at most a few rows per
    /// exam. The original implementation materialised every attempt (with a four-level
    /// ThenInclude graph) plus every exam-student join, then rescanned the full attempt
    /// list once per exam — O(exams x attempts) work and a full-table load on every page
    /// view. Keep the aggregation server-side when changing this.
    /// </remarks>
    public async Task<List<ExamResultTileViewModel>> GetExamResultTilesAsync(
        Guid? schoolId, List<Guid>? schoolBatchIds)
    {
        var now = DateTime.UtcNow;

        var examsQuery = _context.Exams.AsNoTracking().AsQueryable();
        if (schoolBatchIds != null)
        {
            examsQuery = examsQuery.Where(e => e.BatchId != null && schoolBatchIds.Contains(e.BatchId.Value));
        }

        var exams = await examsQuery
            .OrderByDescending(e => e.StartDate)
            .Select(e => new { e.Id, e.Title, e.StartDate, e.EndDate, e.TotalMarks })
            .ToListAsync();

        if (exams.Count == 0) return new List<ExamResultTileViewModel>();

        var examIds = exams.Select(e => e.Id).ToList();

        // Scoped once and reused, including inside the correlated subquery below, so the
        // school filter can never diverge between the aggregates and the toppers.
        var scopedAttempts = _context.ExamAttempts
            .AsNoTracking()
            .Where(a => examIds.Contains(a.ExamId) && a.Status != AttemptStatus.InProgress);

        if (schoolId != null)
        {
            scopedAttempts = scopedAttempts.Where(a =>
                a.Student.StudentProfile != null && a.Student.StudentProfile.SchoolId == schoolId.Value);
        }

        // Count / highest / average, one row per exam.
        var attemptStats = (await scopedAttempts
                .GroupBy(a => a.ExamId)
                .Select(g => new
                {
                    ExamId = g.Key,
                    Attempted = g.Count(),
                    Highest = g.Max(a => a.TotalScore ?? 0m),
                    Average = g.Average(a => a.TotalScore ?? 0m)
                })
                .ToListAsync())
            .ToDictionary(s => s.ExamId);

        // Only attempts tying their exam's best score come back — one row per exam plus
        // any ties, rather than the whole attempts table.
        var topperLookup = (await scopedAttempts
                .Where(a => (a.TotalScore ?? 0m) == scopedAttempts
                    .Where(x => x.ExamId == a.ExamId)
                    .Max(x => x.TotalScore ?? 0m))
                .Select(a => new { a.ExamId, a.Student.FullName })
                .ToListAsync())
            .ToLookup(x => x.ExamId, x => x.FullName);

        var scopedAssignments = _context.ExamStudents
            .AsNoTracking()
            .Where(es => examIds.Contains(es.ExamId))
            .Join(_context.StudentProfiles,
                es => es.StudentId,
                sp => sp.Id,
                (es, sp) => new { es.ExamId, sp.SchoolId, sp.School, sp.Batch });

        if (schoolId != null)
        {
            scopedAssignments = scopedAssignments.Where(a => a.SchoolId == schoolId.Value);
        }

        var assignedCounts = (await scopedAssignments
                .GroupBy(a => a.ExamId)
                .Select(g => new { ExamId = g.Key, Assigned = g.Count() })
                .ToListAsync())
            .ToDictionary(x => x.ExamId, x => x.Assigned);

        // Distinct labels only — bounded by exams x schools x batches, not by the number
        // of assigned students.
        var labelLookup = (await scopedAssignments
                .Select(a => new
                {
                    a.ExamId,
                    SchoolName = a.School.Name,
                    BatchName = a.Batch.ClassName + " " + a.Batch.ProgramName
                })
                .Distinct()
                .ToListAsync())
            .ToLookup(x => x.ExamId);

        var tiles = new List<ExamResultTileViewModel>(exams.Count);

        foreach (var exam in exams)
        {
            attemptStats.TryGetValue(exam.Id, out var stats);
            var toppers = topperLookup[exam.Id].ToList();
            var labels = labelLookup[exam.Id].ToList();

            tiles.Add(new ExamResultTileViewModel
            {
                ExamId = exam.Id,
                ExamTitle = exam.Title,
                IsLive = exam.StartDate <= now && exam.EndDate >= now,
                IsUpcoming = exam.StartDate > now,
                IsCompleted = exam.EndDate < now,
                AssignedLocations = labels.Select(l => l.SchoolName).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList(),
                AssignedBatches = labels.Select(l => l.BatchName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList(),
                StartDate = exam.StartDate,
                TotalAttempted = stats?.Attempted ?? 0,
                TotalAssigned = assignedCounts.GetValueOrDefault(exam.Id),
                TotalMarks = exam.TotalMarks,
                HighestScore = stats?.Highest ?? 0m,
                AverageScore = stats?.Average ?? 0m,
                TopperName = toppers.FirstOrDefault() ?? "",
                TopperExtraCount = Math.Max(0, toppers.Count - 1),
                SubjectStats = new List<ExamResultSubjectStat>()
            });
        }

        return tiles;
    }

    public async Task<ExamResultSummary> GetExamResultsAsync(Guid examId)
    {
        var exam = await _context.Exams.FindAsync(examId);
        if (exam == null) return new ExamResultSummary();

        var attempts = await _context.ExamAttempts
            .AsNoTracking()
            .Include(a => a.Student)
                .ThenInclude(s => s.StudentProfile)
                    .ThenInclude(sp => sp.Batch)
            .Where(a => a.ExamId == examId && a.Status != AttemptStatus.InProgress)
            .OrderByDescending(a => a.TotalScore)
            .ToListAsync();

        int rank = 1;
        var studentResults = attempts.Select(a => new StudentResult
        {
            StudentId = a.StudentId,
            StudentName = a.Student.FullName,
            ClassName = a.Student.StudentProfile?.Batch?.ClassName ?? "",
            Batch = a.Student.StudentProfile?.Batch?.ProgramName ?? "",
            Score = a.TotalScore ?? 0,
            Percentage = exam.TotalMarks > 0
                ? Math.Round((double)(a.TotalScore ?? 0) / (double)exam.TotalMarks * 100, 2)
                : 0,
            Rank = rank++,
            Status = a.Status.ToString(),
            SubmittedAt = a.FinishedAt,
            TabSwitchCount = a.TabSwitchCount
        }).ToList();

        return new ExamResultSummary
        {
            ExamId = examId,
            ExamTitle = exam.Title,
            TotalMarks = exam.TotalMarks,
            TotalStudents = studentResults.Count,
            AverageScore = studentResults.Any() ? Math.Round(studentResults.Average(s => (double)s.Score), 2) : 0,
            HighestScore = studentResults.Any() ? studentResults.Max(s => s.Score) : 0,
            LowestScore = studentResults.Any() ? studentResults.Min(s => s.Score) : 0,
            StudentResults = studentResults
        };
    }

    public async Task<QuestionAnalysis> GetQuestionAnalysisAsync(Guid examId)
    {
        var questions = await _context.Questions
            .AsNoTracking()
            .Where(q => q.ExamId == examId)
            .Include(q => q.Options)
            .Include(q => q.StudentAnswers)
            .OrderBy(q => q.DisplayOrder)
            .ToListAsync();

        var analysis = questions.Select(q =>
        {
            var totalAttempted = q.StudentAnswers.Count;
            var correctCount = q.StudentAnswers.Count(sa => sa.IsCorrect == true);

            return new QuestionStat
            {
                QuestionId = q.Id,
                QuestionText = q.QuestionText,
                QuestionType = q.QuestionType.ToString(),
                Marks = q.Marks,
                TotalAttempted = totalAttempted,
                CorrectCount = correctCount,
                IncorrectCount = totalAttempted - correctCount,
                AccuracyPercentage = totalAttempted > 0
                    ? Math.Round((double)correctCount / totalAttempted * 100, 2)
                    : 0
            };
        }).ToList();

        return new QuestionAnalysis { Questions = analysis };
    }

    // ── Auto difficulty ────────────────────────────────────────────────────────
    // Classifies questions Easy/Medium/Hard purely from how students actually performed,
    // so the tag needs zero manual upkeep. Accuracy = correct ÷ graded answers, pooled
    // across every exam the question has appeared in (important for reused bank questions).
    // A question only gets a tag once it has enough graded answers to be meaningful.
    public const int AutoTagMinAttempts = 10;     // graded answers required before tagging
    private const double AutoEasyAtOrAbove = 70.0; // ≥70% correct → Easy
    private const double AutoHardBelow     = 40.0; // <40% correct → Hard (in between → Medium)

    public async Task<Dictionary<Guid, AutoDifficulty>> GetAutoDifficultyAsync(IReadOnlyCollection<Guid> questionIds)
    {
        var map = new Dictionary<Guid, AutoDifficulty>();
        if (questionIds == null || questionIds.Count == 0) return map;

        // Only graded answers count (IsCorrect != null) — ungraded subjective answers
        // would otherwise drag accuracy down and mis-tag questions.
        var stats = await _context.StudentAnswers
            .Where(sa => questionIds.Contains(sa.QuestionId) && sa.IsCorrect != null)
            .GroupBy(sa => sa.QuestionId)
            .Select(g => new
            {
                QuestionId = g.Key,
                Total = g.Count(),
                Correct = g.Count(sa => sa.IsCorrect == true)
            })
            .ToListAsync();

        foreach (var s in stats)
        {
            if (s.Total < AutoTagMinAttempts) continue;
            var accuracy = (double)s.Correct / s.Total * 100;
            var tag = accuracy >= AutoEasyAtOrAbove ? "Easy"
                    : accuracy < AutoHardBelow     ? "Hard"
                    : "Medium";
            map[s.QuestionId] = new AutoDifficulty
            {
                Tag = tag,
                AccuracyPercentage = Math.Round(accuracy, 1),
                Attempts = s.Total
            };
        }
        return map;
    }

    // Per-question peer accuracy — powers the student review's "X% of students answered
    // this correctly". Counts only graded answers so ungraded subjective responses don't
    // distort the figure. Keyed by question id; questions with no graded answers are omitted.
    public async Task<Dictionary<Guid, QuestionAccuracy>> GetQuestionAccuracyAsync(IReadOnlyCollection<Guid> questionIds)
    {
        var map = new Dictionary<Guid, QuestionAccuracy>();
        if (questionIds == null || questionIds.Count == 0) return map;

        var stats = await _context.StudentAnswers
            .Where(sa => questionIds.Contains(sa.QuestionId) && sa.IsCorrect != null)
            .GroupBy(sa => sa.QuestionId)
            .Select(g => new
            {
                QuestionId = g.Key,
                Total = g.Count(),
                Correct = g.Count(sa => sa.IsCorrect == true)
            })
            .ToListAsync();

        foreach (var s in stats)
        {
            map[s.QuestionId] = new QuestionAccuracy
            {
                AnsweredCount = s.Total,
                CorrectCount = s.Correct,
                CorrectPercentage = s.Total > 0 ? Math.Round((double)s.Correct / s.Total * 100, 0) : 0
            };
        }
        return map;
    }

    public async Task<List<StudentResult>> GetStudentResultsAsync(Guid studentId)
    {
        var attempts = await _context.ExamAttempts
            .AsNoTracking()
            .Include(a => a.Exam)
            .Where(a => a.StudentId == studentId && a.Status != AttemptStatus.InProgress)
            .OrderByDescending(a => a.FinishedAt)
            .ToListAsync();

        return attempts.Select(a => new StudentResult
        {
            StudentId = studentId,
            ExamId = a.ExamId,
            ExamTitle = a.Exam.Title,
            ExamEndDate = a.Exam.EndDate,
            Score = a.TotalScore ?? 0,
            TotalMarks = a.Exam.TotalMarks,
            Percentage = a.Exam.TotalMarks > 0
                ? Math.Round((double)(a.TotalScore ?? 0) / (double)a.Exam.TotalMarks * 100, 2)
                : 0,
            Status = a.Status.ToString(),
            SubmittedAt = a.FinishedAt
        }).ToList();
    }

    // Per-subject score percentage for each completed attempt — powers the
    // subject-wise progress trend chart. Marks for never-visited questions are
    // not penalised here; this is a trend indicator, not the official score.
    public async Task<List<SubjectTrendPoint>> GetStudentSubjectTrendAsync(Guid studentId)
    {
        var obtained = await _context.StudentAnswers
            .Where(sa => sa.Attempt.StudentId == studentId
                && sa.Attempt.Status != AttemptStatus.InProgress
                && sa.Question.Subject != null)
            .GroupBy(sa => new
            {
                sa.AttemptId,
                ExamId = sa.Attempt.ExamId,
                sa.Attempt.Exam.Title,
                sa.Attempt.FinishedAt,
                Subject = sa.Question.Subject!
            })
            .Select(g => new
            {
                g.Key.AttemptId,
                g.Key.ExamId,
                g.Key.Title,
                g.Key.FinishedAt,
                g.Key.Subject,
                Obtained = g.Sum(sa => sa.MarksObtained ?? 0)
            })
            .ToListAsync();

        var examIds = obtained.Select(o => o.ExamId).Distinct().ToList();
        var subjectMax = await _context.Questions
            .Where(q => q.ExamId != null && examIds.Contains(q.ExamId.Value) && q.Subject != null)
            .GroupBy(q => new { ExamId = q.ExamId!.Value, Subject = q.Subject! })
            .Select(g => new { g.Key.ExamId, g.Key.Subject, MaxMarks = g.Sum(q => q.Marks) })
            .ToListAsync();
        var maxLookup = subjectMax.ToDictionary(m => (m.ExamId, m.Subject), m => m.MaxMarks);

        return obtained
            .Where(o => maxLookup.GetValueOrDefault((o.ExamId, o.Subject)) > 0)
            .Select(o => new SubjectTrendPoint
            {
                ExamTitle = o.Title,
                FinishedAt = o.FinishedAt,
                Subject = o.Subject,
                Percentage = Math.Round(
                    (double)o.Obtained / maxLookup[(o.ExamId, o.Subject)] * 100, 1)
            })
            .OrderBy(p => p.FinishedAt)
            .ToList();
    }
}

// View Models for Reports
public class ExamResultSummary
{
    public Guid ExamId { get; set; }
    public string ExamTitle { get; set; } = string.Empty;
    public int TotalMarks { get; set; }
    public int TotalStudents { get; set; }
    public double AverageScore { get; set; }
    public decimal HighestScore { get; set; }
    public decimal LowestScore { get; set; }
    public List<StudentResult> StudentResults { get; set; } = new();
}

public class StudentResult
{
    public Guid StudentId { get; set; }
    public string StudentName { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string Batch { get; set; } = string.Empty;
    public Guid ExamId { get; set; }
    public string ExamTitle { get; set; } = string.Empty;
    public DateTime? ExamEndDate { get; set; }
    public decimal Score { get; set; }
    public int TotalMarks { get; set; }
    public double Percentage { get; set; }
    public int Rank { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? SubmittedAt { get; set; }
    public int TabSwitchCount { get; set; }
}

public class QuestionAnalysis
{
    public List<QuestionStat> Questions { get; set; } = new();
}

// Performance-derived difficulty tag for a question (null when not enough data).
public class AutoDifficulty
{
    public string Tag { get; set; } = "";   // Easy | Medium | Hard
    public double AccuracyPercentage { get; set; }
    public int Attempts { get; set; }
}

// How peers performed on a single question (for student-facing review feedback).
public class QuestionAccuracy
{
    public int AnsweredCount { get; set; }
    public int CorrectCount { get; set; }
    public double CorrectPercentage { get; set; }
}

public class SubjectTrendPoint
{
    public string ExamTitle { get; set; } = string.Empty;
    public DateTime? FinishedAt { get; set; }
    public string Subject { get; set; } = string.Empty;
    public double Percentage { get; set; }
}

public class QuestionStat
{
    public Guid QuestionId { get; set; }
    public string QuestionText { get; set; } = string.Empty;
    public string QuestionType { get; set; } = string.Empty;
    public int Marks { get; set; }
    public int TotalAttempted { get; set; }
    public int CorrectCount { get; set; }
    public int IncorrectCount { get; set; }
    public double AccuracyPercentage { get; set; }
}
