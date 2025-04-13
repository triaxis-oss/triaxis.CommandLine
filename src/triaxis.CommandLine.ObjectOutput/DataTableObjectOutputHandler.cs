using System.CommandLine;
using System.CommandLine.Invocation;
using System.Data;
using System.Runtime.InteropServices;

namespace triaxis.CommandLine.ObjectOutput;

class DataTableObjectOutputHandler : IObjectOutputHandler<DataTable>
{
    private readonly DefaultObjectOutputHandler<DataRow> _rowHandler;

    class AsyncContextDescriptorProvider : IObjectDescriptorProvider<DataRow>
    {
        public static readonly IObjectDescriptorProvider<DataRow> Instance = new AsyncContextDescriptorProvider();

        private static readonly AsyncLocal<IObjectDescriptor> s_current = new AsyncLocal<IObjectDescriptor>();

        public static ValueTask With(IObjectDescriptor descriptor, Func<ValueTask> action)
        {
            var prev = s_current.Value;
            s_current.Value = descriptor;
            try
            {
                return action();
            }
            finally
            {
                s_current.Value = prev;
            }
        }

        public IObjectDescriptor GetDescriptor(DataRow? instance) => s_current.Value!;
    }

    public DataTableObjectOutputHandler(IObjectFormatterProvider formatterProvider, IConsole console)
    {
        _rowHandler = new DefaultObjectOutputHandler<DataRow>(AsyncContextDescriptorProvider.Instance, formatterProvider, console);
    }

    class DataTableCommandInvocationResult(
        ICommandInvocationResult<DataTable> cir
        ) : ICommandInvocationResult<DataRow>
    {
        public bool IsCollection => true;

        public void Apply(InvocationContext context)
            => cir.Apply(context);

        public Task EnsureCompleteAsync(CancellationToken cancellationToken)
            => cir.EnsureCompleteAsync(cancellationToken);

        public Task EnumerateResultsAsync(Func<DataRow, ValueTask> processElement, Func<ValueTask>? flushHint, CancellationToken cancellationToken)
            => cir.EnumerateResultsAsync(async tbl =>
            {
                if (tbl is not null)
                {
                    await AsyncContextDescriptorProvider.With(new DataTableDescriptor(tbl), async () =>
                    {
                        foreach (DataRow row in tbl.Rows)
                        {
                            await processElement(row);
                        }
                    });
                }
            }, flushHint, cancellationToken);
    }

    public Task ProcessOutputAsync(ICommandInvocationResult<DataTable> cir, CancellationToken cancellationToken)
        => _rowHandler.ProcessOutputAsync(new DataTableCommandInvocationResult(cir), cancellationToken);

    public Task ProcessOutputAsync(ICommandInvocationResult cir, CancellationToken cancellationToken)
        => ProcessOutputAsync((ICommandInvocationResult<DataTable>)cir, cancellationToken);
}
