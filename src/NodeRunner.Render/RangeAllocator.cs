namespace NodeRunner.Render;

// Hands out ranges of a fixed space (bytes of a buffer, slots of a table): first fit, freed ranges joined to their
// free neighbours.
internal sealed class RangeAllocator(long size)
{
    private readonly List<(long Start, long Length)> _free = [(0, size)];

    public long Size { get; } = size;

    public long Used { get; private set; }

    public long? Allocate(long length, long alignment = 1)
    {
        if (length <= 0)
            return null;
        for (int i = 0; i < _free.Count; i++)
        {
            var (start, available) = _free[i];
            long aligned = (start + alignment - 1) / alignment * alignment;
            long end = aligned + length;
            if (end > start + available)
                continue;
            _free.RemoveAt(i);
            int at = i;
            if (aligned > start)
                _free.Insert(at++, (start, aligned - start));
            if (start + available > end)
                _free.Insert(at, (end, start + available - end));
            Used += length;
            return aligned;
        }
        return null;
    }

    public void Free(long start, long length)
    {
        Used -= length;
        int lo = 0, hi = _free.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_free[mid].Start < start)
                lo = mid + 1;
            else
                hi = mid;
        }
        int i = lo;
        _free.Insert(i, (start, length));
        if (i + 1 < _free.Count && _free[i].Start + _free[i].Length == _free[i + 1].Start)
        {
            _free[i] = (_free[i].Start, _free[i].Length + _free[i + 1].Length);
            _free.RemoveAt(i + 1);
        }
        if (i > 0 && _free[i - 1].Start + _free[i - 1].Length == _free[i].Start)
        {
            _free[i - 1] = (_free[i - 1].Start, _free[i - 1].Length + _free[i].Length);
            _free.RemoveAt(i);
        }
    }
}
