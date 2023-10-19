namespace ObjectOutput;

public class Weather
{
    public record Forecast(string City, decimal Temperature);

    public IEnumerable<Forecast> GetForecasts()
    {
        decimal RandomTemp() { return Random.Shared.Next(5, 35) / 10m; }

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
