namespace triaxis.CommandLine.ObjectOutput.Tests;

[TestFixture]
public class TupleDescriptorTests
{
    public record Forecast(string City, decimal Temperature);
    public record Extra(string Note);

    [Test]
    public void TupleDescriptor_FlattensBothElements()
    {
        var value = (new Forecast("Bratislava", 19.8m), new Extra("n"));
        var descriptor = new DefaultObjectDescriptorProvider<(Forecast, Extra)>().GetDescriptor(value);

        TestContext.Out.WriteLine($"descriptor = {descriptor.GetType()}");
        TestContext.Out.WriteLine($"fields = {string.Join(",", descriptor.Fields.Select(f => f.Name))}");

        Assert.That(descriptor.Fields.Select(f => f.Name),
            Is.EqualTo(new[] { "City", "Temperature", "Note" }));
    }
}
