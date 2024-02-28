namespace triaxis.CommandLine.ObjectOutput;

internal interface IObjectFieldOrdering : IObjectField
{
    string? Before { get; }
    string? After { get; }
}
