namespace FloatNotify;

// All geometry is the visible glass face, in physical pixels of the target monitor.
internal static class CardPlacement
{
    internal readonly record struct Block(Rectangle Face, bool Whole, bool Detached = false);

    internal static Rectangle? Find(Rectangle area, Size size, double scale, bool done,
        IReadOnlyList<Block> blocks, Point? preferred = null, bool useSideSpace = false)
    {
        int Gap(int dip) => (int)Math.Ceiling(dip * scale);
        var center = new Point(area.Left + (area.Width-size.Width)/2, area.Top + (area.Height-size.Height)/2);
        int inset = Gap(6), header = Gap(45), step = Gap(16);
        var bounds = Rectangle.Inflate(area, -inset, -inset);
        // Keep a reachable strip for the overflow control, including on short displays.
        bounds.Height = Math.Max(1, bounds.Height - Gap(43));
        bool Fits(Rectangle candidate, bool overlap)
        {
            if (!bounds.Contains(candidate)) return false;
            foreach (var block in blocks)
            {
                if (!candidate.IntersectsWith(block.Face)) continue;
                if (!overlap || done || block.Whole || block.Detached ||
                    candidate.Top < block.Face.Top + header || candidate.Left < block.Face.Left + step)
                    return false;
            }
            return true;
        }
        Rectangle At(Point location) => new(location, size);
        if (preferred is {} old && Fits(At(old), true)) return At(old);
        var first = At(center);
        if (Fits(first, false)) return first;

        var stack = blocks.Where(b => !b.Detached).ToArray();
        foreach (var b in stack.Reverse().Concat(blocks.Where(b => b.Detached)))
        {
            var below = new Rectangle(center.X, b.Face.Bottom + Gap(8), size.Width, size.Height);
            var cascade = new Rectangle(b.Face.Left + step, b.Face.Top + header, size.Width, size.Height);
            if (stack.Length < 2 && Fits(below, false)) return below;
            if (!b.Detached && !b.Whole && Fits(cascade, true)) return cascade;
            if (Fits(below, false)) return below;
        }
        // A protected completion or editor can leave more usable room above than below.
        foreach (var b in blocks.OrderBy(b => b.Face.Top))
        {
            var above = new Rectangle(center.X, b.Face.Top - Gap(8) - size.Height, size.Width, size.Height);
            if (Fits(above, false)) return above;
        }
        // Very short displays cannot always center a full-height card above the reserved strip.
        var top = new Rectangle(center.X, bounds.Top, size.Width, size.Height);
        if(Fits(top,false))return top;
        if(useSideSpace){
            // An explicitly opened item can use a free side column on a wide monitor.
            var edges=new[]{center.X,bounds.Left,bounds.Right-size.Width}
                .Concat(blocks.SelectMany(b=>new[]{b.Face.Left-size.Width-Gap(8),b.Face.Right+Gap(8)})).Distinct().ToArray();
            var rows=new[]{center.Y,bounds.Top,bounds.Bottom-size.Height}
                .Concat(blocks.SelectMany(b=>new[]{b.Face.Top-size.Height-Gap(8),b.Face.Bottom+Gap(8)})).Distinct();
            foreach(int y in rows)foreach(int x in edges){
                var side=new Rectangle(x,y,size.Width,size.Height);
                if(Fits(side,false))return side;
            }
        }
        return null;
    }
}

