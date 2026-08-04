using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnlineExamPlatform.Tests.Helpers;
using OnlineExamPlatform.Web.Models;
using OnlineExamPlatform.Web.Services;

namespace OnlineExamPlatform.Tests.Services;

public class ExamServiceTests
{
    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateExam_AssignsNewId()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        await ctx.SaveChangesAsync();

        var exam = Builders.Exam(admin.Id);
        var originalId = exam.Id;

        await new ExamService(ctx, NullLogger<ExamService>.Instance).CreateExamAsync(exam);

        // CreateExamAsync overwrites the Id with a new Guid
        Assert.NotEqual(Guid.Empty, exam.Id);
    }

    [Fact]
    public async Task CreateExam_PersistsToDatabase()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        await ctx.SaveChangesAsync();

        var exam = Builders.Exam(admin.Id, title: "Chemistry Mid-Term");
        await new ExamService(ctx, NullLogger<ExamService>.Instance).CreateExamAsync(exam);

        var stored = await ctx.Exams.FindAsync(exam.Id);
        Assert.NotNull(stored);
        Assert.Equal("Chemistry Mid-Term", stored.Title);
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateExam_SetsUpdatedAt()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var exam = Builders.Exam(admin.Id);
        exam.UpdatedAt = DateTime.UtcNow.AddDays(-10);
        ctx.Exams.Add(exam);
        await ctx.SaveChangesAsync();

        var before = exam.UpdatedAt;
        await new ExamService(ctx, NullLogger<ExamService>.Instance).UpdateExamAsync(exam);

        Assert.True(exam.UpdatedAt > before);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteExam_RemovesFromDatabase()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        await ctx.SaveChangesAsync();

        await new ExamService(ctx, NullLogger<ExamService>.Instance).DeleteExamAsync(exam.Id);

        Assert.Null(await ctx.Exams.FindAsync(exam.Id));
    }

    [Fact]
    public async Task DeleteExam_NonExistentId_DoesNotThrow()
    {
        using var ctx = DbContextFactory.Create();
        var exception = await Record.ExceptionAsync(() =>
            new ExamService(ctx, NullLogger<ExamService>.Instance).DeleteExamAsync(Guid.NewGuid()));
        Assert.Null(exception);
    }

    // ── AssignStudents ────────────────────────────────────────────────────────

    [Fact]
    public async Task AssignStudents_CreatesNewAssignments()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var s1 = Builders.User("Student");
        var s2 = Builders.User("Student");
        ctx.Users.AddRange(admin, s1, s2);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        await ctx.SaveChangesAsync();

        await new ExamService(ctx, NullLogger<ExamService>.Instance).AssignStudentsToExamAsync(exam.Id, new List<Guid> { s1.Id, s2.Id });

        var assignments = await ctx.ExamStudents
            .Where(es => es.ExamId == exam.Id)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
    }

    [Fact]
    public async Task AssignStudents_SkipsDuplicates()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        ctx.ExamStudents.Add(new ExamStudent
        {
            Id = Guid.NewGuid(),
            ExamId = exam.Id,
            StudentId = student.Id
        });
        await ctx.SaveChangesAsync();

        // Assign the same student again — should not create a duplicate
        await new ExamService(ctx, NullLogger<ExamService>.Instance).AssignStudentsToExamAsync(exam.Id, new List<Guid> { student.Id });

        var count = await ctx.ExamStudents.CountAsync(es => es.ExamId == exam.Id);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AssignStudents_EmptyList_NoAssignmentsCreated()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        await ctx.SaveChangesAsync();

        await new ExamService(ctx, NullLogger<ExamService>.Instance).AssignStudentsToExamAsync(exam.Id, new List<Guid>());

        var count = await ctx.ExamStudents.CountAsync(es => es.ExamId == exam.Id);
        Assert.Equal(0, count);
    }

    // AssignExamsToNewStudents

    // Seeds a batch with two exams plus one exam in a different batch, so tests can
    // verify scoping as well as assignment.
    private static async Task<(Guid batchId, Exam first, Exam second, Exam otherBatch)> SeedBatchExamsAsync(
        Web.Data.ApplicationDbContext ctx)
    {
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);

        var batchId = Guid.NewGuid();
        var first = Builders.Exam(admin.Id, "Batch exam 1");
        first.BatchId = batchId;
        var second = Builders.Exam(admin.Id, "Batch exam 2");
        second.BatchId = batchId;
        var otherBatch = Builders.Exam(admin.Id, "Other batch exam");
        otherBatch.BatchId = Guid.NewGuid();

        ctx.Exams.AddRange(first, second, otherBatch);
        await ctx.SaveChangesAsync();

        return (batchId, first, second, otherBatch);
    }

    [Fact]
    public async Task AssignExamsToNewStudents_AssignsEveryBatchExamToEveryStudent()
    {
        using var ctx = DbContextFactory.Create();
        var (batchId, first, second, _) = await SeedBatchExamsAsync(ctx);

        var s1 = Builders.User("Student");
        var s2 = Builders.User("Student");
        ctx.Users.AddRange(s1, s2);
        await ctx.SaveChangesAsync();

        await new ExamService(ctx, NullLogger<ExamService>.Instance)
            .AssignExamsToNewStudentsAsync(new List<Guid> { s1.Id, s2.Id }, batchId);

        var assignments = await ctx.ExamStudents.ToListAsync();
        Assert.Equal(4, assignments.Count);
        Assert.Contains(assignments, a => a.ExamId == first.Id && a.StudentId == s1.Id);
        Assert.Contains(assignments, a => a.ExamId == second.Id && a.StudentId == s2.Id);
    }

    [Fact]
    public async Task AssignExamsToNewStudents_IgnoresExamsInOtherBatches()
    {
        using var ctx = DbContextFactory.Create();
        var (batchId, _, _, otherBatch) = await SeedBatchExamsAsync(ctx);

        var student = Builders.User("Student");
        ctx.Users.Add(student);
        await ctx.SaveChangesAsync();

        await new ExamService(ctx, NullLogger<ExamService>.Instance)
            .AssignExamsToNewStudentsAsync(new List<Guid> { student.Id }, batchId);

        Assert.DoesNotContain(await ctx.ExamStudents.ToListAsync(), a => a.ExamId == otherBatch.Id);
    }

    // The (ExamId, StudentId) unique index means a duplicate would throw, so this also
    // guards the de-duplication that replaced the per-pair AnyAsync.
    [Fact]
    public async Task AssignExamsToNewStudents_SkipsPairsThatAlreadyExist()
    {
        using var ctx = DbContextFactory.Create();
        var (batchId, first, second, _) = await SeedBatchExamsAsync(ctx);

        var student = Builders.User("Student");
        ctx.Users.Add(student);
        ctx.ExamStudents.Add(new ExamStudent
        {
            Id = Guid.NewGuid(),
            ExamId = first.Id,
            StudentId = student.Id
        });
        await ctx.SaveChangesAsync();

        await new ExamService(ctx, NullLogger<ExamService>.Instance)
            .AssignExamsToNewStudentsAsync(new List<Guid> { student.Id }, batchId);

        var assignments = await ctx.ExamStudents.ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.Single(assignments, a => a.ExamId == first.Id && a.StudentId == student.Id);
        Assert.Single(assignments, a => a.ExamId == second.Id && a.StudentId == student.Id);
    }

    // Mixed case: one student is fully assigned, another partially, a third not at all.
    [Fact]
    public async Task AssignExamsToNewStudents_FillsOnlyTheMissingPairs()
    {
        using var ctx = DbContextFactory.Create();
        var (batchId, first, second, _) = await SeedBatchExamsAsync(ctx);

        var fully = Builders.User("Student");
        var partially = Builders.User("Student");
        var none = Builders.User("Student");
        ctx.Users.AddRange(fully, partially, none);

        ctx.ExamStudents.AddRange(
            new ExamStudent { Id = Guid.NewGuid(), ExamId = first.Id, StudentId = fully.Id },
            new ExamStudent { Id = Guid.NewGuid(), ExamId = second.Id, StudentId = fully.Id },
            new ExamStudent { Id = Guid.NewGuid(), ExamId = first.Id, StudentId = partially.Id });
        await ctx.SaveChangesAsync();

        await new ExamService(ctx, NullLogger<ExamService>.Instance)
            .AssignExamsToNewStudentsAsync(new List<Guid> { fully.Id, partially.Id, none.Id }, batchId);

        var assignments = await ctx.ExamStudents.ToListAsync();

        // 3 students x 2 exams, with no duplicates.
        Assert.Equal(6, assignments.Count);
        Assert.Equal(6, assignments.Select(a => (a.ExamId, a.StudentId)).Distinct().Count());
    }

    [Fact]
    public async Task AssignExamsToNewStudents_EmptyStudentList_CreatesNothing()
    {
        using var ctx = DbContextFactory.Create();
        var (batchId, _, _, _) = await SeedBatchExamsAsync(ctx);

        await new ExamService(ctx, NullLogger<ExamService>.Instance)
            .AssignExamsToNewStudentsAsync(new List<Guid>(), batchId);

        Assert.Empty(await ctx.ExamStudents.ToListAsync());
    }

    [Fact]
    public async Task AssignExamsToNewStudents_BatchWithNoExams_CreatesNothing()
    {
        using var ctx = DbContextFactory.Create();
        await SeedBatchExamsAsync(ctx);

        var student = Builders.User("Student");
        ctx.Users.Add(student);
        await ctx.SaveChangesAsync();

        await new ExamService(ctx, NullLogger<ExamService>.Instance)
            .AssignExamsToNewStudentsAsync(new List<Guid> { student.Id }, Guid.NewGuid());

        Assert.Empty(await ctx.ExamStudents.ToListAsync());
    }

    // Running twice must be a no-op the second time — this is the path bulk upload and
    // the per-student create both hit.
    [Fact]
    public async Task AssignExamsToNewStudents_IsIdempotent()
    {
        using var ctx = DbContextFactory.Create();
        var (batchId, _, _, _) = await SeedBatchExamsAsync(ctx);

        var student = Builders.User("Student");
        ctx.Users.Add(student);
        await ctx.SaveChangesAsync();

        var service = new ExamService(ctx, NullLogger<ExamService>.Instance);
        await service.AssignExamsToNewStudentsAsync(new List<Guid> { student.Id }, batchId);
        await service.AssignExamsToNewStudentsAsync(new List<Guid> { student.Id }, batchId);

        Assert.Equal(2, await ctx.ExamStudents.CountAsync());
    }

    // ── GetExamsForStudent ────────────────────────────────────────────────────

    [Fact]
    public async Task GetExamsForStudent_ReturnsOnlyPublishedAssignedExams()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);

        var published   = Builders.Exam(admin.Id, "Published",   published: true);
        var unpublished = Builders.Exam(admin.Id, "Unpublished",  published: false);
        var unassigned  = Builders.Exam(admin.Id, "Unassigned",  published: true);
        ctx.Exams.AddRange(published, unpublished, unassigned);

        ctx.ExamStudents.AddRange(
            new ExamStudent { Id = Guid.NewGuid(), ExamId = published.Id,   StudentId = student.Id },
            new ExamStudent { Id = Guid.NewGuid(), ExamId = unpublished.Id, StudentId = student.Id }
        );
        await ctx.SaveChangesAsync();

        var result = await new ExamService(ctx, NullLogger<ExamService>.Instance).GetExamsForStudentAsync(student.Id);

        Assert.Single(result);
        Assert.Equal("Published", result[0].Title);
    }

    // ── IsStudentAssigned ─────────────────────────────────────────────────────

    [Fact]
    public async Task IsStudentAssigned_ReturnsTrueWhenAssigned()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        ctx.ExamStudents.Add(new ExamStudent { Id = Guid.NewGuid(), ExamId = exam.Id, StudentId = student.Id });
        await ctx.SaveChangesAsync();

        var result = await new ExamService(ctx, NullLogger<ExamService>.Instance).IsStudentAssignedToExamAsync(exam.Id, student.Id);
        Assert.True(result);
    }

    [Fact]
    public async Task IsStudentAssigned_ReturnsFalseWhenNotAssigned()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        await ctx.SaveChangesAsync();

        var result = await new ExamService(ctx, NullLogger<ExamService>.Instance).IsStudentAssignedToExamAsync(exam.Id, student.Id);
        Assert.False(result);
    }

    // ── Attempt Management ────────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveAttempt_ReturnsInProgressAttempt()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        var attempt = Builders.Attempt(exam.Id, student.Id, AttemptStatus.InProgress);
        ctx.ExamAttempts.Add(attempt);
        await ctx.SaveChangesAsync();

        var result = await new ExamService(ctx, NullLogger<ExamService>.Instance).GetActiveAttemptAsync(exam.Id, student.Id);
        Assert.NotNull(result);
        Assert.Equal(attempt.Id, result.Id);
    }

    [Fact]
    public async Task GetActiveAttempt_ReturnsNullForSubmittedAttempt()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        ctx.ExamAttempts.Add(Builders.Attempt(exam.Id, student.Id, AttemptStatus.Submitted, totalScore: 8));
        await ctx.SaveChangesAsync();

        var result = await new ExamService(ctx, NullLogger<ExamService>.Instance).GetActiveAttemptAsync(exam.Id, student.Id);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCompletedAttempt_ReturnsSubmittedAttempt()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        var attempt = Builders.Attempt(exam.Id, student.Id, AttemptStatus.Submitted, totalScore: 10);
        ctx.ExamAttempts.Add(attempt);
        await ctx.SaveChangesAsync();

        var result = await new ExamService(ctx, NullLogger<ExamService>.Instance).GetCompletedAttemptAsync(exam.Id, student.Id);
        Assert.NotNull(result);
        Assert.Equal(AttemptStatus.Submitted, result.Status);
    }

    [Fact]
    public async Task GetCompletedAttempt_ReturnsTimedOutAttempt()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        ctx.ExamAttempts.Add(Builders.Attempt(exam.Id, student.Id, AttemptStatus.TimedOut, totalScore: 5));
        await ctx.SaveChangesAsync();

        var result = await new ExamService(ctx, NullLogger<ExamService>.Instance).GetCompletedAttemptAsync(exam.Id, student.Id);
        Assert.NotNull(result);
        Assert.Equal(AttemptStatus.TimedOut, result.Status);
    }

    // ── StartExamAttempt ──────────────────────────────────────────────────────

    [Fact]
    public async Task StartExamAttempt_SetsCorrectTimeRemaining()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        await ctx.SaveChangesAsync();

        var attempt = await new ExamService(ctx, NullLogger<ExamService>.Instance).StartExamAttemptAsync(exam.Id, student.Id, durationMinutes: 90);

        Assert.Equal(90 * 60, attempt.TimeRemainingSeconds);
        Assert.Equal(AttemptStatus.InProgress, attempt.Status);
    }

    // ── SaveAnswer ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAnswer_CreatesNewAnswer()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        var question = Builders.McqQuestion(exam.Id);
        ctx.Questions.Add(question);
        var option = Builders.Option(question.Id, "A", "Four", isCorrect: true, order: 1);
        ctx.QuestionOptions.Add(option);
        var attempt = Builders.Attempt(exam.Id, student.Id);
        ctx.ExamAttempts.Add(attempt);
        await ctx.SaveChangesAsync();

        await new ExamService(ctx, NullLogger<ExamService>.Instance).SaveAnswerAsync(attempt.Id, question.Id, option.Id, null);

        var answer = await ctx.StudentAnswers
            .FirstOrDefaultAsync(sa => sa.AttemptId == attempt.Id && sa.QuestionId == question.Id);
        Assert.NotNull(answer);
        Assert.Equal(option.Id, answer.SelectedOptionId);
    }

    [Fact]
    public async Task SaveAnswer_UpdatesExistingAnswer()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        var question = Builders.McqQuestion(exam.Id);
        ctx.Questions.Add(question);
        var optionA = Builders.Option(question.Id, "A", "Four",  isCorrect: true,  order: 1);
        var optionB = Builders.Option(question.Id, "B", "Three", isCorrect: false, order: 2);
        ctx.QuestionOptions.AddRange(optionA, optionB);
        var attempt = Builders.Attempt(exam.Id, student.Id);
        ctx.ExamAttempts.Add(attempt);
        var existingAnswer = Builders.Answer(attempt.Id, question.Id, selectedOptionId: optionA.Id);
        ctx.StudentAnswers.Add(existingAnswer);
        await ctx.SaveChangesAsync();

        // Change answer from A to B
        await new ExamService(ctx, NullLogger<ExamService>.Instance).SaveAnswerAsync(attempt.Id, question.Id, optionB.Id, null);

        var count = await ctx.StudentAnswers.CountAsync(sa => sa.AttemptId == attempt.Id);
        var saved = await ctx.StudentAnswers
            .FirstAsync(sa => sa.AttemptId == attempt.Id && sa.QuestionId == question.Id);

        Assert.Equal(1, count);               // no duplicate created
        Assert.Equal(optionB.Id, saved.SelectedOptionId);
    }

    // ── RecalculateTotalMarks ─────────────────────────────────────────────────

    [Fact]
    public async Task RecalculateTotalMarks_SumsAllQuestionMarks()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var exam = Builders.Exam(admin.Id);
        exam.TotalMarks = 0;
        ctx.Exams.Add(exam);
        ctx.Questions.AddRange(
            Builders.McqQuestion(exam.Id, marks: 4, displayOrder: 1),
            Builders.TrueFalseQuestion(exam.Id, marks: 2, displayOrder: 2),
            Builders.FillInTheBlankQuestion(exam.Id, marks: 3, displayOrder: 3)
        );
        await ctx.SaveChangesAsync();

        await new ExamService(ctx, NullLogger<ExamService>.Instance).RecalculateTotalMarksAsync(exam.Id);

        var saved = await ctx.Exams.FindAsync(exam.Id);
        Assert.Equal(9, saved!.TotalMarks);
    }

    [Fact]
    public async Task RecalculateTotalMarks_NoQuestions_SetsZero()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var exam = Builders.Exam(admin.Id);
        exam.TotalMarks = 100;
        ctx.Exams.Add(exam);
        await ctx.SaveChangesAsync();

        await new ExamService(ctx, NullLogger<ExamService>.Instance).RecalculateTotalMarksAsync(exam.Id);

        var saved = await ctx.Exams.FindAsync(exam.Id);
        Assert.Equal(0, saved!.TotalMarks);
    }

    // ── GetCompletedExamIdsForStudent ─────────────────────────────────────────

    [Fact]
    public async Task GetCompletedExamIds_ReturnsSubmittedAndTimedOutExamIds()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student");
        ctx.Users.AddRange(admin, student);

        var e1 = Builders.Exam(admin.Id, "E1");
        var e2 = Builders.Exam(admin.Id, "E2");
        var e3 = Builders.Exam(admin.Id, "E3");
        ctx.Exams.AddRange(e1, e2, e3);

        ctx.ExamAttempts.AddRange(
            Builders.Attempt(e1.Id, student.Id, AttemptStatus.Submitted, 5),
            Builders.Attempt(e2.Id, student.Id, AttemptStatus.TimedOut,  3),
            Builders.Attempt(e3.Id, student.Id, AttemptStatus.InProgress)
        );
        await ctx.SaveChangesAsync();

        var ids = await new ExamService(ctx, NullLogger<ExamService>.Instance).GetCompletedExamIdsForStudentAsync(student.Id);

        Assert.Contains(e1.Id, ids);
        Assert.Contains(e2.Id, ids);
        Assert.DoesNotContain(e3.Id, ids);
    }

    // ── ExceedsProctoringThreshold ────────────────────────────────────────────

    [Theory]
    [InlineData(true, 3, 4, true)]    // over the allowance → lock
    [InlineData(true, 3, 3, false)]   // exactly at allowance → still allowed
    [InlineData(true, 3, 1, false)]   // under the allowance
    [InlineData(true, 0, 99, false)]  // 0 = warn only, never auto-submit
    [InlineData(false, 3, 99, false)] // proctoring disabled → never locks
    public void ExceedsProctoringThreshold_AppliesRule(bool enabled, int max, int count, bool expected)
    {
        var exam = new Exam { EnableProctoring = enabled, MaxProctoringWarnings = max };
        Assert.Equal(expected, ExamService.ExceedsProctoringThreshold(count, exam));
    }

    // ── GetBankQuestions ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetBankQuestions_ReturnsOnlyBankQuestions()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        SeedFullQuestion(ctx, examId: null, inBank: true,  subject: "Math");
        SeedFullQuestion(ctx, examId: exam.Id, inBank: false, subject: "Math");
        await ctx.SaveChangesAsync();

        var bank = await Service(ctx).GetBankQuestionsAsync();

        Assert.Single(bank);
        Assert.True(bank[0].IsInBank);
    }

    [Fact]
    public async Task GetBankQuestions_FiltersBySubject()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        SeedFullQuestion(ctx, examId: null, inBank: true, subject: "Math");
        SeedFullQuestion(ctx, examId: null, inBank: true, subject: "Physics");
        await ctx.SaveChangesAsync();

        var physics = await Service(ctx).GetBankQuestionsAsync(subject: "Physics");

        Assert.Single(physics);
        Assert.Equal("Physics", physics[0].Subject);
    }

    // ── CloneFromBank ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CloneFromBank_CopiesQuestionWithOptionsAndAnswerKey()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        var bankQ = SeedFullQuestion(ctx, examId: null, inBank: true);
        await ctx.SaveChangesAsync();

        var count = await Service(ctx).CloneFromBankAsync(exam.Id, new List<Guid> { bankQ.Id });

        Assert.Equal(1, count);
        var clone = await ctx.Questions.Include(q => q.Options).Include(q => q.AnswerKey)
            .SingleAsync(q => q.ExamId == exam.Id);
        Assert.False(clone.IsInBank);
        Assert.Equal(2, clone.Options.Count);
        Assert.NotNull(clone.AnswerKey);
        // Original bank question is left untouched.
        Assert.True((await ctx.Questions.FindAsync(bankQ.Id))!.IsInBank);
    }

    [Fact]
    public async Task CloneFromBank_AppendsAfterExistingDisplayOrder()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        var existing = Builders.McqQuestion(exam.Id, displayOrder: 3);
        ctx.Questions.Add(existing);
        var bankQ = SeedFullQuestion(ctx, examId: null, inBank: true);
        await ctx.SaveChangesAsync();

        await Service(ctx).CloneFromBankAsync(exam.Id, new List<Guid> { bankQ.Id });

        var clone = await ctx.Questions.SingleAsync(q => q.ExamId == exam.Id && q.Id != existing.Id);
        Assert.Equal(4, clone.DisplayOrder); // max existing (3) + 1
    }

    // ── CloneToBank ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CloneToBank_CreatesBankCopyAndKeepsOriginal()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        var examQ = SeedFullQuestion(ctx, examId: exam.Id, inBank: false);
        await ctx.SaveChangesAsync();

        await Service(ctx).CloneToBankAsync(examQ.Id);

        var bankCopy = await ctx.Questions.Include(q => q.Options).Include(q => q.AnswerKey)
            .SingleAsync(q => q.IsInBank);
        Assert.Null(bankCopy.ExamId);
        Assert.Equal(2, bankCopy.Options.Count);
        Assert.NotNull(bankCopy.AnswerKey);
        Assert.NotNull(await ctx.Questions.FindAsync(examQ.Id)); // original remains
    }

    [Fact]
    public async Task CloneToBank_AlreadyInBank_IsNoOp()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var bankQ = SeedFullQuestion(ctx, examId: null, inBank: true);
        await ctx.SaveChangesAsync();

        await Service(ctx).CloneToBankAsync(bankQ.Id);

        Assert.Equal(1, await ctx.Questions.CountAsync(q => q.IsInBank));
    }

    // ── CopyQuestionsToExam ───────────────────────────────────────────────────

    [Fact]
    public async Task CopyQuestionsToExam_CopiesToTargetAndKeepsOriginal()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var source = Builders.Exam(admin.Id, "Source");
        var target = Builders.Exam(admin.Id, "Target");
        ctx.Exams.AddRange(source, target);
        var q = SeedFullQuestion(ctx, examId: source.Id, inBank: false);
        await ctx.SaveChangesAsync();

        var count = await Service(ctx).CopyQuestionsToExamAsync(new List<Guid> { q.Id }, target.Id);

        Assert.Equal(1, count);
        Assert.Equal(1, await ctx.Questions.CountAsync(x => x.ExamId == target.Id));
        Assert.Equal(1, await ctx.Questions.CountAsync(x => x.ExamId == source.Id)); // original kept
    }

    // ── BulkMoveToBank ────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkMoveToBank_MovesQuestionsOutOfExam()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);
        var q1 = SeedFullQuestion(ctx, examId: exam.Id, inBank: false);
        var q2 = SeedFullQuestion(ctx, examId: exam.Id, inBank: false);
        await ctx.SaveChangesAsync();

        var count = await Service(ctx).BulkMoveToBankAsync(new List<Guid> { q1.Id, q2.Id });

        Assert.Equal(2, count);
        Assert.Equal(0, await ctx.Questions.CountAsync(q => q.ExamId == exam.Id)); // originals removed
        Assert.Equal(2, await ctx.Questions.CountAsync(q => q.IsInBank));          // bank copies created
    }

    [Fact]
    public async Task BulkMoveToBank_SkipsQuestionsAlreadyInBank()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var bankQ = SeedFullQuestion(ctx, examId: null, inBank: true);
        await ctx.SaveChangesAsync();

        var count = await Service(ctx).BulkMoveToBankAsync(new List<Guid> { bankQ.Id });

        Assert.Equal(0, count);
        Assert.Equal(1, await ctx.Questions.CountAsync(q => q.IsInBank)); // unchanged
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ExamService Service(Web.Data.ApplicationDbContext ctx) =>
        new(ctx, NullLogger<ExamService>.Instance);

    // Adds a full MCQ question (two options + an answer key) either to an exam or the bank.
    private static Question SeedFullQuestion(
        Web.Data.ApplicationDbContext ctx, Guid? examId, bool inBank, string subject = "Math", int marks = 4)
    {
        var q = new Question
        {
            Id = Guid.NewGuid(),
            ExamId = examId,
            IsInBank = inBank,
            Subject = subject,
            QuestionText = "Sample question?",
            QuestionType = QuestionType.MCQ,
            Marks = marks,
            DisplayOrder = 1,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Questions.Add(q);
        ctx.QuestionOptions.AddRange(
            Builders.Option(q.Id, "A", "Option A", isCorrect: true,  order: 1),
            Builders.Option(q.Id, "B", "Option B", isCorrect: false, order: 2));
        ctx.AnswerKeys.Add(Builders.AnswerKey(q.Id, "A"));
        return q;
    }
}
