namespace System.Linq;

internal static class EnumerableExtensions
{
    extension(Enumerable)
    {
        // public static IEnumerable<T> Sequence<T>(T start, T endInclusive, T step)
        public static IEnumerable<int> Sequence(int start, int endInclusive, int step)
        {
            if (step == 0)
            {
                throw new ArgumentException("Step cannot be zero.", nameof(step));
            }

            if (step > 0)
            {
                for (int i = start; i <= endInclusive; i += step)
                {
                    yield return i;
                }
            }
            else
            {
                for (int i = start; i >= endInclusive; i += step)
                {
                    yield return i;
                }
            }
        }
    }
}
