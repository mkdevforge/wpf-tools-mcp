namespace WpfToolsMcp.Agent;

internal sealed class AgentConnectionTasks
{
    private readonly List<Task> _tasks = [];

    public int Count
    {
        get
        {
            PruneCompleted();
            return _tasks.Count;
        }
    }

    public void Track(Task task)
    {
        PruneCompleted();

        if (!task.IsCompleted)
        {
            _tasks.Add(task);
            return;
        }

        Observe(task);
    }

    public async Task WaitForAllAsync()
    {
        while (true)
        {
            PruneCompleted();
            if (_tasks.Count == 0)
            {
                return;
            }

            var snapshot = _tasks.ToArray();
            try
            {
                await Task.WhenAll(snapshot).ConfigureAwait(false);
            }
            catch
            {
                foreach (var task in snapshot)
                {
                    Observe(task);
                }
            }
        }
    }

    private void PruneCompleted()
    {
        for (var index = _tasks.Count - 1; index >= 0; index--)
        {
            var task = _tasks[index];
            if (!task.IsCompleted)
            {
                continue;
            }

            Observe(task);
            _tasks.RemoveAt(index);
        }
    }

    private static void Observe(Task task)
    {
        _ = task.Exception;
    }
}
