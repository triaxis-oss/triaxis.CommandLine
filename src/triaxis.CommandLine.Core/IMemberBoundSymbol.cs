namespace triaxis.CommandLine;

using System.CommandLine.Parsing;
using System.Reflection;

interface IMemberBoundSymbol
{
    MemberInfo Member { get; }
    MemberInfo[]? Path { get; }
    void SetValue(object target, ArgumentResult parseResult);
    void SetValue(object target, OptionResult parseResult);

    MemberInfo GetRootMember() => Path?.FirstOrDefault() ?? Member;
}
