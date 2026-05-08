namespace triaxis.CommandLine;

/// <summary>
/// Marks a method on a <c>[Command]</c> class as an alternate entry point that runs
/// when its corresponding boolean flag is set on the command line, instead of the
/// command's primary <c>ExecuteAsync</c>/<c>MainAsync</c>.
/// </summary>
/// <remarks>
/// The attribute exposes the method as a <see cref="OptionAttribute">flag option</see>
/// on the command. When the flag is supplied, the source-generated action invokes the
/// annotated method after binding the command's regular arguments and options.
/// <para>
/// Recognised method shapes mirror the primary entry-point shapes — including an
/// optional <see cref="System.Threading.CancellationToken"/> parameter, and (in
/// <em>standalone</em> commands declaring <c>MainAsync</c>) an optional
/// <see cref="IToolBuilder"/> parameter. When multiple action options are supplied
/// on the same invocation, the last one parsed wins.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public class ActionOptionAttribute : CommandlineAttribute
{
    public ActionOptionAttribute()
    {
    }

    public ActionOptionAttribute(string? name)
    {
        Name = name;
    }

    public ActionOptionAttribute(string? name, params string[] aliases)
    {
        Name = name;
        Aliases = aliases;
    }

    public string[]? Aliases { get; set; }
}
