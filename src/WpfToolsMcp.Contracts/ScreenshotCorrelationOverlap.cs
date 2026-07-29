namespace WpfToolsMcp.Contracts;

internal static class ScreenshotCorrelationOverlap
{
    public static bool HasAnyOverlap(IEnumerable<Rect> rectangles)
    {
        ArgumentNullException.ThrowIfNull(rectangles);

        var events = new List<SweepEvent>();
        var yCoordinates = new List<long>();
        foreach (var rectangle in rectangles)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
            {
                continue;
            }

            var left = (long)rectangle.X;
            var right = left + rectangle.Width;
            var top = (long)rectangle.Y;
            var bottom = top + rectangle.Height;
            events.Add(new SweepEvent(left, IsStart: true, top, bottom));
            events.Add(new SweepEvent(right, IsStart: false, top, bottom));
            yCoordinates.Add(top);
            yCoordinates.Add(bottom);
        }

        if (events.Count < 4)
        {
            return false;
        }

        var orderedY = yCoordinates
            .Distinct()
            .Order()
            .ToArray();
        var yIndex = orderedY
            .Select((value, index) => (value, index))
            .ToDictionary(pair => pair.value, pair => pair.index);
        var tree = new RangeMaximumTree(Math.Max(1, orderedY.Length - 1));

        events.Sort(static (first, second) =>
        {
            var x = first.X.CompareTo(second.X);
            if (x != 0)
            {
                return x;
            }

            // Rectangles that only touch at an edge do not overlap.
            return first.IsStart.CompareTo(second.IsStart);
        });

        foreach (var sweepEvent in events)
        {
            var start = yIndex[sweepEvent.Top];
            var end = yIndex[sweepEvent.Bottom] - 1;
            if (start > end)
            {
                continue;
            }

            if (sweepEvent.IsStart)
            {
                if (tree.QueryMaximum(start, end) > 0)
                {
                    return true;
                }

                tree.Add(start, end, 1);
            }
            else
            {
                tree.Add(start, end, -1);
            }
        }

        return false;
    }

    private sealed class RangeMaximumTree
    {
        private readonly int _length;
        private readonly int[] _maximum;
        private readonly int[] _lazy;

        public RangeMaximumTree(int length)
        {
            _length = length;
            _maximum = new int[checked(length * 4)];
            _lazy = new int[checked(length * 4)];
        }

        public void Add(int start, int end, int delta) =>
            Add(node: 1, left: 0, right: _length - 1, start, end, delta);

        public int QueryMaximum(int start, int end) =>
            QueryMaximum(node: 1, left: 0, right: _length - 1, start, end);

        private void Add(int node, int left, int right, int start, int end, int delta)
        {
            if (start <= left && right <= end)
            {
                _maximum[node] += delta;
                _lazy[node] += delta;
                return;
            }

            var middle = left + (right - left) / 2;
            if (start <= middle)
            {
                Add(node * 2, left, middle, start, end, delta);
            }

            if (end > middle)
            {
                Add(node * 2 + 1, middle + 1, right, start, end, delta);
            }

            _maximum[node] = _lazy[node] + Math.Max(_maximum[node * 2], _maximum[node * 2 + 1]);
        }

        private int QueryMaximum(int node, int left, int right, int start, int end)
        {
            if (start <= left && right <= end)
            {
                return _maximum[node];
            }

            var middle = left + (right - left) / 2;
            var result = 0;
            if (start <= middle)
            {
                result = QueryMaximum(node * 2, left, middle, start, end);
            }

            if (end > middle)
            {
                result = Math.Max(
                    result,
                    QueryMaximum(node * 2 + 1, middle + 1, right, start, end));
            }

            return _lazy[node] + result;
        }
    }

    private readonly record struct SweepEvent(long X, bool IsStart, long Top, long Bottom);
}
