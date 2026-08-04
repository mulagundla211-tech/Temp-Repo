using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OnlineExamPlatform.Tests.Helpers;
using OnlineExamPlatform.Web.Data;
using OnlineExamPlatform.Web.Models;
using OnlineExamPlatform.Web.Services;

namespace OnlineExamPlatform.Tests.Services;

/// <summary>
/// Covers the invalidation contract that keeps the cached grading graph honest:
/// any write to a Question, QuestionOption or AnswerKey must evict the affected
/// exam, regardless of which code path performed it.
/// </summary>
public class GradingGraphCacheInvalidatorTests
{
    private static (ApplicationDbContext ctx, IMemoryCache cache) CreateWithInterceptor()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var interceptor = new GradingGraphCacheInvalidator(
            cache, NullLogger<GradingGraphCacheInvalidator>.Instance);
        return (DbContextFactory.Create(interceptor), cache);
    }

    private static bool IsCached(IMemoryCache cache, Guid examId) =>
        cache.TryGetValue(ExamService.GradingGraphCacheKey(examId), out _);

    private static void Prime(IMemoryCache cache, Guid examId) =>
        cache.Set(ExamService.GradingGraphCacheKey(examId), new List<Question>());

    // The headline case: editing an answer key does not change the exam's total marks,
    // so nothing would have called RecalculateTotalMarksAsync — yet the cached graph
    // must still be dropped or students are graded against the old key.
    [Fact]
    public async Task EditingAnswerKey_EvictsCachedGraph()
    {
        var (ctx, cache) = CreateWithInterceptor();
        using var _ = ctx;

        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        var question = Builders.TrueFalseQuestion(exam.Id);
        ctx.Questions.Add(question);
        var key = Builders.AnswerKey(question.Id, "True");
        ctx.AnswerKeys.Add(key);
        await ctx.SaveChangesAsync();

        Prime(cache, exam.Id);
        Assert.True(IsCached(cache, exam.Id));

        key.CorrectAnswer = "False";
        await ctx.SaveChangesAsync();

        Assert.False(IsCached(cache, exam.Id));
    }

    // Flipping which option is correct also leaves TotalMarks untouched.
    [Fact]
    public async Task EditingOptionIsCorrect_EvictsCachedGraph()
    {
        var (ctx, cache) = CreateWithInterceptor();
        using var _ = ctx;

        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        var question = Builders.McqQuestion(exam.Id);
        ctx.Questions.Add(question);
        var optionA = Builders.Option(question.Id, "A", "Four", isCorrect: true, order: 1);
        var optionB = Builders.Option(question.Id, "B", "Five", isCorrect: false, order: 2);
        ctx.QuestionOptions.AddRange(optionA, optionB);
        await ctx.SaveChangesAsync();

        Prime(cache, exam.Id);

        optionA.IsCorrect = false;
        optionB.IsCorrect = true;
        await ctx.SaveChangesAsync();

        Assert.False(IsCached(cache, exam.Id));
    }

    [Fact]
    public async Task EditingQuestionText_EvictsCachedGraph()
    {
        var (ctx, cache) = CreateWithInterceptor();
        using var _ = ctx;

        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        var question = Builders.McqQuestion(exam.Id);
        ctx.Questions.Add(question);
        await ctx.SaveChangesAsync();

        Prime(cache, exam.Id);

        question.Solution = "Because 2 + 2 = 4.";
        await ctx.SaveChangesAsync();

        Assert.False(IsCached(cache, exam.Id));
    }

    [Fact]
    public async Task DeletingQuestion_EvictsCachedGraph()
    {
        var (ctx, cache) = CreateWithInterceptor();
        using var _ = ctx;

        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        var question = Builders.McqQuestion(exam.Id);
        ctx.Questions.Add(question);
        await ctx.SaveChangesAsync();

        Prime(cache, exam.Id);

        ctx.Questions.Remove(question);
        await ctx.SaveChangesAsync();

        Assert.False(IsCached(cache, exam.Id));
    }

    // Moving a question to the bank clears its ExamId. The exam that must be evicted is
    // the ORIGINAL one, which is only visible via the property's original value.
    [Fact]
    public async Task MovingQuestionOutOfExam_EvictsOriginalExam()
    {
        var (ctx, cache) = CreateWithInterceptor();
        using var _ = ctx;

        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        var question = Builders.McqQuestion(exam.Id);
        ctx.Questions.Add(question);
        await ctx.SaveChangesAsync();

        Prime(cache, exam.Id);

        question.ExamId = null;
        question.IsInBank = true;
        await ctx.SaveChangesAsync();

        Assert.False(IsCached(cache, exam.Id));
    }

    // Bank questions belong to no exam, so touching them must not blow away unrelated
    // cached graphs.
    [Fact]
    public async Task EditingBankQuestion_DoesNotEvictUnrelatedExam()
    {
        var (ctx, cache) = CreateWithInterceptor();
        using var _ = ctx;

        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        var bankQuestion = Builders.McqQuestion(exam.Id);
        bankQuestion.ExamId = null;
        bankQuestion.IsInBank = true;
        ctx.Questions.Add(bankQuestion);
        await ctx.SaveChangesAsync();

        Prime(cache, exam.Id);

        bankQuestion.Solution = "Updated bank solution.";
        await ctx.SaveChangesAsync();

        Assert.True(IsCached(cache, exam.Id));
    }

    // Writes unrelated to grading (an attempt being submitted) must leave the cache warm,
    // otherwise the submit burst the cache exists for would evict it on every student.
    [Fact]
    public async Task SavingUnrelatedEntity_DoesNotEvictCachedGraph()
    {
        var (ctx, cache) = CreateWithInterceptor();
        using var _ = ctx;

        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        var attempt = Builders.Attempt(exam.Id, student.Id);
        ctx.ExamAttempts.Add(attempt);
        await ctx.SaveChangesAsync();

        Prime(cache, exam.Id);

        attempt.Status = AttemptStatus.Submitted;
        attempt.TotalScore = 10;
        await ctx.SaveChangesAsync();

        Assert.True(IsCached(cache, exam.Id));
    }

    // End-to-end: grade, edit the key, grade again. The second attempt must be scored
    // against the NEW key rather than the cached copy of the old one.
    [Fact]
    public async Task GradingAfterAnswerKeyEdit_UsesUpdatedKey()
    {
        var (ctx, cache) = CreateWithInterceptor();
        using var _ = ctx;

        var admin = Builders.User("Admin");
        var first = Builders.User("Student");
        var second = Builders.User("Student");
        ctx.Users.AddRange(admin, first, second);

        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        var question = Builders.TrueFalseQuestion(exam.Id, marks: 5);
        ctx.Questions.Add(question);
        var key = Builders.AnswerKey(question.Id, "True");
        ctx.AnswerKeys.Add(key);

        var firstAttempt = Builders.Attempt(exam.Id, first.Id);
        ctx.ExamAttempts.Add(firstAttempt);
        var firstAnswer = Builders.Answer(firstAttempt.Id, question.Id, answerText: "True");
        ctx.StudentAnswers.Add(firstAnswer);
        await ctx.SaveChangesAsync();

        var grading = new GradingService(ctx, NullLogger<GradingService>.Instance, cache);

        // Populates the cache with the graph built from the original key.
        await grading.GradeAndSubmitAsync(firstAttempt.Id);
        Assert.True((await ctx.StudentAnswers.FindAsync(firstAnswer.Id))!.IsCorrect);

        // The examiner corrects the key.
        key.CorrectAnswer = "False";
        await ctx.SaveChangesAsync();

        var secondAttempt = Builders.Attempt(exam.Id, second.Id);
        ctx.ExamAttempts.Add(secondAttempt);
        var secondAnswer = Builders.Answer(secondAttempt.Id, question.Id, answerText: "True");
        ctx.StudentAnswers.Add(secondAnswer);
        await ctx.SaveChangesAsync();

        await grading.GradeAndSubmitAsync(secondAttempt.Id);

        // "True" is now wrong. Without eviction this would still be marked correct.
        var saved = await ctx.StudentAnswers.FindAsync(secondAnswer.Id);
        Assert.False(saved!.IsCorrect);
        Assert.Equal(0m, saved.MarksObtained);
    }

    // Characterisation test for the bug this interceptor exists to prevent. Identical to
    // the test above except the interceptor is NOT attached, which is how the app behaved
    // before: the corrected answer key is ignored and the student is graded against the
    // stale cached copy. If this ever starts failing, invalidation has become unconditional
    // somewhere else and the interceptor may be redundant.
    [Fact]
    public async Task WithoutInterceptor_GradingUsesStaleAnswerKey()
    {
        using var ctx = DbContextFactory.Create();
        var cache = new MemoryCache(new MemoryCacheOptions());

        var admin = Builders.User("Admin");
        var first = Builders.User("Student");
        var second = Builders.User("Student");
        ctx.Users.AddRange(admin, first, second);

        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        var question = Builders.TrueFalseQuestion(exam.Id, marks: 5);
        ctx.Questions.Add(question);
        var key = Builders.AnswerKey(question.Id, "True");
        ctx.AnswerKeys.Add(key);

        var firstAttempt = Builders.Attempt(exam.Id, first.Id);
        ctx.ExamAttempts.Add(firstAttempt);
        ctx.StudentAnswers.Add(Builders.Answer(firstAttempt.Id, question.Id, answerText: "True"));
        await ctx.SaveChangesAsync();

        var grading = new GradingService(ctx, NullLogger<GradingService>.Instance, cache);
        await grading.GradeAndSubmitAsync(firstAttempt.Id);

        key.CorrectAnswer = "False";
        await ctx.SaveChangesAsync();

        var secondAttempt = Builders.Attempt(exam.Id, second.Id);
        ctx.ExamAttempts.Add(secondAttempt);
        var secondAnswer = Builders.Answer(secondAttempt.Id, question.Id, answerText: "True");
        ctx.StudentAnswers.Add(secondAnswer);
        await ctx.SaveChangesAsync();

        await grading.GradeAndSubmitAsync(secondAttempt.Id);

        // Wrong on purpose: this documents the stale-cache behaviour the interceptor fixes.
        var saved = await ctx.StudentAnswers.FindAsync(secondAnswer.Id);
        Assert.True(saved!.IsCorrect);
        Assert.Equal(5, saved.MarksObtained);
    }
}
