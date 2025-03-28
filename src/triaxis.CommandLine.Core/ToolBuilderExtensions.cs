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


        static void ProcessMemberAttributes(Command cmd, Type type, MemberInfo[]? path = null)
        {
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var (m, attr) in
                from m in members
                from aa in m.GetCustomAttributes()
                let cla = aa as CommandlineAttribute
                where cla is not null
                orderby cla.Order, cla.Name, m.Name
                select (m, cla))
            {
                var memberType = m.GetValueType();
                memberType = Nullable.GetUnderlyingType(memberType) ?? memberType;

                switch (attr)
                {
                    case ArgumentAttribute aa:
                        cmd.AddArgument((Argument)Activator.CreateInstance(typeof(MemberArgument<>).MakeGenericType(memberType), m, attr, path));
                        break;
                    case OptionAttribute oa:
                        cmd.AddOption((Option)Activator.CreateInstance(typeof(MemberOption<>).MakeGenericType(memberType), m, attr, path));
                        break;
                    case OptionsAttribute:
                        var optsPath = path;
                        Array.Resize(ref optsPath, (optsPath?.Length ?? 0) + 1);
                        optsPath[optsPath.Length - 1] = m;
                        ProcessMemberAttributes(cmd, memberType, optsPath);
                        break;
                }
            }
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

                ProcessMemberAttributes(cmd, type);

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
