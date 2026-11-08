namespace ImageViewer;

internal sealed class SlideshowTransition(IEnumerable<ImagePane> panes)
{
    private readonly Dictionary<ImagePane, (Action Present, TaskCompletionSource Completion)?> _frames =
        panes.ToDictionary(pane => pane, _ => ((Action, TaskCompletionSource)?)null);

    public Task Prepare(ImagePane pane, Action present)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _frames[pane] = (present, completion);
        PresentWhenReady();
        return completion.Task;
    }

    public void Withdraw(ImagePane pane, bool presentPreparedFrame)
    {
        if (_frames.Remove(pane, out var entry) && entry is { } frame)
        {
            if (presentPreparedFrame) frame.Present();
            frame.Completion.SetResult();
        }
        PresentWhenReady();
    }

    private void PresentWhenReady()
    {
        if (_frames.Values.Any(present => present == null)) return;

        // Publish the entire batch in one dispatcher operation.
        foreach (var entry in _frames.Values)
        {
            var frame = entry!.Value;
            frame.Present();
            frame.Completion.SetResult();
        }
        _frames.Clear();
    }
}
