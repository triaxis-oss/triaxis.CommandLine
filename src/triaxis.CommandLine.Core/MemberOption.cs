namespace triaxis.CommandLine;

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Reflection;

public class MemberOption<T> : Option<T>, IMemberBoundSymbol
{
    public MemberOption(MemberInfo member, OptionAttribute attribute, MemberInfo[] path)
        : base(GetName(member, attribute), GetAliases(member, attribute))
    {
        Member = member;
        Description = attribute.Description;
        Required = attribute.RequiredIsSet ? attribute.Required : member.IsMemberRequired();
        Path = path;
    }

    public MemberInfo Member { get; }
    public MemberInfo[]? Path { get; }

    private static string GetName(MemberInfo member, OptionAttribute opt)
        => opt.Name ?? member.Name;

    private static string[] GetAliases(MemberInfo member, OptionAttribute opt)
        => opt.Aliases ?? Array.Empty<string>();

    public void SetValue(object target, ArgumentResult parseResult)
    {
        Member.SetValue(Path.GetOrCreateValues(target), parseResult.GetValueOrDefault<T>());
    }

    public void SetValue(object target, OptionResult parseResult)
    {
        Member.SetValue(Path.GetOrCreateValues(target), parseResult.GetValueOrDefault<T>());
    }
}
