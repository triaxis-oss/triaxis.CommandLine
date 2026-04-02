namespace triaxis.CommandLine.ObjectOutput;

public interface IOutputStreamProvider
{
    TextWriter GetOutputStream();
}
