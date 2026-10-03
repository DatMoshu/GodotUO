using System.Buffers.Binary;
namespace CentrED.MapGen.Preview;

/// <summary>Read-only client palette. Never ships or copies client data.</summary>
public static class RadarPalette
{
    public static Func<string?>? ClientDataDirectory { get; set; }
    private static readonly object Gate = new();
    private static string? _path;
    private static DateTime _stamp;
    private static uint[]? _colors;
    public static string Status { get; private set; } = "Radar palette not loaded";

    public static uint[]? LoadConfigured()
    {
        var path = Environment.GetEnvironmentVariable("MAPGEN_RADARCOL");
        if (string.IsNullOrWhiteSpace(path))
        {
            var dir = Environment.GetEnvironmentVariable("UO_CLIENT_DATA");
            if (string.IsNullOrWhiteSpace(dir)) dir = ClientDataDirectory?.Invoke();
            path = string.IsNullOrWhiteSpace(dir) ? "" : Path.Combine(dir, "radarcol.mul");
        }
        lock (Gate)
        {
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (_path == path && _stamp == stamp) return _colors;
            _path = path; _stamp = stamp; _colors = null;
            try
            {
                if (!File.Exists(path)) throw new FileNotFoundException("radarcol.mul not found");
                _colors = Decode(File.ReadAllBytes(path));
                Status = "Client radar colours (land + highest static; hues not applied)";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Status = "Approximate tile colours: set UO_CLIENT_DATA or MAPGEN_RADARCOL (" + e.Message + ")";
                Console.Error.WriteLine(Status);
            }
            return _colors;
        }
    }

    public static uint[] Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 0x4000 * 2 || bytes.Length % 2 != 0)
            throw new InvalidDataException("Incomplete radar palette");
        var colors = new uint[bytes.Length / 2];
        for (int i = 0; i < colors.Length; i++)
        {
            int v = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(i * 2, 2));
            colors[i] = 0xFF000000u | (uint)(((v >> 10) & 31) << 3)
                | (uint)(((v >> 5) & 31) << 11) | (uint)((v & 31) << 19);
        }
        return colors;
    }
}
