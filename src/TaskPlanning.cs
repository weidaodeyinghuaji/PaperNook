using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperTodo;

/// <summary>Optional additive core data. Date-only values are never converted through UTC.</summary>
public sealed record TaskPlanningData
{
    public DateOnly? PlannedDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public int Priority { get; init; }
    public int DurationMinutes { get; init; } = 30;
    public string Tags { get; init; } = "";
    public string? ListId { get; init; }
    public DateTimeOffset? ScheduledStart { get; init; }
    public DateTimeOffset? ScheduledEnd { get; init; }
    public bool Locked { get; init; }

    [JsonIgnore]
    public bool HasContent => PlannedDate.HasValue || DueDate.HasValue || Priority != 0 ||
        DurationMinutes != 30 || !string.IsNullOrWhiteSpace(Tags) || ScheduledStart.HasValue || Locked || ListId != null;
}

public sealed record PlannerPinnedView(string View, string? PaperId)
{
    public PlannerPinnedLayout? Layout { get; init; }
}

public sealed record PlannerPinnedLayout(double Left, double Top, double Width, double Height,
    string? MonitorDeviceName, string Density = "auto")
{
    public static PlannerPinnedLayout? Normalize(PlannerPinnedLayout? value)
    {
        if (value == null || !double.IsFinite(value.Left) || !double.IsFinite(value.Top) ||
            !double.IsFinite(value.Width) || !double.IsFinite(value.Height) || value.Width <= 0 || value.Height <= 0)
            return null;
        return value with { Width = Math.Clamp(value.Width, 340, 10000), Height = Math.Clamp(value.Height, 400, 10000),
            Density = value.Density is "compact" or "comfortable" ? value.Density : "auto",
            MonitorDeviceName = value.MonitorDeviceName is { Length: <= 128 } ? value.MonitorDeviceName : null };
    }
}

internal sealed record PlannerTask(PaperData Paper, PaperItem Item)
{
    public TaskPlanningData Planning => Item.Planning ?? new();
    public (string PaperId, string TodoId) Key => (Paper.Id, Item.Id);
    public string Version => TaskPlanningRules.Version(Item);
}

internal sealed record ScheduleProposal(string PaperId, string TodoId, string ExpectedVersion,
    DateTimeOffset Start, DateTimeOffset End);
internal sealed record ScheduleChange(PlannerTask Task, string ExpectedVersion,
    TaskPlanningData? Before, TaskPlanningData After);
internal sealed record PlannerScheduleDraft(IReadOnlyList<ScheduleChange> Changes);

/// <summary>A short-lived read projection; the original papers/items remain authoritative.</summary>
internal sealed class PlannerTaskQuery
{
    private readonly Dictionary<string, PaperData> _lists;
    internal PlannerTask[] Tasks { get; }
    internal PlannerTaskQuery(AppState state)
    {
        Tasks = TaskPlanningRules.All(state).ToArray();
        _lists = state.Papers.Where(p => p.Type == PaperTypes.Todo).ToDictionary(p => p.Id);
    }
    internal PaperData ResolveList(PlannerTask task) => task.Planning.ListId is { } id && _lists.TryGetValue(id, out var paper)
        ? paper : task.Paper;
    internal IEnumerable<PlannerTask> Query(string view, string? paperId, DateOnly today, string search = "") =>
        TaskPlanningRules.Query(Tasks, ResolveList, view, paperId, today, search);
}

internal static class TaskPlanningRules
{
    public static TaskPlanningData Normalize(TaskPlanningData p)
    {
        var normalized = p with { Priority = Math.Clamp(p.Priority, 0, 3),
            DurationMinutes = Math.Clamp(p.DurationMinutes, 5, 1440),
            Tags = (p.Tags ?? "")[..Math.Min((p.Tags ?? "").Length, 256)] };
        if (p.ScheduledStart.HasValue != p.ScheduledEnd.HasValue ||
            (p.ScheduledStart.HasValue && (p.ScheduledEnd <= p.ScheduledStart ||
                p.ScheduledEnd - p.ScheduledStart > TimeSpan.FromDays(1))))
            normalized = normalized with { ScheduledStart = null, ScheduledEnd = null };
        return normalized;
    }

    public static IEnumerable<PlannerTask> All(AppState state) => state.Papers
        .Where(p => p.Type == PaperTypes.Todo)
        .SelectMany(p => p.Items.Where(TodoRules.HasMeaningfulContent).Select(i => new PlannerTask(p, i)));

    // Classification references a list; it never copies or reparents the source paper's task.
    public static PaperData ResolveList(AppState state, PlannerTask task) =>
        state.Papers.FirstOrDefault(p => p.Type == PaperTypes.Todo && p.Id == task.Planning.ListId) ?? task.Paper;

    public static IEnumerable<PlannerTask> Query(AppState state, string view, string? paperId,
        DateOnly today, string search = "") =>
        Query(All(state), task => ResolveList(state, task), view, paperId, today, search);

    internal static IEnumerable<PlannerTask> Query(IEnumerable<PlannerTask> source, Func<PlannerTask, PaperData> resolveList,
        string view, string? paperId, DateOnly today, string search = "")
    {
        var query = source.Where(t => view == "completed" ? t.Item.Done : !t.Item.Done);
        query = query.Where(t => view switch
        {
            "inbox" => resolveList(t).PlannerInbox,
            "today" => OnDate(t.Planning, today) || IsOverdue(t.Planning, today),
            "tomorrow" => OnDate(t.Planning, today.AddDays(1)),
            "week" => Enumerable.Range(0, 7).Any(d => OnDate(t.Planning, today.AddDays(d))) ||
                IsOverdue(t.Planning, today),
            "agenda" => t.Planning.ScheduledStart.HasValue,
            "list" => resolveList(t).Id == paperId,
            _ => true
        });
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(t => (t.Item.Text + " " + t.Planning.Tags + " " + resolveList(t).Title)
                .Contains(search.Trim(), StringComparison.CurrentCultureIgnoreCase));
        return query.OrderBy(t => IsOverdue(t.Planning, today) ? 0 : 1)
            .ThenBy(t => t.Planning.ScheduledStart)
            .ThenByDescending(t => t.Planning.Priority).ThenBy(t => t.Item.Order);
    }

    public static bool OnDate(TaskPlanningData p, DateOnly date) =>
        p.PlannedDate == date || p.DueDate == date ||
        (p.ScheduledStart.HasValue && DateOnly.FromDateTime(p.ScheduledStart.Value.LocalDateTime) == date);

    public static bool IsOverdue(TaskPlanningData p, DateOnly today) => p.DueDate < today;

    public static void Validate(TaskPlanningData p)
    {
        if (p.Priority is < 0 or > 3 || p.DurationMinutes is < 5 or > 1440 ||
            p.Tags == null || p.Tags.Length > 256 || p.ListId?.Length > 64)
            throw new ArgumentException("Invalid priority, duration or tags.");
        if (p.ScheduledStart.HasValue != p.ScheduledEnd.HasValue ||
            (p.ScheduledStart.HasValue && (p.ScheduledEnd <= p.ScheduledStart ||
                p.ScheduledEnd - p.ScheduledStart > TimeSpan.FromDays(1))))
            throw new ArgumentException("Provide a positive schedule interval of at most 24 hours.");
        if (p.DueDate.HasValue && p.ScheduledEnd.HasValue &&
            DateOnly.FromDateTime(p.ScheduledEnd.Value.LocalDateTime) > p.DueDate)
            throw new ArgumentException(Strings.Get("PlannerDeadlineError"));
    }

    public static string Version(PaperItem item) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { item.Text, item.Done, item.Planning }))));

    public static PlannerScheduleDraft CreateDraft(AppState state, IReadOnlyList<ScheduleProposal> proposals)
    {
        if (proposals.Count is < 1 or > 100) throw new ArgumentException("Provide 1–100 schedule blocks.");
        var tasks = All(state).ToDictionary(t => t.Key);
        var changes = new List<ScheduleChange>();
        var keys = new HashSet<(string, string)>();
        foreach (var proposal in proposals)
        {
            var key = (proposal.PaperId, proposal.TodoId);
            if (!keys.Add(key) || !tasks.TryGetValue(key, out var task))
                throw new ArgumentException("Unknown or duplicate task.");
            if (task.Item.Done || task.Planning.Locked || task.Version != proposal.ExpectedVersion)
                throw new ArgumentException("Task is completed, locked or changed; read current tasks again.");
            var after = task.Planning with { ScheduledStart = proposal.Start, ScheduledEnd = proposal.End };
            Validate(after);
            changes.Add(new(task, proposal.ExpectedVersion, task.Item.Planning, after));
        }
        var intervals = tasks.Values.Where(t => !t.Item.Done && !keys.Contains(t.Key) &&
                t.Planning.ScheduledStart.HasValue)
            .Select(t => (Start: t.Planning.ScheduledStart!.Value, End: t.Planning.ScheduledEnd!.Value))
            .Concat(changes.Select(c => (Start: c.After.ScheduledStart!.Value, End: c.After.ScheduledEnd!.Value)))
            .OrderBy(i => i.Start).ToArray();
        for (var i = 1; i < intervals.Length; i++)
            if (intervals[i].Start < intervals[i - 1].End)
                throw new ArgumentException("Schedule blocks overlap an existing task or each other.");
        return new(changes);
    }
}
