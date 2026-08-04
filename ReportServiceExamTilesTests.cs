using Microsoft.EntityFrameworkCore;
using OnlineExamPlatform.Tests.Helpers;
using OnlineExamPlatform.Web.Data;
using OnlineExamPlatform.Web.Models;
using OnlineExamPlatform.Web.Services;

namespace OnlineExamPlatform.Tests.Services;

/// <summary>
/// Covers the Result Analysis tile aggregation after it was pushed server-side.
/// The figures must match what the previous in-memory implementation produced.
/// </summary>
public class ReportServiceExamTilesTests
{
    private sealed class Fixture
    {
        public Guid SchoolAId { get; init; }
        public Guid SchoolBId { get; init; }
        public Guid BatchAId { get; init; }
        public Exam Exam { get; init; } = null!;
    }

    // Two schools, one batch each, one exam, and attempts with a deliberate tie at the top.
    private static async Task<Fixture> SeedAsync(ApplicationDbContext ctx)
    {
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);

        var schoolA = new School { Id = Guid.NewGuid(), Name = "Alpha School" };
        var schoolB = new School { Id = Guid.NewGuid(), Name = "Beta School" };
        ctx.Schools.AddRange(schoolA, schoolB);

        var batchA = new Batch { Id = Guid.NewGuid(), ClassName = "Class 10", ProgramName = "JEE" };
        var batchB = new Batch { Id = Guid.NewGuid(), ClassName = "Class 12", ProgramName = "NEET" };
        ctx.Batches.AddRange(batchA, batchB);

        var exam = Builders.Exam(admin.Id, "Mock Test 1");
        exam.BatchId = batchA.Id;
        exam.TotalMarks = 100;
        ctx.Exams.Add(exam);

        // School A: scores 90, 90 (tie at the top) and 30.
        // School B: score 95 — higher, so it must be excluded when scoping to school A.
        await AddStudentAsync(ctx, exam, schoolA.Id, batchA.Id, "Ann", 90);
        await AddStudentAsync(ctx, exam, schoolA.Id, batchA.Id, "Bob", 90);
        await AddStudentAsync(ctx, exam, schoolA.Id, batchA.Id, "Cara", 30);
        await AddStudentAsync(ctx, exam, schoolB.Id, batchB.Id, "Dan", 95);

        await ctx.SaveChangesAsync();

        return new Fixture
        {
            SchoolAId = schoolA.Id,
            SchoolBId = schoolB.Id,
            BatchAId = batchA.Id,
            Exam = exam
        };
    }

    private static Task AddStudentAsync(ApplicationDbContext ctx, Exam exam,
        Guid schoolId, Guid batchId, string name, decimal? score)
    {
        var user = Builders.User("Student", name);
        ctx.Users.Add(user);
        ctx.StudentProfiles.Add(Builders.StudentProfile(user.Id, schoolId, batchId));
        ctx.ExamStudents.Add(new ExamStudent { Id = Guid.NewGuid(), ExamId = exam.Id, StudentId = user.Id });

        if (score.HasValue)
        {
            var attempt = Builders.Attempt(exam.Id, user.Id, AttemptStatus.Submitted);
            attempt.TotalScore = score.Value;
            ctx.ExamAttempts.Add(attempt);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Unscoped_CountsEveryAttemptAndAssignment()
    {
        using var ctx = DbContextFactory.Create();
        var f = await SeedAsync(ctx);

        var tiles = await new ReportService(ctx).GetExamResultTilesAsync(null, null);

        var tile = Assert.Single(tiles);
        Assert.Equal(f.Exam.Id, tile.ExamId);
        Assert.Equal(4, tile.TotalAttempted);
        Assert.Equal(4, tile.TotalAssigned);
        Assert.Equal(100, tile.TotalMarks);
    }

    [Fact]
    public async Task Unscoped_ComputesHighestAndAverage()
    {
        using var ctx = DbContextFactory.Create();
        await SeedAsync(ctx);

        var tiles = await new ReportService(ctx).GetExamResultTilesAsync(null, null);

        var tile = Assert.Single(tiles);
        Assert.Equal(95m, tile.HighestScore);
        // (90 + 90 + 30 + 95) / 4
        Assert.Equal(76.25m, tile.AverageScore);
    }

    [Fact]
    public async Task Unscoped_TopperIsTheSingleHighestScorer()
    {
        using var ctx = DbContextFactory.Create();
        await SeedAsync(ctx);

        var tiles = await new ReportService(ctx).GetExamResultTilesAsync(null, null);

        var tile = Assert.Single(tiles);
        Assert.Equal("Dan", tile.TopperName);
        Assert.Equal(0, tile.TopperExtraCount);
    }

    // Scoping must apply to the topper subquery too, not just the aggregate — otherwise
    // school A's tile would name school B's higher scorer.
    [Fact]
    public async Task ScopedToSchool_ExcludesOtherSchoolsAttempts()
    {
        using var ctx = DbContextFactory.Create();
        var f = await SeedAsync(ctx);

        var tiles = await new ReportService(ctx)
            .GetExamResultTilesAsync(f.SchoolAId, new List<Guid> { f.BatchAId });

        var tile = Assert.Single(tiles);
        Assert.Equal(3, tile.TotalAttempted);
        Assert.Equal(3, tile.TotalAssigned);
        Assert.Equal(90m, tile.HighestScore);
        Assert.Equal(70m, tile.AverageScore); // (90 + 90 + 30) / 3
    }

    [Fact]
    public async Task ScopedToSchool_ReportsTiedToppersAsExtraCount()
    {
        using var ctx = DbContextFactory.Create();
        var f = await SeedAsync(ctx);

        var tiles = await new ReportService(ctx)
            .GetExamResultTilesAsync(f.SchoolAId, new List<Guid> { f.BatchAId });

        var tile = Assert.Single(tiles);
        Assert.Contains(tile.TopperName, new[] { "Ann", "Bob" });
        Assert.Equal(1, tile.TopperExtraCount);
    }

    [Fact]
    public async Task ScopedToBatches_ExcludesExamsOutsideThoseBatches()
    {
        using var ctx = DbContextFactory.Create();
        var f = await SeedAsync(ctx);

        var tiles = await new ReportService(ctx)
            .GetExamResultTilesAsync(f.SchoolAId, new List<Guid> { Guid.NewGuid() });

        Assert.Empty(tiles);
    }

    [Fact]
    public async Task LabelsAreDistinctPerExam()
    {
        using var ctx = DbContextFactory.Create();
        await SeedAsync(ctx);

        var tiles = await new ReportService(ctx).GetExamResultTilesAsync(null, null);

        var tile = Assert.Single(tiles);
        // Three students share school A / batch A, so its label must appear once.
        Assert.Equal(2, tile.AssignedLocations.Count);
        Assert.Contains("Alpha School", tile.AssignedLocations);
        Assert.Contains("Class 10 JEE", tile.AssignedBatches);
        Assert.Equal(tile.AssignedBatches.Count, tile.AssignedBatches.Distinct().Count());
    }

    // An exam nobody has attempted must still produce a tile, with zeroed figures
    // rather than being dropped by the GroupBy.
    [Fact]
    public async Task ExamWithNoAttempts_StillProducesZeroedTile()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);
        var exam = Builders.Exam(admin.Id, "Untouched exam");
        ctx.Exams.Add(exam);
        await ctx.SaveChangesAsync();

        var tiles = await new ReportService(ctx).GetExamResultTilesAsync(null, null);

        var tile = Assert.Single(tiles);
        Assert.Equal(0, tile.TotalAttempted);
        Assert.Equal(0, tile.TotalAssigned);
        Assert.Equal(0m, tile.HighestScore);
        Assert.Equal(0m, tile.AverageScore);
        Assert.Equal("", tile.TopperName);
        Assert.Equal(0, tile.TopperExtraCount);
    }

    [Fact]
    public async Task InProgressAttemptsAreExcluded()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        var student = Builders.User("Student", "Eve");
        ctx.Users.AddRange(admin, student);

        var exam = Builders.Exam(admin.Id);
        ctx.Exams.Add(exam);

        var inProgress = Builders.Attempt(exam.Id, student.Id);
        inProgress.TotalScore = 99;
        ctx.ExamAttempts.Add(inProgress);
        await ctx.SaveChangesAsync();

        var tiles = await new ReportService(ctx).GetExamResultTilesAsync(null, null);

        var tile = Assert.Single(tiles);
        Assert.Equal(0, tile.TotalAttempted);
        Assert.Equal(0m, tile.HighestScore);
    }

    [Fact]
    public async Task ExamsAreOrderedByStartDateDescending()
    {
        using var ctx = DbContextFactory.Create();
        var admin = Builders.User("Admin");
        ctx.Users.Add(admin);

        var older = Builders.Exam(admin.Id, "Older");
        older.StartDate = DateTime.UtcNow.AddDays(-10);
        var newer = Builders.Exam(admin.Id, "Newer");
        newer.StartDate = DateTime.UtcNow.AddDays(-1);
        ctx.Exams.AddRange(older, newer);
        await ctx.SaveChangesAsync();

        var tiles = await new ReportService(ctx).GetExamResultTilesAsync(null, null);

        Assert.Equal(new[] { "Newer", "Older" }, tiles.Select(t => t.ExamTitle).ToArray());
    }
}
