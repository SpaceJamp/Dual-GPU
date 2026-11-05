using System.Text;

namespace GpuMonitor.Core.Logging;

/// <summary>
/// Append-only text file with size-based rotation. The file is opened, appended and closed on
/// every write so it is never held open. Failures set <see cref="LastWriteError"/>; nothing throws.
/// </summary>
public sealed class RollingFile
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _backups;

    public RollingFile(string path, long maxBytes = 1_000_000, int backups = 3)
    {
        _path = path;
        _maxBytes = maxBytes;
        _backups = backups;
    }

    public string Path => _path;

    public string? LastWriteError { get; private set; }

    public void Write(string text)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var bytes = new UTF8Encoding(false).GetBytes(text);
            var info = new FileInfo(_path);
            if (info.Exists && info.Length > 0 && info.Length + bytes.Length > _maxBytes) Rotate();
            using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                fs.Write(bytes, 0, bytes.Length);
            }
            LastWriteError = null;
        }
        catch (Exception ex)
        {
            LastWriteError = ex.Message;
        }
    }

    private void Rotate()
    {
        if (_backups <= 0)
        {
            File.Delete(_path);
            return;
        }
        for (var i = _backups; i >= 1; i--)
        {
            var src = i == 1 ? _path : $"{_path}.{i - 1}";
            if (!File.Exists(src)) continue;
            File.Move(src, $"{_path}.{i}", true);
        }
    }
}
