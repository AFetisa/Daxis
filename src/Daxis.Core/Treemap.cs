namespace Daxis.Core;

public readonly record struct Box(double X, double Y, double W, double H);

public static class TreemapLayout
{
    /// <summary>
    /// Squarified treemap (Bruls, Huizing, van Wijk): tiles in the order given (sort descending first for the
    /// best aspect ratios), each with area proportional to its value. Non-positive values get an empty box.
    /// </summary>
    public static Box[] Squarify(IReadOnlyList<double> values, Box bounds)
    {
        var result = new Box[values.Count];
        var total = values.Where(v => v > 0).Sum();
        if (total <= 0 || bounds.W <= 0 || bounds.H <= 0) return result;
        var area = values.Select(v => Math.Max(0, v) / total * bounds.W * bounds.H).ToArray();
        var idx = Enumerable.Range(0, values.Count).Where(i => area[i] > 0).ToList();

        var rem = bounds;
        var i = 0;
        while (i < idx.Count)
        {
            var side = Math.Min(rem.W, rem.H);
            var row = new List<int> { idx[i++] };
            var best = Worst(row, side);
            while (i < idx.Count)
            {
                row.Add(idx[i]);
                var w = Worst(row, side);
                if (w > best) { row.RemoveAt(row.Count - 1); break; }
                best = w;
                i++;
            }

            var sum = row.Sum(k => area[k]);
            if (rem.W >= rem.H)
            {
                // A column on the left of what's left.
                var colW = sum / rem.H;
                var y = rem.Y;
                foreach (var k in row) { var h = area[k] / colW; result[k] = new Box(rem.X, y, colW, h); y += h; }
                rem = new Box(rem.X + colW, rem.Y, Math.Max(0, rem.W - colW), rem.H);
            }
            else
            {
                // A row along the top.
                var rowH = sum / rem.W;
                var x = rem.X;
                foreach (var k in row) { var w = area[k] / rowH; result[k] = new Box(x, rem.Y, w, rowH); x += w; }
                rem = new Box(rem.X, rem.Y + rowH, rem.W, Math.Max(0, rem.H - rowH));
            }
        }
        return result;

        double Worst(List<int> row, double side)
        {
            var s = row.Sum(k => area[k]);
            var max = row.Max(k => area[k]);
            var min = row.Min(k => area[k]);
            return Math.Max(side * side * max / (s * s), s * s / (side * side * min));
        }
    }
}
