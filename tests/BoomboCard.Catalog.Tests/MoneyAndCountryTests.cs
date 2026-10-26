using BoomboCard.Catalog.Domain.ProductAggregate;
using BoomboCard.SharedKernel;

namespace BoomboCard.Catalog.Tests;

public sealed class MoneyAndCountryTests
{
    /// <summary>
    /// Onyx M7: its pricing engine never rounded, and a real order total was 83.096766 — a
    /// figure that cannot be charged, invoiced or reconciled.
    /// </summary>
    [Fact]
    public void Create_RoundsToCents_HalfUp()
    {
        Assert.Equal(83.10m, Money.Create(83.096766m, "USD").Value.Amount);
        Assert.Equal(0.13m, Money.Create(0.125m, "USD").Value.Amount);
        Assert.Equal(10.00m, Money.Create(9.995m, "USD").Value.Amount);
    }

    [Fact]
    public void Arithmetic_StaysRoundedAtEveryStep()
    {
        var subtotal = Money.Create(10.00m, "USD").Value;

        // 8% tax on $10.00. Onyx computed this in double and stored the raw result.
        var withTax = subtotal.Add(Country.UnitedStates.TaxOn(subtotal));

        Assert.Equal(10.80m, withTax.Amount);
        Assert.Equal("USD", withTax.CurrencyCode);
    }

    [Fact]
    public void Add_AcrossCurrencies_Throws()
    {
        var usd = Money.Create(10m, "USD").Value;
        var aed = Money.Create(10m, "AED").Value;

        Assert.Throws<CurrencyMismatchException>(() => usd.Add(aed));
    }

    [Fact]
    public void Create_RejectsNegativeAndUnsupportedCurrency()
    {
        Assert.Equal("Money.Negative", Money.Create(-1m, "USD").FirstError.Code);
        Assert.Equal("Money.UnsupportedCurrency", Money.Create(1m, "GBP").FirstError.Code);
        Assert.Equal("Money.CurrencyRequired", Money.Create(1m, " ").FirstError.Code);
    }

    [Fact]
    public void Money_IsAValue_NotAReference()
    {
        Assert.Equal(Money.Create(10.00m, "USD").Value, Money.Create(10.00m, "USD").Value);
        Assert.NotEqual(Money.Create(10.00m, "USD").Value, Money.Create(10.00m, "AED").Value);
    }

    /// <summary>
    /// Two countries, two currencies, two different tax rates — so the tax path is actually
    /// exercised rather than only the currency formatting.
    /// </summary>
    [Theory]
    [InlineData("US", "USD", 0.08)]
    [InlineData("ae", "AED", 0.05)]
    public void FromCode_IsCaseInsensitive_AndCarriesCurrencyAndRate(
        string code,
        string expectedCurrency,
        double expectedRate)
    {
        var country = Country.FromCode(code);

        Assert.False(country.IsError);
        Assert.Equal(expectedCurrency, country.Value.CurrencyCode);
        Assert.Equal((decimal)expectedRate, country.Value.TaxRate);
    }

    [Fact]
    public void FromCode_RejectsAnUnsupportedCountry()
    {
        Assert.Equal("Country.NotSupported", Country.FromCode("GB").FirstError.Code);
        Assert.Equal("Country.CodeRequired", Country.FromCode(null).FirstError.Code);
    }

    /// <summary>
    /// Tax on a tax-exclusive catalogue price, at checkout. The displayed ProductPrice never
    /// includes tax, which is why there is no inclusive/exclusive display mode to get wrong.
    /// </summary>
    [Fact]
    public void TaxOn_DiffersByCountry()
    {
        var hundredUsd = Money.Create(100m, "USD").Value;
        var hundredAed = Money.Create(100m, "AED").Value;

        Assert.Equal(8.00m, Country.UnitedStates.TaxOn(hundredUsd).Amount);
        Assert.Equal(5.00m, Country.UnitedArabEmirates.TaxOn(hundredAed).Amount);
    }
}

public sealed class SkuAndStockLevelTests
{
    [Fact]
    public void Sku_NormalisesToUpperCase_SoUniquenessIsAPropertyOfTheValue()
    {
        Assert.Equal("ONX-001", Sku.Create(" onx-001 ").Value.Value);
        Assert.Equal(Sku.Create("ONX-001").Value, Sku.Create("onx-001").Value);
    }

    [Fact]
    public void Sku_RejectsEmptyAndMalformedValues()
    {
        Assert.Equal("Sku.Required", Sku.Create("  ").FirstError.Code);
        Assert.Equal("Sku.InvalidFormat", Sku.Create("-leading-hyphen").FirstError.Code);
        Assert.Equal("Sku.InvalidFormat", Sku.Create("has space").FirstError.Code);
        Assert.Equal("Sku.TooLong", Sku.Create(new string('A', 33)).FirstError.Code);
    }

    [Fact]
    public void StockLevel_CannotBeNegative()
    {
        Assert.Equal("StockLevel.Negative", StockLevel.Create(-1).FirstError.Code);
        Assert.Equal("StockLevel.Insufficient", StockLevel.Create(2).Value.Deduct(3).FirstError.Code);
    }

    /// <summary>One availability rule. Onyx had three, and they disagreed at the boundary.</summary>
    [Theory]
    [InlineData(3, 2, true)]
    [InlineData(3, 3, true)]    // exactly enough IS enough — Onyx M18 said otherwise
    [InlineData(3, 4, false)]
    [InlineData(3, 0, false)]
    [InlineData(0, 1, false)]
    public void CanFulfil_HasOneDefinition(int available, int requested, bool expected) =>
        Assert.Equal(expected, StockLevel.Create(available).Value.CanFulfil(requested));
}
