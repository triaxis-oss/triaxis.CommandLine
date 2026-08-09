namespace triaxis.CommandLine.ObjectOutput;

public enum ObjectFieldVisibility
{
    /// <summary>Shown by every format.</summary>
    Standard,

    /// <summary>Shown by <c>Wide</c> and by the data formats, hidden from the default table.</summary>
    Extended,

    /// <summary>
    /// Hidden from every table, still emitted by the data formats (<c>Json</c>, <c>Yaml</c>).
    /// </summary>
    Internal,

    /// <summary>
    /// Dropped from the descriptor entirely, so no format can emit it.
    /// </summary>
    /// <remarks>
    /// Unlike the levels above this is not a filter — the field never reaches
    /// <see cref="IObjectDescriptor.Fields"/>, so custom formatters and anything walking a
    /// descriptor cannot surface it either. That is the difference that matters when the
    /// point is to keep a value out of the output rather than merely out of the way.
    ///
    /// <para><c>Raw</c> is the exception, and deliberately so: it writes
    /// <see cref="object.ToString"/> and never consults a descriptor, so a record's
    /// compiler-generated <c>ToString</c> still prints every property. Override
    /// <c>ToString</c> on a type that must not leak under <c>-o raw</c>.</para>
    /// </remarks>
    Hidden,
}
