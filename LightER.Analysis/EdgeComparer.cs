using LightER.Analysis.Dtos;

namespace LightER.Analysis
{
    internal sealed class EdgeComparer : IEqualityComparer<EdgeDto>
    {
        public bool Equals(EdgeDto? x, EdgeDto? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return string.Equals(x.From, y.From, StringComparison.Ordinal)
                && string.Equals(x.To, y.To, StringComparison.Ordinal)
                && string.Equals(x.Rel, y.Rel, StringComparison.Ordinal);
        }

        public int GetHashCode(EdgeDto obj)
        {
            return HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.From),
                StringComparer.Ordinal.GetHashCode(obj.To),
                StringComparer.Ordinal.GetHashCode(obj.Rel));
        }
    }
}