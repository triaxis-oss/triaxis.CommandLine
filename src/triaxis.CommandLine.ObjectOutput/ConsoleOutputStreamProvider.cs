namespace triaxis.CommandLine.ObjectOutput;

class ConsoleOutputStreamProvider : IOutputStreamProvider
{
    public TextWriter GetOutputStream() => Console.Out;
}
