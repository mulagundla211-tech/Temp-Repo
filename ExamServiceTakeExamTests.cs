using Microsoft.Extensions.Logging.Abstractions;
using OnlineExamPlatform.Tests.Helpers;
using OnlineExamPlatform.Web.Data;
using OnlineExamPlatform.Web.Models;
using OnlineExamPlatform.Web.Models.ViewModels;
using OnlineExamPlatform.Web.Services;

namespace OnlineExamPlatform.Tests.Services;

/// <summary>
/// Covers the take-exam projection: that it orders correctly in the query rather than
/// by mutating a loaded graph, and that it structurally cannot carry correct answers
/// to the student.
/// </summary>
public class ExamServiceTakeExamTests
{
    private static ExamService CreateService(ApplicationDbContext ctx) =>
        new(ctx, NullLogger<ExamService>.Instance);

    private static async Task<Exam> SeedExamAsync(ApplicationDbContext ctx)
    {
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);

        var exam = Builders.Exam(admin.Id);
        exam.ShuffleQuestions = false;
        exam.ShuffleOptions = false;
        exam.EnableProctoring = true;
        exam.RequireFullscreen = true;
        exam.MaxProctoringWarnings = 3;
        ctx.Exams.Add(exam);

        // Added deliberately out of order so ordering cannot pass by insertion luck.
        var third = Builders.McqQuestion(exam.Id, marks: 1, displayOrder: 3);
        var first = Builders.McqQuestion(exam.Id, marks: 2, displayOrder: 1);
        var second = Builders.McqQuestion(exam.Id, marks: 3, displayOrder: 2);
        ctx.Questions.AddRange(third, first, second);

        ctx.QuestionOptions.AddRange(
            Builders.Option(first.Id, "B", "Second", isCorrect: false, order: 2),
            Builders.Option(first.Id, "A", "First", isCorrect: true, order: 1),
            Builders.Option(first.Id, "C", "Third", isCorrect: false, order: 3));

        await ctx.SaveChangesAsync();
        return exam;
    }

    // ── Structural guarantee ────────────────────────────────────────────────────

    // The whole point of the DTO: there is no member through which the correct answer
    // could reach the exam page. If someone adds one, this fails loudly.
    [Fact]
    public void TakeExamOption_ExposesNoCorrectnessMember()
    {
        var memberNames = typeof(TakeExamOption)
            .GetProperties()
            .Select(p => p.Name)
            .ToList();

        Assert.DoesNotContain(memberNames,
            name => name.Contains("Correct", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TakeExamQuestion_ExposesNoAnswerKeyOrSolutionMember()
    {
        var memberNames = typeof(TakeExamQuestion)
            .GetProperties()
            .Select(p => p.Name)
            .ToList();

        Assert.DoesNotContain(memberNames,
            name => name.Contains("AnswerKey", StringComparison.OrdinalIgnoreCase)
                 || name.Contains("Solution", StringComparison.OrdinalIgnoreCase));
    }

    // ── Projection ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTakeExamAsync_OrdersQuestionsByDisplayOrder()
    {
        using var ctx = DbContextFactory.Create();
        var exam = await SeedExamAsync(ctx);

        var result = await CreateService(ctx).GetTakeExamAsync(exam.Id);

        Assert.NotNull(result);
        Assert.Equal(new[] { 1, 2, 3 }, result!.Questions.Select(q => q.DisplayOrder).ToArray());
    }

    [Fact]
    public async Task GetTakeExamAsync_OrdersOptionsByDisplayOrder()
    {
        using var ctx = DbContextFactory.Create();
        var exam = await SeedExamAsync(ctx);

        var result = await CreateService(ctx).GetTakeExamAsync(exam.Id);

        var withOptions = result!.Questions.Single(q => q.Options.Count > 0);
        Assert.Equal(new[] { "A", "B", "C" }, withOptions.Options.Select(o => o.OptionLabel).ToArray());
    }

    [Fact]
    public async Task GetTakeExamAsync_CarriesFieldsTheExamPageNeeds()
    {
        using var ctx = DbContextFactory.Create();
        var exam = await SeedExamAsync(ctx);

        var result = await CreateService(ctx).GetTakeExamAsync(exam.Id);

        Assert.NotNull(result);
        Assert.Equal(exam.Title, result!.Title);
        Assert.Equal(exam.DurationMinutes, result.DurationMinutes);
        Assert.Equal(exam.StartDate, result.StartDate);
        Assert.Equal(exam.EndDate, result.EndDate);
        Assert.True(result.EnableProctoring);
        Assert.True(result.RequireFullscreen);
        Assert.Equal(3, result.MaxProctoringWarnings);
    }

    [Fact]
    public async Task GetTakeExamAsync_UnknownExam_ReturnsNull()
    {
        using var ctx = DbContextFactory.Create();

        var result = await CreateService(ctx).GetTakeExamAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    // The projection must not leave tracked entities behind — the take-exam path is
    // strictly read-only and previously relied on AsNoTracking for that.
    [Fact]
    public async Task GetTakeExamAsync_TracksNothing()
    {
        using var ctx = DbContextFactory.Create();
        var exam = await SeedExamAsync(ctx);
        ctx.ChangeTracker.Clear();

        await CreateService(ctx).GetTakeExamAsync(exam.Id);

        Assert.Empty(ctx.ChangeTracker.Entries());
    }

    // ── Ordered includes on the entity loaders ──────────────────────────────────

    [Fact]
    public async Task GetExamForReviewAsync_OrdersQuestionsAndOptionsInTheQuery()
    {
        using var ctx = DbContextFactory.Create();
        var exam = await SeedExamAsync(ctx);
        ctx.ChangeTracker.Clear();

        var result = await CreateService(ctx).GetExamForReviewAsync(exam.Id);

        Assert.NotNull(result);
        Assert.Equal(new[] { 1, 2, 3 }, result!.Questions.Select(q => q.DisplayOrder).ToArray());

        var withOptions = result.Questions.Single(q => q.Options.Count > 0);
        Assert.Equal(new[] { "A", "B", "C" }, withOptions.Options.Select(o => o.OptionLabel).ToArray());
    }

    [Fact]
    public async Task GetExamByIdAsync_OrdersQuestionsAndOptionsInTheQuery()
    {
        using var ctx = DbContextFactory.Create();
        var exam = await SeedExamAsync(ctx);
        ctx.ChangeTracker.Clear();

        var result = await CreateService(ctx).GetExamByIdAsync(exam.Id);

        Assert.NotNull(result);
        Assert.Equal(new[] { 1, 2, 3 }, result!.Questions.Select(q => q.DisplayOrder).ToArray());
    }

    // ── Shuffle ─────────────────────────────────────────────────────────────────

    private static (TakeExamViewModel exam, List<TakeExamQuestion> questions) BuildShuffleFixture(
        bool shuffleQuestions = true, bool shuffleOptions = false, int questionCount = 12)
    {
        var exam = new TakeExamViewModel
        {
            Id = Guid.NewGuid(),
            ShuffleQuestions = shuffleQuestions,
            ShuffleOptions = shuffleOptions
        };

        var questions = Enumerable.Range(1, questionCount)
            .Select(i => new TakeExamQuestion
            {
                Id = Guid.NewGuid(),
                DisplayOrder = i,
                Subject = "Maths",
                QuestionType = QuestionType.MCQ,
                Options = Enumerable.Range(1, 4)
                    .Select(o => new TakeExamOption
                    {
                        Id = Guid.NewGuid(),
                        OptionLabel = ((char)('A' + o - 1)).ToString(),
                        DisplayOrder = o
                    }).ToList()
            })
            .ToList();

        return (exam, questions);
    }

    [Fact]
    public void ApplyShuffle_SameSeed_ProducesSameOrder()
    {
        var (exam, questions) = BuildShuffleFixture();
        var ids = questions.Select(q => q.Id).ToList();

        var first = ExamService.ApplyShuffle(questions.ToList(), exam, seed: 12345);
        var second = ExamService.ApplyShuffle(
            ids.Select(id => new TakeExamQuestion { Id = id, Subject = "Maths", QuestionType = QuestionType.MCQ }).ToList(),
            exam, seed: 12345);

        Assert.Equal(first.Select(q => q.Id), second.Select(q => q.Id));
    }

    [Fact]
    public void ApplyShuffle_ZeroSeed_LeavesOrderUntouched()
    {
        var (exam, questions) = BuildShuffleFixture();
        var expected = questions.Select(q => q.Id).ToList();

        var result = ExamService.ApplyShuffle(questions, exam, seed: 0);

        Assert.Equal(expected, result.Select(q => q.Id));
    }

    [Fact]
    public void ApplyShuffle_ShuffleDisabled_LeavesOrderUntouched()
    {
        var (exam, questions) = BuildShuffleFixture(shuffleQuestions: false, shuffleOptions: false);
        var expected = questions.Select(q => q.Id).ToList();

        var result = ExamService.ApplyShuffle(questions, exam, seed: 999);

        Assert.Equal(expected, result.Select(q => q.Id));
    }

    [Fact]
    public void ApplyShuffle_KeepsSubjectsGroupedTogether()
    {
        var exam = new TakeExamViewModel { ShuffleQuestions = true };
        var questions = new List<TakeExamQuestion>();
        foreach (var subject in new[] { "Physics", "Chemistry" })
        {
            for (int i = 0; i < 5; i++)
            {
                questions.Add(new TakeExamQuestion
                {
                    Id = Guid.NewGuid(),
                    Subject = subject,
                    QuestionType = QuestionType.MCQ
                });
            }
        }

        var result = ExamService.ApplyShuffle(questions, exam, seed: 777);

        // Each subject must still occupy one contiguous block.
        var subjectRuns = result.Select(q => q.Subject)
            .Aggregate(new List<string?>(), (acc, s) =>
            {
                if (acc.Count == 0 || acc[^1] != s) acc.Add(s);
                return acc;
            });

        Assert.Equal(2, subjectRuns.Count);
    }

    [Fact]
    public void ApplyShuffle_DoesNotLoseOrDuplicateQuestions()
    {
        var (exam, questions) = BuildShuffleFixture(shuffleQuestions: true, shuffleOptions: true);
        var expected = questions.Select(q => q.Id).OrderBy(id => id).ToList();

        var result = ExamService.ApplyShuffle(questions, exam, seed: 42);

        Assert.Equal(expected, result.Select(q => q.Id).OrderBy(id => id));
    }

    [Fact]
    public void ApplyShuffle_ShuffleOptions_KeepsEveryOption()
    {
        var (exam, questions) = BuildShuffleFixture(shuffleQuestions: false, shuffleOptions: true, questionCount: 1);
        var expected = questions[0].Options.Select(o => o.Id).OrderBy(id => id).ToList();

        var result = ExamService.ApplyShuffle(questions, exam, seed: 42);

        Assert.Equal(expected, result[0].Options.Select(o => o.Id).OrderBy(id => id));
    }
}
