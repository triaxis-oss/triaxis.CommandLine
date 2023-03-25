namespace triaxis.CommandLine;

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Reflection;

public class MemberOption<T> : Option<T>, IMemberBoundSymbol
{
    public MemberOption(MemberInfo member, OptionAttribute attribute, MemberInfo[] path)
        : base(GetNameAndAliases(member, attribute), attribute.Description)
    {
        Member = member;
        Description = attribute.Description;
        IsRequired = attribute.Required;
        Path = path;
    }

    public MemberInfo Member { get; }
    public MemberInfo[]? Path { get; }

    private static string[] GetNameAndAliases(MemberInfo member, OptionAttribute opt)
    {
        var name = opt.Name ?? member.Name;
        if (opt.Aliases == null)
        {
            return new[] { name };
        }
        else
        {
            var res = new string[opt.Aliases.Length + 1];
            res[0] = name;
            opt.Aliases.CopyTo(res, 1);
            return res;
        }
    }

    public void SetValue(object target, ArgumentResult parseResult)
    {
        Member.SetValue(Path.GetOrCreateValues(target), parseResult.GetValueOrDefault<T>());
    }

    public void SetValue(object target, OptionResult parseResult)
    {
        Member.SetValue(Path.GetOrCreateValues(target), parseResult.GetValueOrDefault<T>());
    }
}
