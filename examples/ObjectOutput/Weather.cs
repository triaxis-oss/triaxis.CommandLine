using System.Data;

namespace ObjectOutput;

public class Weather
{
    public record Forecast(string City, decimal Temperature)
    {
        [ObjectOutput(ObjectFieldVisibility.Extended)]
        public decimal TemperatureF => Temperature * 9 / 5 + 32;
    }

    public class Extension
    {
        public Extension(Forecast forecast)
        {
            ReverseCity = string.Create(forecast.City.Length, forecast.City, (span, s) =>
                {
                    s.CopyTo(span);
                    span.Reverse();
                });
            NegativeTemperature = -forecast.Temperature;
        }

        [ObjectOutput(After = nameof(Forecast.City))]
        public string ReverseCity { get; }
        [ObjectOutput(After = nameof(Forecast.Temperature))]
        public decimal NegativeTemperature { get; }
    }

    public IEnumerable<Forecast> GetForecasts()
    {
        decimal RandomTemp() { return Random.Shared.Next(50, 350) / 10m; }

        return new Forecast[]
        {
            new("Bratislava", RandomTemp()),
            new("Prague", RandomTemp()),
            new("Paris", RandomTemp()),
            new("London", RandomTemp()),
            new("New York", RandomTemp()),
        };
    }
}


[Command("enumerable", Description = "Returns an IEnumerable")]
public class Enumerable : Weather
{
    public IEnumerable<Forecast> Execute() => GetForecasts();
}


[Command("array", Description = "Returns an array")]
public class Array : Weather
{
    public Forecast[] Execute() => GetForecasts().ToArray();
}

[Command("list", Description = "Returns a list")]
public class List : Weather
{
    public IList<Forecast> Execute() => GetForecasts().ToList();
}

[Command("async", Description = "Returns async enumerable")]
public class Async : Weather
{
    public async IAsyncEnumerable<Forecast> ExecuteAsync()
    {
        foreach (var f in GetForecasts())
        {
            await Task.Delay(100);
            yield return f;
        }
    }
}

[Command("task", Description = "Returns async task returning IEnumerable")]
public class AsyncTask : Weather
{
    public async Task<IEnumerable<Forecast>> ExecuteAsync()
    {
        await Task.Delay(100);
        return GetForecasts();
    }
}

[Command("multiple", Description = "Outputs multiple sets")]
public class Multiple : Weather
{
    [Inject]
    private readonly IObjectOutputHandler _output = null!;

    public async Task ExecuteAsync()
    {
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(1000);
            await _output.ProcessOutputAsync(GetForecasts().ToCommandInvocationResult(), default);
        }
    }
}

[Command("tuple", Description = "Tuple output example, with some extra fields")]
public class Tuple : Weather
{
    public IEnumerable<(Forecast, Extension)> Execute()
        => GetForecasts().Select(f => (f, new Extension(f)));
}

[Command("table", Description = "DataTable output example")]
public class DataTableOutput : Weather
{
    public DataTable Execute()
    {
        var dt = new DataTable();
        dt.Columns.Add("City", typeof(string));
        dt.Columns.Add("Temperature", typeof(decimal));
        dt.Columns.Add("TemperatureF", typeof(decimal));
        foreach (var fc in GetForecasts())
        {
            dt.Rows.Add(fc.City, fc.Temperature, fc.TemperatureF);
        }
        return dt;
    }
}

[Command("asynctable", Description = "Async DataTable output example")]
public class AsyncDataTableOutput : Weather
{
    public async Task<DataTable> Execute()
    {
        var dt = new DataTable();
        dt.Columns.Add("City", typeof(string));
        dt.Columns.Add("Temperature", typeof(decimal));
        dt.Columns.Add("TemperatureF", typeof(decimal));
        dt.Columns.Add("Nullable", typeof(int)).AllowDBNull = true;
        foreach (var (i, fc) in GetForecasts().Select((f, i) => (i, f)))
        {
            dt.Rows.Add(fc.City, fc.Temperature, fc.TemperatureF, (i % 2 == 0) ? (i * 100) : null);
        }
        await Task.Delay(100);
        return dt;
    }
}
