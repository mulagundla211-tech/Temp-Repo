using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnlineExamPlatform.Tests.Helpers;
using OnlineExamPlatform.Web.Models;
using OnlineExamPlatform.Web.Services;

namespace OnlineExamPlatform.Tests.Services;

public class GradingServiceTests
{
    // ── MCQ ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GradeAndSubmit_Mcq_CorrectOption_AwardsFullMarks()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);

        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        var question = Builders.McqQuestion(exam.Id, marks: 4);
        ctx.Questions.Add(question);

        var correctOption = Builders.Option(question.Id, "A", "Four", isCorrect: true, order: 1);
        var wrongOption   = Builders.Option(question.Id, "B", "Five", isCorrect: false, order: 2);
        ctx.QuestionOptions.AddRange(correctOption, wrongOption);

        var attempt = Builders.Attempt(exam.Id, student.Id);
        ctx.ExamAttempts.Add(attempt);

        var answer = Builders.Answer(attempt.Id, question.Id, selectedOptionId: correctOption.Id);
        ctx.StudentAnswers.Add(answer);

        await ctx.SaveChangesAsync();

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attempt.Id);

        var saved = await ctx.StudentAnswers.FindAsync(answer.Id);
        Assert.True(saved!.IsCorrect);
        Assert.Equal(4, saved.MarksObtained);
    }

    [Fact]
    public async Task GradeAndSubmit_Mcq_WrongOption_AwardsZeroMarks()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);

        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        var question = Builders.McqQuestion(exam.Id, marks: 4);
        ctx.Questions.Add(question);

        var correctOption = Builders.Option(question.Id, "A", "Four",  isCorrect: true,  order: 1);
        var wrongOption   = Builders.Option(question.Id, "B", "Three", isCorrect: false, order: 2);
        ctx.QuestionOptions.AddRange(correctOption, wrongOption);

        var attempt = Builders.Attempt(exam.Id, student.Id);
        ctx.ExamAttempts.Add(attempt);

        var answer = Builders.Answer(attempt.Id, question.Id, selectedOptionId: wrongOption.Id);
        ctx.StudentAnswers.Add(answer);

        await ctx.SaveChangesAsync();

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attempt.Id);

        var saved = await ctx.StudentAnswers.FindAsync(answer.Id);
        Assert.False(saved!.IsCorrect);
        Assert.Equal(0, saved.MarksObtained);
    }

    [Fact]
    public async Task GradeAndSubmit_Mcq_NoOptionSelected_AwardsZeroMarks()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);

        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        var question = Builders.McqQuestion(exam.Id, marks: 4);
        ctx.Questions.Add(question);
        ctx.QuestionOptions.Add(Builders.Option(question.Id, "A", "Four", isCorrect: true, order: 1));

        var attempt = Builders.Attempt(exam.Id, student.Id);
        ctx.ExamAttempts.Add(attempt);

        var answer = Builders.Answer(attempt.Id, question.Id, selectedOptionId: null);
        ctx.StudentAnswers.Add(answer);

        await ctx.SaveChangesAsync();

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attempt.Id);

        var saved = await ctx.StudentAnswers.FindAsync(answer.Id);
        Assert.False(saved!.IsCorrect);
        Assert.Equal(0, saved.MarksObtained);
    }

    // ── TrueFalse ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GradeAndSubmit_TrueFalse_CorrectAnswer_AwardsFullMarks()
    {
        using var ctx = DbContextFactory.Create();
        var (attemptId, answerId) = await SeedTrueFalseAttempt(ctx, studentAnswer: "True", correctAnswer: "True", marks: 2);

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attemptId);

        var saved = await ctx.StudentAnswers.FindAsync(answerId);
        Assert.True(saved!.IsCorrect);
        Assert.Equal(2, saved.MarksObtained);
    }

    [Fact]
    public async Task GradeAndSubmit_TrueFalse_WrongAnswer_AwardsZeroMarks()
    {
        using var ctx = DbContextFactory.Create();
        var (attemptId, answerId) = await SeedTrueFalseAttempt(ctx, studentAnswer: "False", correctAnswer: "True", marks: 2);

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attemptId);

        var saved = await ctx.StudentAnswers.FindAsync(answerId);
        Assert.False(saved!.IsCorrect);
        Assert.Equal(0, saved.MarksObtained);
    }

    [Fact]
    public async Task GradeAndSubmit_TrueFalse_CaseInsensitive_StillCorrect()
    {
        using var ctx = DbContextFactory.Create();
        var (attemptId, answerId) = await SeedTrueFalseAttempt(ctx, studentAnswer: "true", correctAnswer: "True", marks: 1);

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attemptId);

        var saved = await ctx.StudentAnswers.FindAsync(answerId);
        Assert.True(saved!.IsCorrect);
    }

    [Fact]
    public async Task GradeAndSubmit_TrueFalse_EmptyAnswer_AwardsZeroMarks()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);

        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        var question = Builders.TrueFalseQuestion(exam.Id, marks: 1);
        ctx.Questions.Add(question);
        ctx.AnswerKeys.Add(Builders.AnswerKey(question.Id, "True"));

        var attempt = Builders.Attempt(exam.Id, student.Id);
        ctx.ExamAttempts.Add(attempt);

        var answer = Builders.Answer(attempt.Id, question.Id, answerText: "");
        ctx.StudentAnswers.Add(answer);

        await ctx.SaveChangesAsync();

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attempt.Id);

        var saved = await ctx.StudentAnswers.FindAsync(answer.Id);
        Assert.False(saved!.IsCorrect);
        Assert.Equal(0, saved.MarksObtained);
    }

    // ── FillInTheBlank ────────────────────────────────────────────────────────

    [Fact]
    public async Task GradeAndSubmit_FillInTheBlank_CorrectAnswer_AwardsFullMarks()
    {
        using var ctx = DbContextFactory.Create();
        var (attemptId, answerId) = await SeedFillInBlankAttempt(ctx, studentAnswer: "Paris", correctAnswer: "Paris", marks: 3);

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attemptId);

        var saved = await ctx.StudentAnswers.FindAsync(answerId);
        Assert.True(saved!.IsCorrect);
        Assert.Equal(3, saved.MarksObtained);
    }

    [Fact]
    public async Task GradeAndSubmit_FillInTheBlank_CaseInsensitiveAndTrimmed_StillCorrect()
    {
        using var ctx = DbContextFactory.Create();
        var (attemptId, answerId) = await SeedFillInBlankAttempt(ctx, studentAnswer: "  paris  ", correctAnswer: "Paris", marks: 3);

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attemptId);

        var saved = await ctx.StudentAnswers.FindAsync(answerId);
        Assert.True(saved!.IsCorrect);
    }

    [Fact]
    public async Task GradeAndSubmit_FillInTheBlank_WrongAnswer_AwardsZeroMarks()
    {
        using var ctx = DbContextFactory.Create();
        var (attemptId, answerId) = await SeedFillInBlankAttempt(ctx, studentAnswer: "London", correctAnswer: "Paris", marks: 3);

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attemptId);

        var saved = await ctx.StudentAnswers.FindAsync(answerId);
        Assert.False(saved!.IsCorrect);
        Assert.Equal(0, saved.MarksObtained);
    }

    // ── Subjective ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GradeAndSubmit_Subjective_LeavesGradeNull()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);

        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        var question = Builders.SubjectiveQuestion(exam.Id, marks: 5);
        ctx.Questions.Add(question);

        var attempt = Builders.Attempt(exam.Id, student.Id);
        ctx.ExamAttempts.Add(attempt);

        var answer = Builders.Answer(attempt.Id, question.Id, answerText: "Long answer here.");
        ctx.StudentAnswers.Add(answer);

        await ctx.SaveChangesAsync();

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attempt.Id);

        var saved = await ctx.StudentAnswers.FindAsync(answer.Id);
        Assert.Null(saved!.IsCorrect);
        Assert.Null(saved.MarksObtained);
    }

    // ── TotalScore & Attempt State ────────────────────────────────────────────

    [Fact]
    public async Task GradeAndSubmit_MixedTypes_TotalScoreEqualsSumOfMarksObtained()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);

        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        // MCQ — 4 marks (correct)
        var mcq = Builders.McqQuestion(exam.Id, marks: 4, displayOrder: 1);
        ctx.Questions.Add(mcq);
        var correctOpt = Builders.Option(mcq.Id, "A", "Right", isCorrect: true,  order: 1);
        var wrongOpt   = Builders.Option(mcq.Id, "B", "Wrong", isCorrect: false, order: 2);
        ctx.QuestionOptions.AddRange(correctOpt, wrongOpt);

        // TrueFalse — 2 marks (correct)
        var tf = Builders.TrueFalseQuestion(exam.Id, marks: 2, displayOrder: 2);
        ctx.Questions.Add(tf);
        ctx.AnswerKeys.Add(Builders.AnswerKey(tf.Id, "True"));

        // FillInBlank — 3 marks (wrong)
        var fib = Builders.FillInTheBlankQuestion(exam.Id, marks: 3, displayOrder: 3);
        ctx.Questions.Add(fib);
        ctx.AnswerKeys.Add(Builders.AnswerKey(fib.Id, "Paris"));

        // Subjective — 5 marks (not graded)
        var subj = Builders.SubjectiveQuestion(exam.Id, marks: 5, displayOrder: 4);
        ctx.Questions.Add(subj);

        var attempt = Builders.Attempt(exam.Id, student.Id);
        ctx.ExamAttempts.Add(attempt);

        ctx.StudentAnswers.AddRange(
            Builders.Answer(attempt.Id, mcq.Id,  selectedOptionId: correctOpt.Id),
            Builders.Answer(attempt.Id, tf.Id,   answerText: "True"),
            Builders.Answer(attempt.Id, fib.Id,  answerText: "London"),
            Builders.Answer(attempt.Id, subj.Id, answerText: "Essay text")
        );

        await ctx.SaveChangesAsync();

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attempt.Id);

        // MCQ=4, TF=2, FIB=0, Subjective=null → total = 6
        var saved = await ctx.ExamAttempts.FindAsync(attempt.Id);
        Assert.Equal(6, saved!.TotalScore);
    }

    [Fact]
    public async Task GradeAndSubmit_SetsAttemptStatusToSubmitted()
    {
        using var ctx = DbContextFactory.Create();
        var (attemptId, _) = await SeedTrueFalseAttempt(ctx, "True", "True", 1);

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attemptId);

        var attempt = await ctx.ExamAttempts.FindAsync(attemptId);
        Assert.Equal(AttemptStatus.Submitted, attempt!.Status);
    }

    [Fact]
    public async Task GradeAndSubmit_SetsFinishedAt()
    {
        using var ctx = DbContextFactory.Create();
        var (attemptId, _) = await SeedTrueFalseAttempt(ctx, "True", "True", 1);

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attemptId);

        var attempt = await ctx.ExamAttempts.FindAsync(attemptId);
        Assert.NotNull(attempt!.FinishedAt);
    }

    [Fact]
    public async Task GradeAndSubmit_AttemptNotFound_DoesNotThrow()
    {
        using var ctx = DbContextFactory.Create();
        var service = new GradingService(ctx, NullLogger<GradingService>.Instance);

        // Should silently return without throwing
        var exception = await Record.ExceptionAsync(() => service.GradeAndSubmitAsync(Guid.NewGuid()));
        Assert.Null(exception);
    }

    // Unattempted penalty.
    //
    // The penalty is opt-in per question (HasNegativeMarksIfUnattempted), NOT driven
    // by the question type. These tests pin that rule down so a blank Numerical or
    // FillInTheBlank answer stays neutral by default.

    [Theory]
    [InlineData(QuestionType.Numerical)]
    [InlineData(QuestionType.FillInTheBlank)]
    [InlineData(QuestionType.TrueFalse)]
    [InlineData(QuestionType.MCQ)]
    public async Task GradeAndSubmit_Unattempted_WithoutOptIn_ScoresZeroNotNegative(QuestionType type)
    {
        using var ctx = DbContextFactory.Create();
        var (attemptId, answerId) = await SeedUnattemptedAttempt(ctx, type, optIn: false, unattemptedPenalty: 0m);

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attemptId);

        var saved = await ctx.StudentAnswers.FindAsync(answerId);
        Assert.False(saved!.IsCorrect);
        Assert.Equal(0m, saved.MarksObtained);

        var attempt = await ctx.ExamAttempts.FindAsync(attemptId);
        Assert.Equal(0m, attempt!.TotalScore);
    }

    [Theory]
    [InlineData(QuestionType.Numerical)]
    [InlineData(QuestionType.FillInTheBlank)]
    [InlineData(QuestionType.TrueFalse)]
    [InlineData(QuestionType.MCQ)]
    public async Task GradeAndSubmit_Unattempted_WithOptIn_AppliesNegativePenalty(QuestionType type)
    {
        using var ctx = DbContextFactory.Create();
        var (attemptId, answerId) = await SeedUnattemptedAttempt(ctx, type, optIn: true, unattemptedPenalty: 0.5m);

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attemptId);

        var saved = await ctx.StudentAnswers.FindAsync(answerId);
        Assert.False(saved!.IsCorrect);
        Assert.Equal(-0.5m, saved.MarksObtained);

        var attempt = await ctx.ExamAttempts.FindAsync(attemptId);
        Assert.Equal(-0.5m, attempt!.TotalScore);
    }

    [Fact]
    public async Task GradeAndSubmit_Unattempted_Subjective_NeverIncursPenalty()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);

        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        // Opted in, but Subjective questions are skipped before the penalty is applied.
        var question = Builders.SubjectiveQuestion(exam.Id, marks: 5);
        question.HasNegativeMarksIfUnattempted = true;
        question.NegativeMarksIfUnattempted = 2m;
        ctx.Questions.Add(question);

        var attempt = Builders.Attempt(exam.Id, student.Id);
        ctx.ExamAttempts.Add(attempt);

        var answer = Builders.Answer(attempt.Id, question.Id, answerText: "");
        ctx.StudentAnswers.Add(answer);

        await ctx.SaveChangesAsync();

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attempt.Id);

        var saved = await ctx.StudentAnswers.FindAsync(answer.Id);
        Assert.Null(saved!.IsCorrect);
        Assert.Null(saved.MarksObtained);

        var savedAttempt = await ctx.ExamAttempts.FindAsync(attempt.Id);
        Assert.Equal(0m, savedAttempt!.TotalScore);
    }

    [Fact]
    public async Task GradeAndSubmit_QuestionNeverOpened_StillIncursOptedInPenalty()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);

        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        // No StudentAnswer row at all - the student never opened this question.
        var question = Builders.NumericalQuestion(exam.Id, marks: 4);
        question.HasNegativeMarksIfUnattempted = true;
        question.NegativeMarksIfUnattempted = 1m;
        ctx.Questions.Add(question);
        ctx.AnswerKeys.Add(Builders.AnswerKey(question.Id, "3.14"));

        var attempt = Builders.Attempt(exam.Id, student.Id);
        ctx.ExamAttempts.Add(attempt);

        await ctx.SaveChangesAsync();

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attempt.Id);

        var savedAttempt = await ctx.ExamAttempts.FindAsync(attempt.Id);
        Assert.Equal(-1m, savedAttempt!.TotalScore);
    }

    // Attempted-detection consistency.
    //
    // Which column carries the response is decided client-side (exam-timer.js sniffs
    // the radio value for a GUID), so the option-rendered types must accept either
    // signal. These tests cover the mismatch cases the old MCQ/else split got wrong —
    // most importantly an option-backed TrueFalse answer, which was scored as
    // unattempted and could take the unattempted penalty despite being answered.

    [Fact]
    public async Task GradeAndSubmit_TrueFalse_AnsweredViaSelectedOption_IsNotTreatedAsUnattempted()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);

        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        // Opted into the unattempted penalty so a misdetection shows up as a negative
        // score rather than a silent zero.
        var question = Builders.TrueFalseQuestion(exam.Id, marks: 4);
        question.HasNegativeMarksIfUnattempted = true;
        question.NegativeMarksIfUnattempted = 1m;
        ctx.Questions.Add(question);
        ctx.AnswerKeys.Add(Builders.AnswerKey(question.Id, "True"));

        var trueOption = Builders.Option(question.Id, "A", "True", isCorrect: true, order: 1);
        var falseOption = Builders.Option(question.Id, "B", "False", isCorrect: false, order: 2);
        ctx.QuestionOptions.AddRange(trueOption, falseOption);

        var attempt = Builders.Attempt(exam.Id, student.Id);
        ctx.ExamAttempts.Add(attempt);

        // Option id posted instead of the literal "True"/"False" text.
        var answer = Builders.Answer(attempt.Id, question.Id, selectedOptionId: trueOption.Id, answerText: null);
        ctx.StudentAnswers.Add(answer);

        await ctx.SaveChangesAsync();

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attempt.Id);

        var saved = await ctx.StudentAnswers.FindAsync(answer.Id);
        Assert.True(saved!.IsCorrect);
        Assert.Equal(4, saved.MarksObtained);
    }

    [Fact]
    public async Task GradeAndSubmit_TrueFalse_WrongOptionSelected_ScoresZeroNotUnattemptedPenalty()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);

        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        var question = Builders.TrueFalseQuestion(exam.Id, marks: 4);
        question.HasNegativeMarksIfUnattempted = true;
        question.NegativeMarksIfUnattempted = 1m;
        ctx.Questions.Add(question);
        ctx.AnswerKeys.Add(Builders.AnswerKey(question.Id, "True"));

        var trueOption = Builders.Option(question.Id, "A", "True", isCorrect: true, order: 1);
        var falseOption = Builders.Option(question.Id, "B", "False", isCorrect: false, order: 2);
        ctx.QuestionOptions.AddRange(trueOption, falseOption);

        var attempt = Builders.Attempt(exam.Id, student.Id);
        ctx.ExamAttempts.Add(attempt);

        var answer = Builders.Answer(attempt.Id, question.Id, selectedOptionId: falseOption.Id, answerText: null);
        ctx.StudentAnswers.Add(answer);

        await ctx.SaveChangesAsync();

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attempt.Id);

        var saved = await ctx.StudentAnswers.FindAsync(answer.Id);
        Assert.False(saved!.IsCorrect);
        // Wrong answer, not unattempted: no negative marking configured, so 0 — the
        // unattempted penalty of -1 must NOT apply.
        Assert.Equal(0m, saved.MarksObtained);
    }

    [Fact]
    public async Task GradeAndSubmit_Mcq_AnsweredViaAnswerText_IsNotTreatedAsUnattempted()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);

        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        var question = Builders.McqQuestion(exam.Id, marks: 4);
        question.HasNegativeMarksIfUnattempted = true;
        question.NegativeMarksIfUnattempted = 1m;
        ctx.Questions.Add(question);

        var correct = Builders.Option(question.Id, "A", "Four", isCorrect: true, order: 1);
        ctx.QuestionOptions.Add(correct);

        var attempt = Builders.Attempt(exam.Id, student.Id);
        ctx.ExamAttempts.Add(attempt);

        // Option id arrived as text rather than in SelectedOptionId.
        var answer = Builders.Answer(attempt.Id, question.Id, selectedOptionId: null, answerText: correct.Id.ToString());
        ctx.StudentAnswers.Add(answer);

        await ctx.SaveChangesAsync();

        await new GradingService(ctx, NullLogger<GradingService>.Instance).GradeAndSubmitAsync(attempt.Id);

        var saved = await ctx.StudentAnswers.FindAsync(answer.Id);
        // Counted as attempted, so the unattempted penalty must not apply.
        Assert.Equal(0m, saved!.MarksObtained);
    }

    [Fact]
    public async Task GradeAndSubmit_TrueFalse_MissingAnswerKey_DoesNotThrow()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);

        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        // No answer key at all — grading must degrade to "not correct", never throw.
        var question = Builders.TrueFalseQuestion(exam.Id, marks: 2);
        ctx.Questions.Add(question);

        var attempt = Builders.Attempt(exam.Id, student.Id);
        ctx.ExamAttempts.Add(attempt);

        var answer = Builders.Answer(attempt.Id, question.Id, answerText: "True");
        ctx.StudentAnswers.Add(answer);

        await ctx.SaveChangesAsync();

        var service = new GradingService(ctx, NullLogger<GradingService>.Instance);
        var exception = await Record.ExceptionAsync(() => service.GradeAndSubmitAsync(attempt.Id));
        Assert.Null(exception);

        var saved = await ctx.StudentAnswers.FindAsync(answer.Id);
        Assert.False(saved!.IsCorrect);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<(Guid attemptId, Guid answerId)> SeedTrueFalseAttempt(
        Web.Data.ApplicationDbContext ctx,
        string studentAnswer, string correctAnswer, int marks)
    {
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);

        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        var question = Builders.TrueFalseQuestion(exam.Id, marks);
        ctx.Questions.Add(question);
        ctx.AnswerKeys.Add(Builders.AnswerKey(question.Id, correctAnswer));

        var attempt = Builders.Attempt(exam.Id, student.Id);
        ctx.ExamAttempts.Add(attempt);

        var answer = Builders.Answer(attempt.Id, question.Id, answerText: studentAnswer);
        ctx.StudentAnswers.Add(answer);

        await ctx.SaveChangesAsync();
        return (attempt.Id, answer.Id);
    }

    private static async Task<(Guid attemptId, Guid answerId)> SeedFillInBlankAttempt(
        Web.Data.ApplicationDbContext ctx,
        string studentAnswer, string correctAnswer, int marks)
    {
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);

        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        var question = Builders.FillInTheBlankQuestion(exam.Id, marks);
        ctx.Questions.Add(question);
        ctx.AnswerKeys.Add(Builders.AnswerKey(question.Id, correctAnswer));

        var attempt = Builders.Attempt(exam.Id, student.Id);
        ctx.ExamAttempts.Add(attempt);

        var answer = Builders.Answer(attempt.Id, question.Id, answerText: studentAnswer);
        ctx.StudentAnswers.Add(answer);

        await ctx.SaveChangesAsync();
        return (attempt.Id, answer.Id);
    }

    // Seeds an attempt whose single question is left blank (no option selected and
    // no answer text), so grading takes the unattempted branch.
    private static async Task<(Guid attemptId, Guid answerId)> SeedUnattemptedAttempt(
        Web.Data.ApplicationDbContext ctx,
        QuestionType type, bool optIn, decimal unattemptedPenalty)
    {
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);

        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        var question = type switch
        {
            QuestionType.Numerical => Builders.NumericalQuestion(exam.Id, marks: 4),
            QuestionType.FillInTheBlank => Builders.FillInTheBlankQuestion(exam.Id, marks: 4),
            QuestionType.TrueFalse => Builders.TrueFalseQuestion(exam.Id, marks: 4),
            QuestionType.MCQ => Builders.McqQuestion(exam.Id, marks: 4),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported question type.")
        };
        question.HasNegativeMarksIfUnattempted = optIn;
        question.NegativeMarksIfUnattempted = unattemptedPenalty;
        ctx.Questions.Add(question);

        if (type == QuestionType.MCQ)
        {
            ctx.QuestionOptions.AddRange(
                Builders.Option(question.Id, "A", "Four", isCorrect: true, order: 1),
                Builders.Option(question.Id, "B", "Five", isCorrect: false, order: 2));
        }
        else
        {
            ctx.AnswerKeys.Add(Builders.AnswerKey(question.Id, "3.14"));
        }

        var attempt = Builders.Attempt(exam.Id, student.Id);
        ctx.ExamAttempts.Add(attempt);

        // Row exists (the student visited the question) but nothing was entered.
        var answer = Builders.Answer(attempt.Id, question.Id, selectedOptionId: null, answerText: null);
        ctx.StudentAnswers.Add(answer);

        await ctx.SaveChangesAsync();
        return (attempt.Id, answer.Id);
    }
}
