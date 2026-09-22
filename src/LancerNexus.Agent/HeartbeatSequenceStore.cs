namespace LancerNexus.Agent;

public sealed class HeartbeatSequenceStore(string filePath)
{
    private readonly object sync = new();
    private ulong current = Load(filePath);
    private readonly string fullPath = Path.GetFullPath(filePath);

    public ulong Next()
    {
        lock (sync)
        {
            current = checked(current + 1);
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryPath, current.ToString(System.Globalization.CultureInfo.InvariantCulture));
                File.Move(temporaryPath, fullPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }

            return current;
        }
    }

    private static ulong Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return 0;
        var value = File.ReadAllText(fullPath);
        if (!ulong.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var sequence))
            throw new InvalidDataException($"Heartbeat sequence state at '{fullPath}' is invalid.");
        return sequence;
    }
}
