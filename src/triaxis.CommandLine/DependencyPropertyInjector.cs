namespace triaxis.CommandLine;

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public class DependencyPropertyInjector : IPropertyInjector
{
    private readonly IServiceProvider _serviceProvider;

    public DependencyPropertyInjector(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void InjectProperties(object target)
    {
        foreach (var memberInfo in target.GetType().GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (memberInfo.GetCustomAttribute<InjectAttribute>() is InjectAttribute attr)
            {
                var type = attr.Type ?? memberInfo.GetValueType();
                // special case
                if (type == typeof(ILogger))
                {
                    type = typeof(ILogger<>).MakeGenericType(target.GetType());
                }
                var service = _serviceProvider.GetRequiredService(type);
                memberInfo.SetValue(target, service);
            }
        }
    }
}
