namespace triaxis.CommandLine;

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Reflection;

class MemberArgument<T> : Argument<T>, IMemberBoundSymbol
{
    public MemberArgument(MemberInfo member, ArgumentAttribute attribute, MemberInfo[] path)
        : base(attribute.Name ?? member.Name)
    {
        Member = member;
        Description = attribute.Description;
        Path = path;

        if (attribute.RequiredIsSet)
        {
            if (attribute.Required && Arity.MinimumNumberOfValues == 0)
            {
                Arity = new ArgumentArity(1, Arity.MaximumNumberOfValues);
            }
            else if (!attribute.Required && Arity.MinimumNumberOfValues == 1)
            {
                Arity = new ArgumentArity(0, Arity.MaximumNumberOfValues);
            }
        }
    }

    public MemberInfo Member { get; }
    public MemberInfo[]? Path { get; }

    public void SetValue(object target, ArgumentResult parseResult)
    {
        Member.SetValue(Path.GetOrCreateValues(target), parseResult.GetValueOrDefault<T>());
    }

    public void SetValue(object target, OptionResult parseResult)
    {
        Member.SetValue(Path.GetOrCreateValues(target), parseResult.GetValueOrDefault<T>());
    }
}
