using CouplesCash.Api.Domain;

namespace CouplesCash.Api.Tests;

public class PayeeDisplayTests
{
    // Her bank's wording for one Monday (US Bank through SimpleFIN), and what the plan shows.
    private static readonly string[] Monday =
    [
        "Debit Purchase -visa Chick-fil-a #034columbia Mo 10/24 Card 8023",
        "Debit Purchase -visa Andy's - Columbicolumbia Mo 10/23 Card 8023",
        "Debit Purchase -visa Chewy.com 800-672-4399fl 10/24 Card 8023",
        "Debit Purchase -visa Instacart*aldi Instacart.coca 10/24 Card 8023",
        "Debit Purchase -visa Link.com* Simplelink.com Ca 10/25 Card 8023",
        "Debit Purchase -visa Moser's Discountcolumbia Mo 10/24 Card 8023",
        "Debit Purchase -visa Tst*irenes Bbq 573-508-0561mo 10/24 Card 8023",
        "Debit Purchase -visa Tst*irenes Bbq Columbia Mo 10/24 Card 8023",
        "Debit Purchase -visa Valhallas Gate Columbia Mo 10/24 Card 8023",
        "Debit Purchase -visa Sq *the Crossingcolumbia Mo 10/25 Card 8023",
        "Debit Purchase Break Time 3160 Columbia Mo 10/23 Card 2003",
        "Debit Purchase -visa Hy-vee Columbia Columbia Mo 10/25 Card 8023",
        "Debit Purchase -visa Hy-vee Aisles On515-267-2800ia 10/24 Card 8023",
        "Debit Purchase Trader Joe S #80columbia Mo 10/25 Card 8023",
        "Web Authorized Pmt Applecard Gsbank",
        "Phillips 66 - Rocket Columbia Mo 1",
    ];

    [Theory]
    [InlineData(0, "Chick-fil-a")]
    [InlineData(1, "Andy's")]
    [InlineData(2, "Chewy.com")]
    [InlineData(3, "Instacart")]
    [InlineData(4, "Link.com")]
    [InlineData(5, "Moser's Discount")]
    [InlineData(6, "Irenes Bbq")]
    [InlineData(7, "Irenes Bbq")]
    [InlineData(8, "Valhallas Gate")]
    [InlineData(9, "The Crossing")]
    [InlineData(10, "Break Time")]
    [InlineData(11, "Hy-vee")]
    [InlineData(12, "Hy-vee Aisles On")]
    [InlineData(13, "Trader Joe's")]
    [InlineData(14, "Applecard Gsbank")]
    [InlineData(15, "Phillips 66 - Rocket")]
    public void Card_wording_becomes_the_merchant(int row, string expected)
    {
        var cities = PayeeDisplay.LearnCities(Monday);
        Assert.Equal(["Columbia"], cities);
        Assert.Equal(expected, PayeeDisplay.Clean(Monday[row], cities));
    }

    [Fact]
    public void All_caps_is_title_cased_and_nothing_readable_keeps_the_original()
    {
        Assert.Equal("Blue Bottle", PayeeDisplay.Clean("SQ *BLUE BOTTLE OAKLAND CA 10/24", ["Oakland"]));
        Assert.Equal("Netflix.com", PayeeDisplay.Clean("NETFLIX.COM"));
        Assert.Equal("ACME PAYROLL", PayeeDisplay.Clean("ACME PAYROLL").ToUpperInvariant());
        Assert.Equal("12/31", PayeeDisplay.Clean("12/31"));
        Assert.Equal("", PayeeDisplay.Clean(null));
    }
}
