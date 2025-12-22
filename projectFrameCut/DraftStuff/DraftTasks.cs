using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace projectFrameCut.DraftStuff
{
    public class DraftTasks
    {
        public required string Id { get; init; }
        public string Name { get; init; }
        public string Description { get; init; }
        public required Task InnerTask { get; init; }
        private CancellationTokenSource taskCts = new();
        public object? Result { get; private set; } = null;
        private bool hasCancelled = false;
        public event EventHandler Finished;
        public string IsRunningDisplay =>
            hasCancelled ?
            InnerTask.Status switch
            {
                TaskStatus.Running or TaskStatus.WaitingForActivation or TaskStatus.WaitingToRun or TaskStatus.WaitingForChildrenToComplete
                => Localized.DraftPage_Tasks_Status_Cancelling,
                TaskStatus.Canceled or TaskStatus.Faulted or TaskStatus.RanToCompletion => Localized.DraftPage_Tasks_Status_Canceled,
                _ => Localized.DraftPage_Tasks_Status_Unknown
            }
            : InnerTask.Status switch
            {
                TaskStatus.RanToCompletion => Localized.DraftPage_Tasks_Status_Completed,
                TaskStatus.Canceled => Localized.DraftPage_Tasks_Status_Canceled,
                TaskStatus.Faulted => Localized.DraftPage_Tasks_Status_Fail,
                TaskStatus.Running or TaskStatus.WaitingForActivation or TaskStatus.WaitingToRun or TaskStatus.WaitingForChildrenToComplete => Localized.DraftPage_Tasks_Status_Running,
                _ => Localized.DraftPage_Tasks_Status_Unknown
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
            //InnerTask.Start();
        }
        public void Cancel()
        {
            hasCancelled = true;
            taskCts.Cancel();
        }
    }
}