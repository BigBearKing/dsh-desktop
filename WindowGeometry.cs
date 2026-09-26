using System.Drawing;
using System.Text.Json;

namespace DshDesktop;

/// <summary>
/// App-managed window geometry, kept apart from the hand-edited config file.
/// Named <c>WindowGeometry</c> rather than WindowState to avoid colliding with
/// <see cref="Form.WindowState"/> at every use site.
/// </summary>
internal sealed class WindowGeometry
{
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; } = int.MinValue;
    public int Width { get; set; } = 1360;
    public int Height { get; set; } = 900;
    public bool Maximized { get; set; }

    static string Path => System.IO.Path.Combine(AppConfig.DataDirectory, "state.json");

    public static WindowGeometry Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<WindowGeometry>(File.ReadAllText(Path)) ?? new WindowGeometry();
        }
        catch
        {
            // Corrupt state is not worth reporting; fall back to defaults.
        }
        return new WindowGeometry();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DataDirectory);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Non-fatal.
        }
    }

    /// <summary>Restored bounds must still land on a monitor that exists right now.</summary>
    public bool TryGetBounds(out Rectangle bounds)
    {
        var candidate = new Rectangle(X, Y, Width, Height);
        bounds = candidate;

        if (X == int.MinValue || Y == int.MinValue)
            return false;
        if (Width < 400 || Height < 300)
            return false;
        if (!Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(candidate)))
            return false;

        return true;
    }
}
