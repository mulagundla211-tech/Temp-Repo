using OnlineExamPlatform.Web.Models;

namespace OnlineExamPlatform.Web.Models.ViewModels;

/// <summary>
/// Everything the take-exam page needs, and deliberately nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The page used to be handed the <see cref="Exam"/> entity graph directly. That was
/// risky in two ways. First, the graph was loaded <c>AsNoTracking</c> and then mutated
/// (collections reassigned to apply ordering and shuffling) — correct only for as long
/// as nobody added a save or switched the query to tracking. Second, every
/// <see cref="QuestionOption"/> carried its <c>IsCorrect</c> flag into the view, so the
/// answers were one careless <c>@opt.IsCorrect</c> away from being served to students.
/// </para>
/// <para>
/// These DTOs fix both structurally: they are throwaway objects that no DbContext knows
/// about, so reordering them is safe by construction, and there is simply no member on
/// <see cref="TakeExamOption"/> that could expose the correct answer.
/// </para>
/// <para>
/// Member names intentionally mirror the entity members the view already used, so the
/// view binds to the DTO without a rewrite.
/// </para>
/// </remarks>
public class TakeExamViewModel
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public int DurationMinutes { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public int TotalMarks { get; set; }
    public string? Subjects { get; set; }

    // Per-student randomisation settings, applied via ExamService.ApplyShuffle.
    public bool ShuffleQuestions { get; set; }
    public bool ShuffleOptions { get; set; }

    // Anti-cheating settings consumed by the client proctoring module.
    public bool EnableProctoring { get; set; }
    public bool RequireFullscreen { get; set; }
    public int MaxProctoringWarnings { get; set; }

    public List<TakeExamQuestion> Questions { get; set; } = new();
}

public class TakeExamQuestion
{
    public Guid Id { get; set; }
    public int DisplayOrder { get; set; }
    public string? Subject { get; set; }
    public QuestionType QuestionType { get; set; }
    public int Marks { get; set; }
    public string? QuestionText { get; set; }
    public string? ImagePath { get; set; }

    public List<TakeExamOption> Options { get; set; } = new();
}

/// <summary>
/// A selectable option as the student sees it.
/// </summary>
/// <remarks>
/// There is deliberately no <c>IsCorrect</c> member here. Adding one would make the
/// correct answer reachable from the exam page, so any attempt to reintroduce it should
/// be treated as a security regression rather than a convenience.
/// </remarks>
public class TakeExamOption
{
    public Guid Id { get; set; }
    public string OptionLabel { get; set; } = string.Empty;
    public string OptionText { get; set; } = string.Empty;
    public string? ImagePath { get; set; }
    public int DisplayOrder { get; set; }
}
