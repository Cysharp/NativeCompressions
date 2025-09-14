using System.Diagnostics;

namespace NativeCompressions.Internal;

internal static class LZ4ActivitySource
{
    internal static readonly ActivitySource Source = new ActivitySource("NativeCompressions.LZ4");

    public static Activity? Start(string name)
    {
        var activity = Source.StartActivity(name);
        return activity;
    }

    public static Activity? Start(string name, ref ActivityContext? linkContext)
    {
        var activity = Source.StartActivity(name);
        if (activity != null)
        {
            if (linkContext != null)
            {
                activity.AddLink(new ActivityLink(linkContext.Value));
            }
            linkContext = activity.Context;
        }
        return activity;
    }

    public static Activity? Start<T>(string name, string tagKey, T? tagValue)
    {
        var activity = Source.StartActivity(name);
        if (activity != null)
        {
            activity.AddTag(tagKey, tagValue);
        }
        return activity;
    }
}
