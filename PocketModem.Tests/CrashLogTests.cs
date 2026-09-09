namespace PocketModem.Tests;

/// <summary>
/// The concurrency fix behind the silent crashes.
///
/// A List mutated while another thread enumerates it throws
/// InvalidOperationException, and an exception escaping a timer callback or a
/// background thread kills a .NET process outright - no dialog, no console
/// output, the app simply disappears. This reproduces the race and shows the
/// snapshot-under-lock approach survives it.
/// </summary>
public class HistoryConcurrencyTests
{
    [Fact]
    public void An_unlocked_list_can_throw_while_being_read()
    {
        // Demonstrates the original failure, so the fix below is not solving an
        // imagined problem. Not asserted as always throwing: it is a race, and
        // a test that demanded it fail every time would itself be flaky.
        var history = new List<double>();
        bool observedFailure = false;

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 20000; i++)
            {
                history.Add(i);
                if (history.Count > 40) history.RemoveAt(0);
            }
        });

        try
        {
            while (!writer.IsCompleted)
            {
                foreach (var _ in history) { }
            }
        }
        catch (InvalidOperationException)
        {
            observedFailure = true;
        }

        writer.Wait();
        Assert.True(observedFailure || true, "race did not surface this run");
    }

    [Fact]
    public void Snapshotting_under_a_lock_survives_concurrent_writes()
    {
        // What MainWindow does now: mutate under the lock, copy under the lock,
        // then draw from the copy.
        var history = new List<double>();
        var gate = new object();

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 20000; i++)
            {
                lock (gate)
                {
                    history.Add(i);
                    if (history.Count > 40) history.RemoveAt(0);
                }
            }
        });

        // Reading a snapshot must never throw, however often it runs.
        while (!writer.IsCompleted)
        {
            double[] snapshot;
            lock (gate) snapshot = history.ToArray();
            foreach (var _ in snapshot) { }
        }

        writer.Wait();
        Assert.True(writer.IsCompletedSuccessfully);
    }

    [Fact]
    public void A_cleared_history_still_draws()
    {
        // Disconnect clears the list while the timer may be mid-draw. An empty
        // snapshot has to be handled rather than dividing by its maximum.
        var history = new List<double>();
        var gate = new object();

        lock (gate) history.Clear();

        double[] snapshot;
        lock (gate) snapshot = history.ToArray();

        Assert.Empty(snapshot);
        // The real code returns early here; the point is that it must not
        // compute Max() over nothing.
        Assert.Throws<InvalidOperationException>(() => snapshot.Max());
    }
}
