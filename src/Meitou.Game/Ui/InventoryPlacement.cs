using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// Opening positions for independent inventory windows, with Kenshi's screen margin and five candidate rows.
/// </summary>
static class InventoryPlacement
{
    /// <summary>
    /// The opening margin, truncated to pixels: five percent of the smaller screen dimension.
    /// </summary>
    internal static float Margin(int width, int height) => (int)(Math.Min(width, height) * 0.05f);

    /// <summary>
    /// Places an independent window after existing windows on the same row; a full row advances by twice the opening margin.
    /// </summary>
    internal static Vector2 FindSpace(int width, int height, float windowWidth, IReadOnlyList<UiRect> occupied)
    {
        float margin = Margin(width, height), y = margin;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            float x = margin;
            foreach (var r in occupied)
            {
                if (r.Y == y) x = Math.Max(x, r.X + r.Width);
            }

            if (x + windowWidth <= width) return new(x, y);
            if (attempt < 4) y += 2 * margin;
        }

        return new(margin, y);
    }
}
