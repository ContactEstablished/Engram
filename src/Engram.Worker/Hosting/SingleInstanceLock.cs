using Engram.Core;

namespace Engram.Worker;

/// <summary>
/// One worker per data folder: the running worker holds <c>worker.lock</c> open exclusively. A second
/// copy (two SessionStart hooks racing to start it) fails to open the file and exits quietly.
/// </summary>
internal sealed class SingleInstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private SingleInstanceLock(FileStream stream) => _stream = stream;

    public static SingleInstanceLock? TryAcquire(string home)
    {
        Directory.CreateDirectory(home);
        try
        {
            var stream = new FileStream(EngramPaths.WorkerLock(home), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            var pid = System.Text.Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            stream.SetLength(0);
            stream.Write(pid);
            stream.Flush();
            return new SingleInstanceLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
