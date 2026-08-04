using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OnlineExamPlatform.Web.Models;
using OnlineExamPlatform.Web.Services;

namespace OnlineExamPlatform.Web.Controllers.Student;

[Authorize(Roles = "Student")]
public class StudentExamController : Controller
{
    private readonly ExamService _examService;
    private readonly GradingService _gradingService;
    private readonly ReportService _reportService;
    private readonly UserManager<ApplicationUser> _userManager;

    public StudentExamController(
        ExamService examService,
        GradingService gradingService,
        ReportService reportService,
        UserManager<ApplicationUser> userManager)
    {
        _examService = examService;
        _gradingService = gradingService;
        _reportService = reportService;
        _userManager = userManager;
    }

    [HttpGet]
    public async Task<IActionResult> Start(Guid examId)
    {
        var user = await _userManager.GetUserAsync(User);
        var exam = await _examService.GetTakeExamAsync(examId);
        if (exam == null) return NotFound();

        // Date gate enforcement
        var now = DateTime.UtcNow;
        if (now < exam.StartDate || now > exam.EndDate)
        {
            TempData["Error"] = "This exam is not available at this time.";
            return RedirectToAction("Index", "StudentDashboard");
        }

        if (!await _examService.IsStudentAssignedToExamAsync(examId, user!.Id))
        {
            TempData["Error"] = "You are not assigned to this exam.";
            return RedirectToAction("Index", "StudentDashboard");
        }

        // Check for existing active attempt (session recovery)
        var attempt = await _examService.GetActiveAttemptAsync(examId, user.Id);

        // Check if student already completed this exam
        if (attempt == null)
        {
            var completedAttempt = await _examService.GetCompletedAttemptAsync(examId, user.Id);
            if (completedAttempt != null)
            {
                TempData["Error"] = "You have already completed this exam.";
                return RedirectToAction("Index", "StudentDashboard");
            }
        }

        if (attempt != null)
        {
            // Recovered attempt: re-derive remaining time from the server clock.
            // If it already expired while the student was away, grade it now.
            var remaining = ExamService.ComputeRemainingSeconds(attempt, exam.DurationMinutes);
            if (remaining <= 0)
            {
                await _gradingService.GradeAndSubmitAsync(attempt.Id, timedOut: true);
                TempData["Error"] = "Time ran out on your previous attempt; it has been submitted automatically.";
                return RedirectToAction("Results");
            }
            attempt.TimeRemainingSeconds = remaining;
        }
        else
        {
            attempt = await _examService.StartExamAttemptAsync(examId, user.Id, exam.DurationMinutes);
        }

        ViewBag.Exam = exam;
        ViewBag.Attempt = attempt;
        ViewBag.StudentName = user!.FullName;
        // exam.Questions is already ordered by DisplayOrder by the projection.
        ViewBag.Questions = ExamService.ApplyShuffle(exam.Questions, exam, attempt.ShuffleSeed);

        // Prevent the browser from caching this page so it is never restored
        // from bfcache (which would show a blank screen with all questions hidden).
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";

        return View("TakeExam");
    }

    // Grace period for in-flight saves and the final submit after the timer
    // hits zero (network latency, auto-submit round trip).
    private const int SubmitGraceSeconds = 30;

    // Loads the attempt only if it belongs to the logged-in student.
    private async Task<ExamAttempt?> GetOwnedAttemptAsync(Guid attemptId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return null;

        var attempt = await _examService.GetAttemptWithExamAsync(attemptId);
        return attempt?.StudentId == user.Id ? attempt : null;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAnswer([FromBody] SaveAnswerRequest request)
    {
        var attempt = await GetOwnedAttemptAsync(request.AttemptId);
        if (attempt == null)
            return StatusCode(403, new { success = false });
        if (attempt.Status != AttemptStatus.InProgress)
            return Json(new { success = false, reason = "submitted" });

        if (ExamService.ComputeRemainingSeconds(attempt) < -SubmitGraceSeconds)
        {
            await _gradingService.GradeAndSubmitAsync(attempt.Id, timedOut: true);
            return Json(new { success = false, reason = "timeup" });
        }

        await _examService.SaveAnswerAsync(request.AttemptId, request.QuestionId, request.SelectedOptionId, request.AnswerText, request.MarkedForReview);
        return Json(new { success = true });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateTimer([FromBody] UpdateTimerRequest request)
    {
        var attempt = await GetOwnedAttemptAsync(request.AttemptId);
        if (attempt == null)
            return StatusCode(403, new { success = false });
        if (attempt.Status != AttemptStatus.InProgress)
            return Json(new { success = false, reason = "submitted" });

        // The client-reported value is ignored; the server clock is authoritative.
        var remaining = ExamService.ComputeRemainingSeconds(attempt);
        if (remaining <= 0)
        {
            await _gradingService.GradeAndSubmitAsync(attempt.Id, timedOut: true);
            return Json(new { success = true, secondsRemaining = 0, expired = true });
        }

        // No DB write here: remaining time is always recomputed from the attempt's
        // StartedAt, so persisting TimeRemainingSeconds on every 30 s sync would be
        // a wasted write. The initial value stored at StartExamAttempt is enough.
        return Json(new { success = true, secondsRemaining = remaining, expired = false });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RecordTabSwitch([FromBody] TabSwitchRequest request)
    {
        var attempt = await GetOwnedAttemptAsync(request.AttemptId);
        if (attempt == null)
            return StatusCode(403, new { success = false });
        if (attempt.Status != AttemptStatus.InProgress)
            return Json(new { success = false, reason = "submitted" });

        attempt.TabSwitchCount++;
        await _examService.SaveAttemptAsync();

        // Server is authoritative: if the violation count exceeds the configured
        // allowance, lock the attempt by grading + submitting it now. The client
        // cannot bypass this by disabling JS — the next save/timer call will also
        // see the submitted status.
        var locked = ExamService.ExceedsProctoringThreshold(attempt.TabSwitchCount, attempt.Exam);
        if (locked)
            await _gradingService.GradeAndSubmitAsync(attempt.Id, timedOut: false);

        return Json(new
        {
            success = true,
            count = attempt.TabSwitchCount,
            max = attempt.Exam.EnableProctoring ? attempt.Exam.MaxProctoringWarnings : 0,
            locked
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(Guid attemptId)
    {
        var attempt = await GetOwnedAttemptAsync(attemptId);
        if (attempt == null) return NotFound();

        if (attempt.Status != AttemptStatus.InProgress)
        {
            TempData["Error"] = "This exam has already been submitted.";
            return RedirectToAction("Results");
        }

        var timedOut = ExamService.ComputeRemainingSeconds(attempt) < -SubmitGraceSeconds;
        await _gradingService.GradeAndSubmitAsync(attemptId, timedOut);
        TempData["Success"] = "Exam submitted and graded successfully.";
        return RedirectToAction("Results");
    }

    [HttpGet]
    public async Task<IActionResult> Results()
    {
        var user = await _userManager.GetUserAsync(User);
        var results = await _reportService.GetStudentResultsAsync(user!.Id);
        ViewBag.SubjectTrend = await _reportService.GetStudentSubjectTrendAsync(user.Id);
        return View(results);
    }

    [HttpGet]
    public async Task<IActionResult> Review(Guid examId)
    {
        var user = await _userManager.GetUserAsync(User);
        var exam = await _examService.GetExamForReviewAsync(examId);
        if (exam == null) return NotFound();

        var attempt = await _examService.GetCompletedAttemptWithAnswersAsync(examId, user!.Id);
        if (attempt == null)
        {
            TempData["Error"] = "You have not completed this exam yet.";
            return RedirectToAction("Results");
        }

        // Solutions must stay hidden while other students can still take the exam.
        if (DateTime.UtcNow < exam.EndDate)
        {
            TempData["Error"] = $"Answer review opens after the exam window closes on {DateTimeDisplay.ToIstString(exam.EndDate, "dd MMM yyyy HH:mm")}.";
            return RedirectToAction("Results");
        }

        var answersByQuestion = attempt.StudentAnswers
            .GroupBy(sa => sa.QuestionId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(sa => sa.AnsweredAt).First());

        // Peer accuracy per question — "X% of students answered this correctly".
        var accuracy = await _reportService.GetQuestionAccuracyAsync(
            exam.Questions.Select(q => q.Id).ToList());

        var items = exam.Questions
            .OrderBy(q => q.DisplayOrder)
            .Select(q =>
            {
                var acc = accuracy.GetValueOrDefault(q.Id);
                return new ReviewItem
                {
                    Question = q,
                    Answer = answersByQuestion.GetValueOrDefault(q.Id),
                    PeerAnsweredCount = acc?.AnsweredCount ?? 0,
                    PeerCorrectPercentage = acc?.CorrectPercentage ?? 0
                };
            })
            .ToList();

        return View(new ExamReviewViewModel
        {
            Exam = exam,
            Attempt = attempt,
            Items = items
        });
    }
}

public class ExamReviewViewModel
{
    public Exam Exam { get; set; } = null!;
    public ExamAttempt Attempt { get; set; } = null!;
    public List<ReviewItem> Items { get; set; } = new();
}

public class ReviewItem
{
    public Question Question { get; set; } = null!;
    public StudentAnswer? Answer { get; set; }

    // How peers did on this question (0 responses = hide the feedback).
    public int PeerAnsweredCount { get; set; }
    public double PeerCorrectPercentage { get; set; }
}

public class SaveAnswerRequest
{
    public Guid AttemptId { get; set; }
    public Guid QuestionId { get; set; }
    public Guid? SelectedOptionId { get; set; }
    public string? AnswerText { get; set; }
    public bool MarkedForReview { get; set; } = false;
}

public class UpdateTimerRequest
{
    public Guid AttemptId { get; set; }
    public int SecondsRemaining { get; set; }
}

public class TabSwitchRequest
{
    public Guid AttemptId { get; set; }
}
