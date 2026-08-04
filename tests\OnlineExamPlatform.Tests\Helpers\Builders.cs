using OnlineExamPlatform.Web.Models;

namespace OnlineExamPlatform.Tests.Helpers;

/// <summary>
/// Factory helpers that return pre-populated model instances for tests.
/// They do NOT persist to any DbContext — callers decide when/whether to save.
/// </summary>
public static class Builders
{
    public static ApplicationUser User(string role = "Student", string? fullName = null) => new()
    {
        Id = Guid.NewGuid(),
        UserName = $"user_{Guid.NewGuid():N}",
        NormalizedUserName = $"USER_{Guid.NewGuid():N}".ToUpper(),
        Email = $"{Guid.NewGuid():N}@test.com",
        NormalizedEmail = $"{Guid.NewGuid():N}@TEST.COM".ToUpper(),
        FullName = fullName ?? "Test User",
        Role = role,
        IsActive = true,
        SecurityStamp = Guid.NewGuid().ToString(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    public static StudentProfile StudentProfile(Guid userId, Guid? schoolId = null, Guid? batchId = null, Guid? managedByAdminId = null) => new()
    {
        Id = userId,
        SchoolId = schoolId ?? Guid.NewGuid(),
        BatchId = batchId ?? Guid.NewGuid(),
        RollNumber = "R-101",
        ManagedByAdminId = managedByAdminId
    };

    public static Exam Exam(Guid createdById, string title = "Test Exam", bool published = true) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        DurationMinutes = 60,
        StartDate = DateTime.UtcNow.AddDays(-1),
        EndDate = DateTime.UtcNow.AddDays(1),
        TotalMarks = 0,
        IsPublished = published,
        CreatedById = createdById,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    public static Question McqQuestion(Guid examId, int marks = 2, int displayOrder = 1) => new()
    {
        Id = Guid.NewGuid(),
        ExamId = examId,
        QuestionText = "What is 2 + 2?",
        QuestionType = QuestionType.MCQ,
        Marks = marks,
        DisplayOrder = displayOrder,
        CreatedAt = DateTime.UtcNow
    };

    public static Question TrueFalseQuestion(Guid examId, int marks = 1, int displayOrder = 1) => new()
    {
        Id = Guid.NewGuid(),
        ExamId = examId,
        QuestionText = "The sky is blue.",
        QuestionType = QuestionType.TrueFalse,
        Marks = marks,
        DisplayOrder = displayOrder,
        CreatedAt = DateTime.UtcNow
    };

    public static Question FillInTheBlankQuestion(Guid examId, int marks = 3, int displayOrder = 1) => new()
    {
        Id = Guid.NewGuid(),
        ExamId = examId,
        QuestionText = "The capital of France is ___.",
        QuestionType = QuestionType.FillInTheBlank,
        Marks = marks,
        DisplayOrder = displayOrder,
        CreatedAt = DateTime.UtcNow
    };

    public static Question NumericalQuestion(Guid examId, int marks = 4, int displayOrder = 1) => new()
    {
        Id = Guid.NewGuid(),
        ExamId = examId,
        QuestionText = "What is the value of pi to 2 decimal places?",
        QuestionType = QuestionType.Numerical,
        Marks = marks,
        DisplayOrder = displayOrder,
        CreatedAt = DateTime.UtcNow
    };

    public static Question SubjectiveQuestion(Guid examId, int marks = 5, int displayOrder = 1) => new()
    {
        Id = Guid.NewGuid(),
        ExamId = examId,
        QuestionText = "Explain photosynthesis.",
        QuestionType = QuestionType.Subjective,
        Marks = marks,
        DisplayOrder = displayOrder,
        CreatedAt = DateTime.UtcNow
    };

    public static QuestionOption Option(Guid questionId, string label, string text, bool isCorrect, int order) => new()
    {
        Id = Guid.NewGuid(),
        QuestionId = questionId,
        OptionLabel = label,
        OptionText = text,
        IsCorrect = isCorrect,
        DisplayOrder = order
    };

    public static AnswerKey AnswerKey(Guid questionId, string correctAnswer) => new()
    {
        Id = Guid.NewGuid(),
        QuestionId = questionId,
        CorrectAnswer = correctAnswer
    };

    public static ExamAttempt Attempt(Guid examId, Guid studentId,
        AttemptStatus status = AttemptStatus.InProgress,
        int? totalScore = null) => new()
    {
        Id = Guid.NewGuid(),
        ExamId = examId,
        StudentId = studentId,
        Status = status,
        TotalScore = totalScore,
        TimeRemainingSeconds = 3600,
        StartedAt = DateTime.UtcNow,
        FinishedAt = status != AttemptStatus.InProgress ? DateTime.UtcNow : null,
        CreatedAt = DateTime.UtcNow
    };

    public static StudentAnswer Answer(Guid attemptId, Guid questionId,
        Guid? selectedOptionId = null, string? answerText = null) => new()
    {
        Id = Guid.NewGuid(),
        AttemptId = attemptId,
        QuestionId = questionId,
        SelectedOptionId = selectedOptionId,
        AnswerText = answerText,
        AnsweredAt = DateTime.UtcNow
    };
}
