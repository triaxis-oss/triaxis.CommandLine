namespace triaxis.CommandLine;

class CommandNode
{
    private SortedDictionary<string, CommandNode>? _children;
    private readonly Command _command;
    private bool _added;

    public CommandNode(Command command)
    {
        _command = command;
    }

    public Command GetCommand(string[] path)
    {
        var node = this;

        foreach (string element in path)
        {
            node = node.GetOrCreateChild(element);
        }

        return node._command;
    }

    private CommandNode GetOrCreateChild(string name)
    {
        var children = _children ??= new SortedDictionary<string, CommandNode>(StringComparer.OrdinalIgnoreCase);
        if (!children.TryGetValue(name, out var cmd))
        {
            children[name] = cmd = new CommandNode(new Command(name));
        }
        return cmd;
    }

    public void Realize()
    {
        if (_children != null)
        {
            foreach (var child in _children.Values)
            {
                if (!child._added)
                {
                    child._added = true;
                    _command.AddCommand(child._command);
                }
                child.Realize();
            }
        }
    }
}
