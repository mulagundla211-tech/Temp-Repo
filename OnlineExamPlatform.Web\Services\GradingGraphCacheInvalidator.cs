using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using OnlineExamPlatform.Web.Models;

namespace OnlineExamPlatform.Web.Services;

/// <summary>
/// Evicts the cached per-exam grading graph whenever any row that feeds it is written.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GradingService"/> caches an exam's questions, options and answer keys for
/// several minutes so the synchronized end-of-exam submit burst doesn't re-read the same
/// static rows once per student. That cache is only safe if it is dropped the moment the
/// underlying rows change — otherwise students are graded against a stale answer key,
/// which is silent and extremely hard to reproduce after the fact.
/// </para>
/// <para>
/// Invalidation used to live in <c>ExamService.RecalculateTotalMarksAsync</c>, which meant
/// correctness depended on every caller remembering to invoke it after every edit. Editing
/// an answer key, an option's IsCorrect flag or a solution does not change an exam's total
/// marks, so there was no reason for that method to be called at all on those paths.
/// </para>
/// <para>
/// Hooking <see cref="SaveChangesInterceptor"/> instead makes eviction structural: any write
/// to <see cref="Question"/>, <see cref="QuestionOption"/> or <see cref="AnswerKey"/> evicts
/// the affected exam, no matter which controller or service performed it, including code
/// written in the future.
/// </para>
/// <para>
/// <b>Scale-out constraint:</b> the backing store is <see cref="IMemoryCache"/>, which is
/// per-process. This interceptor only evicts on the node that performed the write, so on a
/// multi-instance deployment other nodes would keep serving their own stale copy for up to
/// the cache lifetime. Running more than one instance requires moving this to a distributed
/// cache (or a version/row-version check) first.
/// </para>
/// </remarks>
public sealed class GradingGraphCacheInvalidator : SaveChangesInterceptor
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<GradingGraphCacheInvalidator> _logger;

    // Question ids whose parent exam could not be resolved from the ChangeTracker during
    // SavingChanges. They are looked up after the save completes, where querying is safe.
    //
    // Keyed by DbContext because this interceptor is a singleton shared by every context
    // instance; a plain field would leak state across concurrent requests. The table holds
    // weak references, so a collected/returned-to-pool context drops its entry automatically.
    private static readonly ConditionalWeakTable<DbContext, HashSet<Guid>> PendingLookups = new();

    public GradingGraphCacheInvalidator(IMemoryCache cache, ILogger<GradingGraphCacheInvalidator> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        EvictResolvable(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        EvictResolvable(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ResolvePendingAsync(eventData.Context).GetAwaiter().GetResult();
        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        await ResolvePendingAsync(eventData.Context, cancellationToken);
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    // Scans the pending changes and evicts every exam whose grading graph they affect.
    //
    // This runs *before* the write completes. If the save then rolls back we will have
    // dropped a still-valid cache entry, which costs one repopulating query — the opposite
    // mistake (keeping a stale entry) would corrupt scores, so the trade is deliberate.
    private void EvictResolvable(DbContext? context)
    {
        if (context == null) return;

        var examIds = new HashSet<Guid>();
        var unresolvedQuestionIds = new HashSet<Guid>();

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            switch (entry.Entity)
            {
                case Question question:
                    // Read both values: moving a question to the bank (or to another exam)
                    // sets a new ExamId, but the *old* exam's cached graph is the stale one.
                    AddIfPresent(examIds, question.ExamId);
                    var examIdProperty = entry.Property(nameof(Question.ExamId));
                    if (examIdProperty.OriginalValue is Guid originalExamId)
                        examIds.Add(originalExamId);
                    break;

                case QuestionOption option:
                    Resolve(context, option.QuestionId, examIds, unresolvedQuestionIds);
                    break;

                case AnswerKey answerKey:
                    Resolve(context, answerKey.QuestionId, examIds, unresolvedQuestionIds);
                    break;
            }
        }

        Evict(examIds);

        if (unresolvedQuestionIds.Count > 0)
        {
            var pending = PendingLookups.GetOrCreateValue(context);
            foreach (var id in unresolvedQuestionIds) pending.Add(id);
        }
    }

    // Maps a QuestionId to its exam using only already-tracked entities. Options and answer
    // keys are always manipulated alongside their parent question in this codebase, so this
    // resolves without a query on every current path; anything else is deferred.
    private static void Resolve(DbContext context, Guid questionId,
        HashSet<Guid> examIds, HashSet<Guid> unresolved)
    {
        var question = context.ChangeTracker.Entries<Question>()
            .Select(e => e.Entity)
            .FirstOrDefault(q => q.Id == questionId);

        if (question == null)
        {
            unresolved.Add(questionId);
            return;
        }

        AddIfPresent(examIds, question.ExamId);
    }

    // Resolves any question ids that were not tracked, now that the save has completed and
    // issuing a query is safe.
    private async Task ResolvePendingAsync(DbContext? context, CancellationToken cancellationToken = default)
    {
        if (context == null || !PendingLookups.TryGetValue(context, out var questionIds) || questionIds.Count == 0)
            return;

        PendingLookups.Remove(context);

        try
        {
            var examIds = await context.Set<Question>()
                .AsNoTracking()
                .Where(q => questionIds.Contains(q.Id) && q.ExamId != null)
                .Select(q => q.ExamId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);

            Evict(examIds);
        }
        catch (Exception ex)
        {
            // The write already succeeded; failing to resolve here must not surface to the
            // caller. Log loudly because it means an exam may serve a stale grading graph
            // until the entry expires.
            _logger.LogError(ex,
                "Failed to resolve exams for {Count} changed question(s) — their cached grading graph may be stale.",
                questionIds.Count);
        }
    }

    private void Evict(IEnumerable<Guid> examIds)
    {
        foreach (var examId in examIds)
        {
            _cache.Remove(ExamService.GradingGraphCacheKey(examId));
            _logger.LogDebug("Evicted cached grading graph for exam {ExamId}.", examId);
        }
    }

    private static void AddIfPresent(HashSet<Guid> examIds, Guid? examId)
    {
        if (examId.HasValue) examIds.Add(examId.Value);
    }
}
