using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperCommandService
{
    public void CreatePlannerList(string title, string? folder)
    {
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        EnsurePaperCapacity();
        var cleanTitle = RequiredText(title, _controller.State.MaxTitleLength, false, "title");
        var cleanFolder = OptionalText(folder, 80, true, "folder");
        using (_controller.SuppressPaperPluginEventScans())
        {
            var paper = _controller.CreatePaper(PaperTypes.Todo, show: false)
                ?? throw Error("paper_limit", "PaperNook cannot create another paper.");
            paper.Title = cleanTitle;
            paper.PlannerFolder = string.IsNullOrWhiteSpace(cleanFolder) ? null : cleanFolder.Trim();
            paper.IsVisible = false;
            if (!_controller.TryCommitExternalMutation())
            {
                _controller.RollbackExternalCreatedPaper(paper);
                _controller.ResetPaperPluginEventBaseline();
                throw SaveFailed();
            }
            _controller.RunExternalPostCommitUi(() => _controller.FinalizeExternalPaperCreated(paper, false));
        }
        _controller.PublishExternalPaperOperation(PaperOperationContext.User());
    }

    public IReadOnlyList<PlannerTask> ListPlannerTasks()
    {
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        return TaskPlanningRules.All(_controller.State).ToArray();
    }

    public void SetPlannerList(string paperId, string title, string? folder)
    {
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        var paper = RequirePaper(RequiredId(paperId, "paperId"), PaperTypes.Todo);
        var newTitle = RequiredText(title, _controller.State.MaxTitleLength, false, "title");
        var newFolder = OptionalText(folder, 80, true, "folder");
        var beforeTitle = paper.Title;
        var beforeFolder = paper.PlannerFolder;
        using (_controller.SuppressPaperPluginEventScans())
        {
            paper.Title = newTitle;
            paper.PlannerFolder = string.IsNullOrWhiteSpace(newFolder) ? null : newFolder.Trim();
            if (!_controller.TryCommitExternalMutation())
            {
                paper.Title = beforeTitle;
                paper.PlannerFolder = beforeFolder;
                _controller.ResetPaperPluginEventBaseline();
                throw SaveFailed();
            }
            _controller.RunExternalPostCommitUi(() => _controller.RefreshExternalTodoPaper(paper));
        }
        _controller.PublishExternalPaperOperation(PaperOperationContext.User());
    }

    public string AddPlannerTask(string? paperId, string text, DateOnly? plannedDate)
    {
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        var clean = RequiredText(text, PaperWindow.TodoTextMaxLength, false, "text");
        var paper = paperId == null ? _controller.State.Papers.FirstOrDefault(p =>
            p.Type == PaperTypes.Todo && p.PlannerInbox) : RequirePaper(paperId, PaperTypes.Todo);
        var created = paper == null;
        if (created) EnsurePaperCapacity();
        using (_controller.SuppressPaperPluginEventScans())
        {
            if (paper == null)
            {
                paper = _controller.CreatePaper(PaperTypes.Todo, show: false)
                    ?? throw Error("paper_limit", "PaperNook cannot create another paper.");
                paper.Title = Strings.Get("PlannerInbox");
                paper.PlannerInbox = true;
                paper.IsVisible = false;
            }
            var before = TodoPaperSnapshot.Capture(paper);
            if (IsBlankPlaceholderPaper(paper)) paper.Items.Clear();
            var item = new PaperItem { Text = clean, Order = paper.Items.Count,
                Planning = plannedDate.HasValue ? new() { PlannedDate = plannedDate } : null };
            paper.Items.Add(item);
            if (!_controller.TryCommitExternalMutation())
            {
                if (created) _controller.RollbackExternalCreatedPaper(paper);
                else before.Restore(paper);
                _controller.ResetPaperPluginEventBaseline();
                throw SaveFailed();
            }
            if (!created) _controller.RecordExternalTodoMutationUndoStep(paper, before.ToItems());
            _controller.RunExternalPostCommitUi(() =>
            {
                if (created) _controller.FinalizeExternalPaperCreated(paper, false);
                else _controller.RefreshExternalTodoPaper(paper);
            });
            _controller.PublishExternalPaperOperation(PaperOperationContext.User());
            return item.Id;
        }
    }

    public void SetTaskPlanning(string paperId, string todoId, string expectedVersion,
        TaskPlanningData planning, PaperOperationContext context)
        => SetTaskDetailsCore(paperId, todoId, expectedVersion, null, planning, context);

    public void SetTaskDetails(string paperId, string todoId, string expectedVersion,
        string text, TaskPlanningData planning, PaperOperationContext context)
        => SetTaskDetailsCore(paperId, todoId, expectedVersion,
            RequiredText(text, PaperWindow.TodoTextMaxLength, false, "text"), planning, context);

    private void SetTaskDetailsCore(string paperId, string todoId, string expectedVersion,
        string? text, TaskPlanningData planning, PaperOperationContext context)
    {
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        var paper = RequirePaper(paperId, PaperTypes.Todo);
        var item = RequireTodo(paper, todoId);
        if (TaskPlanningRules.Version(item) != expectedVersion)
            throw Error("stale_task", Strings.Get("PlannerStale"));
        try { TaskPlanningRules.Validate(planning); }
        catch (ArgumentException ex) { throw Error("invalid_params", ex.Message); }
        if (planning.ListId != null) _ = RequirePaper(planning.ListId, PaperTypes.Todo);
        if (planning.ScheduledStart != item.Planning?.ScheduledStart || planning.ScheduledEnd != item.Planning?.ScheduledEnd)
        {
            if (planning.ScheduledStart.HasValue && TaskPlanningRules.All(_controller.State).Any(t =>
                t.Item != item && !t.Item.Done && t.Planning.ScheduledStart.HasValue &&
                planning.ScheduledStart < t.Planning.ScheduledEnd && t.Planning.ScheduledStart < planning.ScheduledEnd))
                throw Error("schedule_conflict", Strings.Get("PlannerConflict"));
        }
        if (item.Planning == planning && (text == null || item.Text == text)) return;
        CommitPlannerChanges([(paper, item, planning)], context,
            text == null ? null : (item, text));
    }

    public PlannerScheduleDraft PreviewPlannerSchedule(IReadOnlyList<ScheduleProposal> proposals)
    {
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        try { return TaskPlanningRules.CreateDraft(_controller.State, proposals); }
        catch (ArgumentException ex) { throw Error("invalid_schedule", ex.Message); }
    }

    public void ApplyPlannerSchedule(PlannerScheduleDraft draft,
        IReadOnlyCollection<(string PaperId, string TodoId)>? selectedKeys = null)
    {
        // Re-resolve identity and recheck every version/conflict at the confirmation boundary.
        var selected = selectedKeys?.ToHashSet();
        if (selected != null && (selected.Count == 0 || selected.Any(key => !draft.Changes.Any(c => c.Task.Key == key))))
            throw Error("invalid_schedule", Strings.Get("PlannerStale"));
        var changes = draft.Changes.Where(c => selected == null || selected.Contains(c.Task.Key));
        var current = PreviewPlannerSchedule(changes.Select(c => new ScheduleProposal(
            c.Task.Paper.Id, c.Task.Item.Id, c.ExpectedVersion,
            c.After.ScheduledStart!.Value, c.After.ScheduledEnd!.Value)).ToArray());
        CommitPlannerChanges(current.Changes.Select(c =>
            (c.Task.Paper, c.Task.Item, (TaskPlanningData?)c.After)).ToArray(), PaperOperationContext.User());
        _controller.LastPlannerSchedule = current;
    }

    public void UndoPlannerSchedule()
    {
        EnsureRunning();
        _controller.PrepareExternalPaperOperation();
        var draft = _controller.LastPlannerSchedule ?? throw Error("no_undo", Strings.Get("PlannerNoUndo"));
        var changes = new List<(PaperData, PaperItem, TaskPlanningData?)>();
        foreach (var change in draft.Changes)
        {
            var paper = RequirePaper(change.Task.Paper.Id, PaperTypes.Todo);
            var item = RequireTodo(paper, change.Task.Item.Id);
            if (item.Planning != change.After)
                throw Error("stale_task", Strings.Get("PlannerStale"));
            changes.Add((paper, item, change.Before));
        }
        var changedItems = changes.Select(c => c.Item2).ToHashSet();
        var restored = changes.Where(c => !c.Item2.Done && c.Item3?.ScheduledStart != null).ToArray();
        foreach (var change in restored)
        {
            var planning = change.Item3!;
            if (TaskPlanningRules.All(_controller.State).Any(t => !changedItems.Contains(t.Item) && !t.Item.Done &&
                t.Planning.ScheduledStart.HasValue && planning.ScheduledStart < t.Planning.ScheduledEnd &&
                t.Planning.ScheduledStart < planning.ScheduledEnd))
                throw Error("schedule_conflict", Strings.Get("PlannerConflict"));
        }
        CommitPlannerChanges(changes, PaperOperationContext.User());
        _controller.LastPlannerSchedule = null;
    }

    private void CommitPlannerChanges(IReadOnlyList<(PaperData Paper, PaperItem Item, TaskPlanningData? Planning)> changes,
        PaperOperationContext context, (PaperItem Item, string Text)? textChange = null)
    {
        var papers = changes.Select(c => c.Paper).Distinct().ToArray();
        var snapshots = papers.ToDictionary(p => p, TodoPaperSnapshot.Capture);
        using (_controller.SuppressPaperPluginEventScans())
        {
            if (textChange is { } edit) edit.Item.Text = edit.Text;
            foreach (var change in changes) change.Item.Planning = change.Planning;
            if (!_controller.TryCommitExternalMutation())
            {
                foreach (var paper in papers) snapshots[paper].Restore(paper);
                _controller.ResetPaperPluginEventBaseline();
                throw SaveFailed();
            }
            foreach (var paper in papers)
                _controller.RecordExternalTodoMutationUndoStep(paper, snapshots[paper].ToItems());
            _controller.RunExternalPostCommitUi(() =>
            {
                foreach (var paper in papers) _controller.RefreshExternalTodoPaper(paper);
            });
        }
        _controller.PublishExternalPaperOperation(context);
    }
}
