namespace triaxis.CommandLine;

using System.CommandLine.Parsing;
using System.Reflection;

interface IMemberBoundSymbol
{
    MemberInfo Member { get; }
    void SetValue(object target, ArgumentResult parseResult);
    void SetValue(object target, OptionResult parseResult);
}
