using System.Diagnostics.CodeAnalysis;
using projectFrameCut.ApplicationAPIBase.Localize;

namespace projectFrameCut.ApplicationAPIBase.Project
{
    [Obsolete("The DraftTasks was no longer maintained, and it may be removed in a future version. Consider migrate the background work to the Workspace Task API.")]
    public class DraftTasks
    {
        public required string Id { get; init; }
        public string Name { get; init; }
        public string Description { get; init; }
        public required Task InnerTask { get; init; }
        private CancellationTokenSource taskCts = new();
        public object? Result { get; private set; } = null;
        private bool hasCancelled = false;
        public event EventHandler? Finished;

        public string IsRunningDisplay =>
            hasCancelled ?
            InnerTask.Status switch
            {
                TaskStatus.Running or TaskStatus.WaitingForActivation or TaskStatus.WaitingToRun or TaskStatus.WaitingForChildrenToComplete
                => APIBaseLocalizedResources.Localized.DraftPage_Tasks_Status_Cancelling,
                TaskStatus.Canceled or TaskStatus.Faulted or TaskStatus.RanToCompletion => APIBaseLocalizedResources.Localized.DraftPage_Tasks_Status_Canceled,
                _ => APIBaseLocalizedResources.Localized.DraftPage_Tasks_Status_Unknown
            }
            : InnerTask.Status switch
            {
                TaskStatus.RanToCompletion => APIBaseLocalizedResources.Localized.DraftPage_Tasks_Status_Completed,
                TaskStatus.Canceled => APIBaseLocalizedResources.Localized.DraftPage_Tasks_Status_Canceled,
                TaskStatus.Faulted => APIBaseLocalizedResources.Localized.DraftPage_Tasks_Status_Fail,
                TaskStatus.Running or TaskStatus.WaitingForActivation or TaskStatus.WaitingToRun or TaskStatus.WaitingForChildrenToComplete => APIBaseLocalizedResources.Localized.DraftPage_Tasks_Status_Running,
                _ => APIBaseLocalizedResources.Localized.DraftPage_Tasks_Status_Unknown
            };

        [SetsRequiredMembers]
        public DraftTasks(string id, Func<CancellationToken, object> innerTask, string name = "", string description = "")
        {
            Id = id;
            InnerTask = new(async () => Result = innerTask(taskCts.Token), taskCts.Token);
            Name = name;
            Description = description;
            InnerTask.ContinueWith(t => Finished?.Invoke(this, EventArgs.Empty));
        }

        [SetsRequiredMembers]
        public DraftTasks(string id, Func<CancellationToken, Task<object>> innerTask, string name = "", string description = "")
        {
            Id = id;
            InnerTask = new(async () => Result = await innerTask(taskCts.Token), taskCts.Token);
            Name = name;
            Description = description;
            InnerTask.ContinueWith(t => Finished?.Invoke(this, EventArgs.Empty));
        }

        [SetsRequiredMembers]
        public DraftTasks(string id, Action<CancellationToken> innerTask, string name = "", string description = "")
        {
            Id = id;
            InnerTask = new(() => innerTask(taskCts.Token), taskCts.Token);
            Name = name;
            Description = description;
            InnerTask.Start();
            InnerTask.ContinueWith(t => Finished?.Invoke(this, EventArgs.Empty));
        }

        [SetsRequiredMembers]
        public DraftTasks(string id, Func<CancellationToken, Task> innerTask, string name = "", string description = "")
        {
            Id = id;
            InnerTask = innerTask(taskCts.Token);
            Name = name;
            Description = description;
            InnerTask.ContinueWith(t => Finished?.Invoke(this, EventArgs.Empty));
        }

        public void Cancel()
        {
            hasCancelled = true;
            taskCts.Cancel();
        }
    }
}