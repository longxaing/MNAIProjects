using Xunit;

public sealed class CalculatorTests
{
    [Fact]
    public void Add_ReturnsSum() => Assert.Equal(5, Calculator.Add(2, 3));
}