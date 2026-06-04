namespace Xunit;

[AttributeUsage(AttributeTargets.Method)]
public class FactAttribute : Attribute
{
    public string? DisplayName { get; init; }

    public string? Skip { get; init; }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class TheoryAttribute : FactAttribute { }

[AttributeUsage(AttributeTargets.Method)]
public sealed class NetworkChecksumFactAttribute : FactAttribute
{
    public int[] LocalNetIds { get; }

    public bool ExpectMismatch { get; set; }

    public NetworkChecksumFactAttribute(params int[] localNetIds)
    {
        LocalNetIds = localNetIds;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class InlineDataAttribute : Attribute
{
    public object?[] Data { get; }

    public InlineDataAttribute(params object?[] data)
    {
        Data = data;
    }
}
