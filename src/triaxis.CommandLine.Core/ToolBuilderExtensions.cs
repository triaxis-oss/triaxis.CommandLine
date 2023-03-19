namespace triaxis.CommandLine;

using System.CommandLine;
using System.CommandLine.Hosting;
using System.CommandLine.Parsing;
using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public static class ToolBuilderExtensions
{
    public static IToolBuilder AddCommandsFromAssembly(this IToolBuilder builder)
        => builder.AddCommandsFromAssembly(Assembly.GetCallingAssembly());

    public static IToolBuilder AddCommandsFromAssembly(this IToolBuilder builder, Assembly assembly)
    {
        static Type UnwrapNullable(Type type)
            => Nullable.GetUnderlyingType(type) ?? type;
        static Argument NewMemberArgument(MemberInfo mi, ArgumentAttribute attr)
            => (Argument)Activator.CreateInstance(typeof(MemberArgument<>).MakeGenericType(UnwrapNullable(mi.GetValueType())), mi, attr)!;
        static Option NewMemberOption(MemberInfo mi, OptionAttribute attr)
            => (Option)Activator.CreateInstance(typeof(MemberOption<>).MakeGenericType(UnwrapNullable(mi.GetValueType())), mi, attr)!;

        Command CommandFromAttribute(CommandAttribute attr, Type? type = null)
        {
            var cmd = builder.GetCommand(attr.Path);
            cmd.Description ??= attr.Description ?? type?.GetCustomAttribute<DescriptionAttribute>()?.Description;

            if (attr.Aliases != null)
            {
                foreach (var alias in attr.Aliases)
                {
                    cmd.AddAlias(alias);
                }
            }

            return cmd;
        }

        foreach (var attr in assembly.GetCustomAttributes<CommandAttribute>())
        {
            CommandFromAttribute(attr);
        }

        var types = new List<Type>();

        foreach (var type in assembly.GetExportedTypes())
        {
            foreach (var attr in type.GetCustomAttributes<CommandAttribute>())
            {
                var cmd = CommandFromAttribute(attr, type);
                var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                foreach (var arg in
                    from m in members
                    from aa in m.GetCustomAttributes<ArgumentAttribute>()
                    orderby aa.Order, aa.Name, m.Name
                    select NewMemberArgument(m, aa))
                {
                    cmd.AddArgument(arg);
                }

                foreach (var opt in
                    from m in members
                    from oa in m.GetCustomAttributes<OptionAttribute>()
                    orderby oa.Order, oa.Name, m.Name
                    select NewMemberOption(m, oa))
                {
                    cmd.AddOption(opt);
                }

                cmd.Handler = new DependencyCommandHandler(type, attr);
                types.Add(type);
            }
        }

        if (types.Any())
        {
            builder.ConfigureServices(services =>
            {
                foreach (var type in types)
                {
                    services.AddTransient(type);
                }
            });
        }
        return builder;
    }
}
