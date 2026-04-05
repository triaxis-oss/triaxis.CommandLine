namespace triaxis.CommandLine.Tests;

using triaxis.CommandLine.Invocation;

[TestFixture]
public class TypeExtensionsTests
{
    [Test]
    public void GetIEnumerableElementType_ReturnsElementType_ForIEnumerableT()
    {
        Assert.That(typeof(IEnumerable<int>).GetIEnumerableElementType(), Is.EqualTo(typeof(int)));
    }

    [Test]
    public void GetIEnumerableElementType_ReturnsElementType_ForListT()
    {
        Assert.That(typeof(List<string>).GetIEnumerableElementType(), Is.EqualTo(typeof(string)));
    }

    [Test]
    public void GetIEnumerableElementType_ReturnsElementType_ForArray()
    {
        Assert.That(typeof(string[]).GetIEnumerableElementType(), Is.EqualTo(typeof(string)));
    }

    [Test]
    public void GetIEnumerableElementType_ReturnsNull_ForNonEnumerable()
    {
        Assert.That(typeof(int).GetIEnumerableElementType(), Is.Null);
        Assert.That(typeof(object).GetIEnumerableElementType(), Is.Null);
    }

    [Test]
    public void GetIEnumerableElementType_ReturnsNull_ForByteEnumerableSoBytesAreNotListed()
    {
        Assert.That(typeof(IEnumerable<byte>).GetIEnumerableElementType(), Is.Null);
    }

    [Test]
    public void GetIEnumerableElementType_ReturnsNull_ForStringCharsEvenThoughItImplementsIEnumerableChar()
    {
        Assert.That(typeof(string).GetIEnumerableElementType(), Is.Null);
    }
}
