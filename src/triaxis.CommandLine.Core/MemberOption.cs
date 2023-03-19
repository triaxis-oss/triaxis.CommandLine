namespace triaxis.CommandLine;

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Reflection;

public class MemberOption<T> : Option<T>, IMemberBoundSymbol
{
    public MemberOption(MemberInfo member, OptionAttribute attribute)
        : base(GetNameAndAliases(member, attribute), attribute.Description)
    {
        Member = member;
        Description = attribute.Description;
        IsRequired = attribute.Required;
    }

    public MemberInfo Member { get; }

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
        Member.SetValue(target, parseResult.GetValueOrDefault<T>());
    }

    public void SetValue(object target, OptionResult parseResult)
    {
        Member.SetValue(target, parseResult.GetValueOrDefault<T>());
    }
}
