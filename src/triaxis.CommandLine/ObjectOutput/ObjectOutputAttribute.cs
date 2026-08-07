namespace triaxis.CommandLine.ObjectOutput;

[AttributeUsage(AttributeTargets.Property)]
public class ObjectOutputAttribute : Attribute
{
    public ObjectOutputAttribute()
    {
    }

    public ObjectOutputAttribute(ObjectFieldVisibility visibility)
    {
        Visibility = visibility;
    }

    public string? Before { get; set; }
    public string? After { get; set; }
    public ObjectFieldVisibility? Visibility { get; set; }

    /// <summary>
    /// Format string handed to <see cref="IFormattable.ToString(string, IFormatProvider)"/>
    /// when rendering this field as text, e.g. <c>"F1"</c> or <c>"yyyy-MM-dd"</c>. Always
    /// applied with the invariant culture so output does not vary with the host locale.
    /// </summary>
    /// <remarks>
    /// Affects the textual formats (<c>Table</c>, <c>Wide</c>). The data formats
    /// (<c>Json</c>, <c>Yaml</c>) keep the canonical representation of the value's type,
    /// so a consumer parsing them still sees a number as a number and a date as a date.
    /// </remarks>
    public string? Format { get; set; }
}
