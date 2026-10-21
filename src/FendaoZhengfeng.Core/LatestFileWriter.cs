using System.Text;

namespace FendaoZhengfeng.Core;

// Only immutable strings cross the worker boundary. Native game state must be
// captured by the caller on the game thread before queuing a write.
public sealed class LatestFileWriter : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, string> pending = [];
    private readonly Dictionary<string, Exception> failures = [];
    private readonly List<Exception> notifications = [];
    private readonly Action<string, string> write;
    private Task worker = Task.CompletedTask;
    private bool running, closed;

    public LatestFileWriter(Action<string, string>? write = null) => this.write = write ?? AtomicWrite;
    public void Enqueue(string path, string contents)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            pending[path] = contents;
            if (running) return;
            running = true; worker = Task.Run(Drain);
        }
    }
    private void Drain()
    {
        while (true)
        {
            KeyValuePair<string, string> next;
            lock (gate)
            {
                if (pending.Count == 0) { running = false; return; }
                next = pending.First(); pending.Remove(next.Key);
            }
            try
            {
                write(next.Key, next.Value);
                lock (gate) failures.Remove(next.Key);
            }
            catch (Exception error)
            {
                var failure = new IOException("无法保存 " + next.Key, error);
                lock (gate) { failures[next.Key] = failure; notifications.Add(failure); }
            }
        }
    }
    public Exception[] TakeErrors()
    { lock (gate) { var errors = notifications.ToArray(); notifications.Clear(); return errors; } }
    public void Flush()
    {
        while (true)
        {
            Task current; lock (gate) current = worker;
            current.GetAwaiter().GetResult();
            lock (gate)
            {
                if (running) continue;
                if (failures.Count > 0) throw new AggregateException("进度保存失败。", failures.Values);
                return;
            }
        }
    }
    public void Dispose()
    { lock (gate) closed = true; Flush(); }
    public static void AtomicWrite(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        try { File.WriteAllText(temp, contents, Encoding.UTF8); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
