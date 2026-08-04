using ClosedXML.Excel;
using OnlineExamPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnlineExamPlatform.Web.Data;
using OnlineExamPlatform.Web.Models;

namespace OnlineExamPlatform.Web.Controllers.Admin;

[Authorize(Roles = "SuperAdmin,Admin,SchoolAdmin")]
public class ResultAnalysisController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ReportService _reportService;

    public ResultAnalysisController(ApplicationDbContext context, UserManager<ApplicationUser> userManager,
        ReportService reportService)
    {
        _context = context;
        _userManager = userManager;
        _reportService = reportService;
    }

    public async Task<IActionResult> Index()
    {
        var currentUser = await _userManager.GetUserAsync(User);

        Guid? schoolId = null;
        List<Guid>? schoolBatchIds = null;

        if (User.IsInRole("SchoolAdmin") && currentUser != null)
        {
            var profile = await _context.SchoolAdminProfiles
                .AsNoTracking()
                .Include(p => p.School)
                .FirstOrDefaultAsync(p => p.Id == currentUser.Id);
            if (profile != null && profile.School != null)
            {
                schoolId = profile.SchoolId;
                schoolBatchIds = await _context.SchoolBatches
                    .AsNoTracking()
                    .Where(sb => sb.SchoolId == schoolId)
                    .Select(sb => sb.BatchId)
                    .ToListAsync();
            }
        }

        // Aggregation lives in ReportService so it can be tested without a UserManager
        // or HttpContext, and so the server-side GroupBy stays in one place.
        var tiles = await _reportService.GetExamResultTilesAsync(schoolId, schoolBatchIds);

        return View(tiles);
    }


    [HttpGet]
    public async Task<IActionResult> ResultsTabular()
    {
        var currentUser = await _userManager.GetUserAsync(User);
        Guid? schoolId = null;
        string? schoolName = null;

        if (User.IsInRole("SchoolAdmin") && currentUser != null)
        {
            var profile = await _context.SchoolAdminProfiles
                .Include(p => p.School)
                .FirstOrDefaultAsync(p => p.Id == currentUser.Id);
            if (profile != null)
            {
                schoolId = profile.SchoolId;
                schoolName = profile.School?.Name;
            }
        }

        var schools = new List<School>();
        if (User.IsInRole("SuperAdmin") || User.IsInRole("Admin"))
        {
            schools = await _context.Schools.OrderBy(s => s.Name).ToListAsync();
        }
        
        ViewBag.UserRole = User.IsInRole("SchoolAdmin") ? "SchoolAdmin" : "Admin";
        ViewBag.SchoolId = schoolId;
        ViewBag.SchoolName = schoolName;
        ViewBag.Schools = schools;

        return View();
    }

    [HttpGet]
    public async Task<IActionResult> GetExamsForSchool(Guid schoolId)
    {
        var batchIds = await _context.SchoolBatches
            .Where(sb => sb.SchoolId == schoolId)
            .Select(sb => sb.BatchId)
            .ToListAsync();

        var exams = await _context.Exams
            .Where(e => e.BatchId != null && batchIds.Contains(e.BatchId.Value))
            .OrderByDescending(e => e.StartDate)
            .Select(e => new { id = e.Id, title = e.Title })
            .ToListAsync();

        return Json(exams);
    }

    [HttpGet]
    public async Task<IActionResult> GetBatchesForExamAndSchool(Guid examId, Guid schoolId)
    {
        var batchIds = await _context.SchoolBatches
            .Where(sb => sb.SchoolId == schoolId)
            .Select(sb => sb.BatchId)
            .ToListAsync();

        var batches = await _context.Batches
            .Where(b => batchIds.Contains(b.Id))
            .Select(b => new { id = b.Id, name = b.ClassName + " " + b.ProgramName })
            .ToListAsync();

        return Json(batches);
    }

    [HttpGet]
    public async Task<IActionResult> GetStudentResults(Guid examId, Guid schoolId, Guid batchId)
    {
        var reportService = HttpContext.RequestServices.GetRequiredService<Services.ReportService>();
        var results = await reportService.GetExamResultsAsync(examId);

        if (results.StudentResults != null)
        {
            // Filter by school and batch
            // The StudentResult model doesn't explicitly have SchoolId and BatchId.
            // But we can filter the actual ExamAttempts or StudentProfiles.
            var studentIds = await _context.StudentProfiles
                .Where(sp => sp.SchoolId == schoolId && sp.BatchId == batchId)
                .Select(sp => sp.Id)
                .ToListAsync();

            results.StudentResults = results.StudentResults
                .Where(sr => studentIds.Contains(sr.StudentId))
                .ToList();

            results.TotalStudents = results.StudentResults.Count;
            results.AverageScore = results.StudentResults.Any() 
                ? Math.Round(results.StudentResults.Average(s => (double)s.Score), 2) : 0;
            results.HighestScore = results.StudentResults.Any() 
                ? results.StudentResults.Max(s => s.Score) : 0;
            results.LowestScore = results.StudentResults.Any() 
                ? results.StudentResults.Min(s => s.Score) : 0;

            // Recalculate ranks
            int rank = 1;
            foreach (var r in results.StudentResults.OrderByDescending(sr => sr.Score))
            {
                r.Rank = rank++;
            }
        }

        return PartialView("_ResultsTablePartial", results);
    }

    [HttpGet]
    public async Task<IActionResult> DownloadExcel(Guid id)
    {
        var exam = await _context.Exams.FindAsync(id);
        if (exam == null) return NotFound();

        var currentUser = await _userManager.GetUserAsync(User);
        Guid? schoolId = null;
        if (User.IsInRole("SchoolAdmin") && currentUser != null)
        {
            var profile = await _context.SchoolAdminProfiles.FirstOrDefaultAsync(p => p.Id == currentUser.Id);
            if (profile != null) schoolId = profile.SchoolId;
        }

        var reportService = HttpContext.RequestServices.GetRequiredService<ReportService>();
        var summary = await reportService.GetExamResultsAsync(id);

        if (schoolId != null && summary.StudentResults != null)
        {
            var schoolStudentIds = await _context.StudentProfiles
                .Where(sp => sp.SchoolId == schoolId.Value)
                .Select(sp => sp.Id)
                .ToHashSetAsync();

            summary.StudentResults = summary.StudentResults
                .Where(sr => schoolStudentIds.Contains(sr.StudentId))
                .ToList();

            int rank = 1;
            foreach (var sr in summary.StudentResults.OrderByDescending(s => s.Score))
            {
                sr.Rank = rank++;
            }
        }

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Exam Results");

        worksheet.Cell(1, 1).Value = "Exam Title:";
        worksheet.Cell(1, 1).Style.Font.Bold = true;
        worksheet.Cell(1, 2).Value = summary.ExamTitle;

        worksheet.Cell(2, 1).Value = "Total Marks:";
        worksheet.Cell(2, 1).Style.Font.Bold = true;
        worksheet.Cell(2, 2).Value = summary.TotalMarks;

        worksheet.Cell(3, 1).Value = "Total Students:";
        worksheet.Cell(3, 1).Style.Font.Bold = true;
        worksheet.Cell(3, 2).Value = summary.StudentResults.Count;

        var headers = new[] { "Rank", "Student Name", "Class / Batch", "Score", "Total Marks", "Percentage (%)", "Status", "Submitted At", "Tab Switches" };
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = worksheet.Cell(5, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#2C3590");
            cell.Style.Font.FontColor = XLColor.White;
        }

        int rowIdx = 6;
        foreach (var sr in summary.StudentResults)
        {
            worksheet.Cell(rowIdx, 1).Value = sr.Rank;
            worksheet.Cell(rowIdx, 2).Value = sr.StudentName;
            worksheet.Cell(rowIdx, 3).Value = $"{sr.ClassName} {sr.Batch}".Trim();
            worksheet.Cell(rowIdx, 4).Value = (double)sr.Score;
            worksheet.Cell(rowIdx, 5).Value = sr.TotalMarks;
            worksheet.Cell(rowIdx, 6).Value = sr.Percentage;
            worksheet.Cell(rowIdx, 7).Value = sr.Status;
            worksheet.Cell(rowIdx, 8).Value = sr.SubmittedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A";
            worksheet.Cell(rowIdx, 9).Value = sr.TabSwitchCount;
            rowIdx++;
        }

        worksheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        var content = stream.ToArray();

        var safeTitle = string.Concat(summary.ExamTitle.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_");
        var fileName = $"{safeTitle}_Results.xlsx";
        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    [HttpGet]
    public async Task<IActionResult> DiagnosticAnalytics(Guid examId, Guid? schoolId = null, Guid? batchId = null)
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null) return Challenge();

        var exam = await _context.Exams
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == examId);

        if (exam == null) return NotFound();

        Guid? activeSchoolId = schoolId;
        if (User.IsInRole("SchoolAdmin"))
        {
            var profile = await _context.SchoolAdminProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == currentUser.Id);
            if (profile != null)
            {
                activeSchoolId = profile.SchoolId;
            }
        }

        var attemptsQuery = _context.ExamAttempts
            .AsNoTracking()
            .Include(a => a.Student).ThenInclude(s => s.StudentProfile)
            .Where(a => a.ExamId == examId && a.Status != AttemptStatus.InProgress);

        if (activeSchoolId.HasValue)
        {
            attemptsQuery = attemptsQuery.Where(a => a.Student.StudentProfile != null && a.Student.StudentProfile.SchoolId == activeSchoolId.Value);
        }

        if (batchId.HasValue)
        {
            attemptsQuery = attemptsQuery.Where(a => a.Student.StudentProfile != null && a.Student.StudentProfile.BatchId == batchId.Value);
        }

        var attempts = await attemptsQuery.ToListAsync();
        var attemptIds = attempts.Select(a => a.Id).ToList();

        var questions = await _context.Questions
            .AsNoTracking()
            .Where(q => q.ExamId == examId)
            .ToListAsync();

        var studentAnswers = await _context.StudentAnswers
            .AsNoTracking()
            .Where(sa => attemptIds.Contains(sa.AttemptId))
            .ToListAsync();

        var viewModel = new DiagnosticAnalyticsViewModel
        {
            ExamId = exam.Id,
            ExamTitle = exam.Title,
            SelectedSchoolId = activeSchoolId,
            SelectedBatchId = batchId,
            TotalStudentsAttempted = attempts.Count,
            TotalQuestionsInExam = questions.Count
        };

        if (activeSchoolId.HasValue)
        {
            var sch = await _context.Schools.AsNoTracking().FirstOrDefaultAsync(s => s.Id == activeSchoolId.Value);
            if (sch != null) viewModel.SelectedSchoolName = sch.Name;
        }

        if (batchId.HasValue)
        {
            var bch = await _context.Batches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == batchId.Value);
            if (bch != null) viewModel.SelectedBatchName = bch.Name;
        }

        // Available filter dropdowns
        if (User.IsInRole("SuperAdmin") || User.IsInRole("Admin"))
        {
            viewModel.AvailableSchools = await _context.Schools.AsNoTracking().OrderBy(s => s.Name).ToListAsync();
        }
        if (activeSchoolId.HasValue)
        {
            var bIds = await _context.SchoolBatches.AsNoTracking().Where(sb => sb.SchoolId == activeSchoolId.Value).Select(sb => sb.BatchId).ToListAsync();
            viewModel.AvailableBatches = await _context.Batches.AsNoTracking().Where(b => bIds.Contains(b.Id)).OrderBy(b => b.Name).ToListAsync();
        }
        else
        {
            viewModel.AvailableBatches = await _context.Batches.AsNoTracking().OrderBy(b => b.Name).ToListAsync();
        }

        // Total Accuracy
        int totalAnswers = studentAnswers.Count;
        int totalCorrect = studentAnswers.Count(sa => sa.IsCorrect == true);
        viewModel.OverallAccuracyPercentage = totalAnswers > 0 ? Math.Round((decimal)totalCorrect / totalAnswers * 100, 1) : 0;

        // Subject Breakdown
        var questionDict = questions.ToDictionary(q => q.Id);

        var subjectGroups = studentAnswers
            .Where(sa => questionDict.ContainsKey(sa.QuestionId))
            .GroupBy(sa => questionDict[sa.QuestionId].Subject ?? "General");

        foreach (var group in subjectGroups)
        {
            string subjName = group.Key;
            int qCount = questions.Count(q => (q.Subject ?? "General") == subjName);
            int groupTotal = group.Count();
            int groupCorrect = group.Count(sa => sa.IsCorrect == true);
            decimal acc = groupTotal > 0 ? Math.Round((decimal)groupCorrect / groupTotal * 100, 1) : 0;
            decimal avgMarks = group.Any() ? Math.Round(group.Average(sa => sa.MarksObtained ?? 0), 1) : 0;

            viewModel.SubjectDiagnostics.Add(new SubjectDiagnosticStat
            {
                SubjectName = subjName,
                TotalQuestions = qCount,
                TotalAnswersSubmitted = groupTotal,
                CorrectAnswersCount = groupCorrect,
                AccuracyPercentage = acc,
                AverageMarks = avgMarks
            });
        }

        // Topic Breakdown
        var topicGroups = studentAnswers
            .Where(sa => questionDict.ContainsKey(sa.QuestionId))
            .GroupBy(sa => new { 
                Subject = questionDict[sa.QuestionId].Subject ?? "General",
                Topic = string.IsNullOrWhiteSpace(questionDict[sa.QuestionId].Topic) ? "General Topic" : questionDict[sa.QuestionId].Topic!.Trim() 
            });

        foreach (var group in topicGroups)
        {
            string subjName = group.Key.Subject;
            string topName = group.Key.Topic;
            int qCount = questions.Count(q => (q.Subject ?? "General") == subjName && (string.IsNullOrWhiteSpace(q.Topic) ? "General Topic" : q.Topic.Trim()) == topName);
            int groupTotal = group.Count();
            int groupCorrect = group.Count(sa => sa.IsCorrect == true);
            decimal acc = groupTotal > 0 ? Math.Round((decimal)groupCorrect / groupTotal * 100, 1) : 0;

            var stat = new TopicDiagnosticStat
            {
                SubjectName = subjName,
                TopicName = topName,
                TotalQuestions = qCount,
                TotalAnswersSubmitted = groupTotal,
                CorrectAnswersCount = groupCorrect,
                AccuracyPercentage = acc
            };

            viewModel.TopicDiagnostics.Add(stat);
            if (stat.RequiresRemedialFocus)
            {
                viewModel.RemedialFocusTopics.Add($"{subjName}: {topName} ({acc}% Accuracy)");
            }
        }

        // Difficulty Breakdown
        var diffGroups = studentAnswers
            .Where(sa => questionDict.ContainsKey(sa.QuestionId))
            .GroupBy(sa => string.IsNullOrWhiteSpace(questionDict[sa.QuestionId].DifficultyLevel) ? "Medium" : questionDict[sa.QuestionId].DifficultyLevel!.Trim());

        foreach (var group in diffGroups)
        {
            string diffLevel = group.Key;
            int qCount = questions.Count(q => (string.IsNullOrWhiteSpace(q.DifficultyLevel) ? "Medium" : q.DifficultyLevel.Trim()) == diffLevel);
            int groupTotal = group.Count();
            int groupCorrect = group.Count(sa => sa.IsCorrect == true);
            decimal acc = groupTotal > 0 ? Math.Round((decimal)groupCorrect / groupTotal * 100, 1) : 0;

            viewModel.DifficultyDiagnostics.Add(new DifficultyDiagnosticStat
            {
                DifficultyLevel = diffLevel,
                QuestionCount = qCount,
                TotalAnswersSubmitted = groupTotal,
                CorrectAnswersCount = groupCorrect,
                AccuracyPercentage = acc
            });
        }

        viewModel.TopicDiagnostics = viewModel.TopicDiagnostics.OrderBy(t => t.AccuracyPercentage).ToList();
        viewModel.SubjectDiagnostics = viewModel.SubjectDiagnostics.OrderByDescending(s => s.AccuracyPercentage).ToList();

        return View(viewModel);
    }
}

public class ExamResultTileViewModel
{
    public Guid ExamId { get; set; }
    public string ExamTitle { get; set; } = string.Empty;
    public bool IsLive { get; set; }
    public bool IsUpcoming { get; set; }
    public bool IsCompleted { get; set; }
    public List<string> AssignedLocations { get; set; } = new();
    public List<string> AssignedBatches { get; set; } = new();
    public DateTime StartDate { get; set; }
    public int TotalAttempted { get; set; }
    public int TotalAssigned { get; set; }
    public string TopperName { get; set; } = string.Empty;
    public int TopperExtraCount { get; set; }
    public int TotalMarks { get; set; }
    public decimal HighestScore { get; set; }
    public decimal AverageScore { get; set; }
    public List<ExamResultSubjectStat> SubjectStats { get; set; } = new();
}

public class ExamResultSubjectStat
{
    public string Subject { get; set; } = string.Empty;
    public decimal Highest { get; set; }
    public decimal Average { get; set; }
    public string TopperName { get; set; } = string.Empty;
    public int TopperExtraCount { get; set; }
}

public class DiagnosticAnalyticsViewModel
{
    public Guid ExamId { get; set; }
    public string ExamTitle { get; set; } = string.Empty;
    public Guid? SelectedSchoolId { get; set; }
    public string SelectedSchoolName { get; set; } = "All Schools";
    public Guid? SelectedBatchId { get; set; }
    public string SelectedBatchName { get; set; } = "All Batches";
    
    public int TotalStudentsAttempted { get; set; }
    public int TotalQuestionsInExam { get; set; }
    public decimal OverallAccuracyPercentage { get; set; }
    
    public List<SubjectDiagnosticStat> SubjectDiagnostics { get; set; } = new();
    public List<TopicDiagnosticStat> TopicDiagnostics { get; set; } = new();
    public List<DifficultyDiagnosticStat> DifficultyDiagnostics { get; set; } = new();
    public List<string> RemedialFocusTopics { get; set; } = new();
    
    public List<School> AvailableSchools { get; set; } = new();
    public List<Batch> AvailableBatches { get; set; } = new();
}

public class SubjectDiagnosticStat
{
    public string SubjectName { get; set; } = string.Empty;
    public int TotalQuestions { get; set; }
    public int TotalAnswersSubmitted { get; set; }
    public int CorrectAnswersCount { get; set; }
    public decimal AccuracyPercentage { get; set; }
    public decimal AverageMarks { get; set; }
}

public class TopicDiagnosticStat
{
    public string SubjectName { get; set; } = string.Empty;
    public string TopicName { get; set; } = string.Empty;
    public int TotalQuestions { get; set; }
    public int TotalAnswersSubmitted { get; set; }
    public int CorrectAnswersCount { get; set; }
    public decimal AccuracyPercentage { get; set; }
    public bool RequiresRemedialFocus => AccuracyPercentage < 50;
}

public class DifficultyDiagnosticStat
{
    public string DifficultyLevel { get; set; } = string.Empty;
    public int QuestionCount { get; set; }
    public int TotalAnswersSubmitted { get; set; }
    public int CorrectAnswersCount { get; set; }
    public decimal AccuracyPercentage { get; set; }
}
