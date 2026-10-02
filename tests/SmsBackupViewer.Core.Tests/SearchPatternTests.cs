using SmsBackupViewer.Core;

namespace SmsBackupViewer.Core.Tests;

public class SearchPatternTests
{
    private static bool Matches(string pattern, string text) => SearchPattern.Parse(pattern)!.IsMatch(text);

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void BlankTermParsesToNull(string? term) => Assert.Null(SearchPattern.Parse(term));

    [Fact]
    public void PlainTermIsCaseInsensitiveSubstring()
    {
        var pattern = SearchPattern.Parse("  Pay ")!;
        Assert.False(pattern.HasWildcards);
        Assert.True(pattern.IsMatch("did you make the PAYMENT?"));
        Assert.False(pattern.IsMatch("nothing here"));
    }

    [Theory]
    [InlineData("pay*ment", "Did you make the payment?", true)]
    [InlineData("pay*ment", "pay the first installment", true)]
    [InlineData("pay*ment", "payme", false)]
    [InlineData("b?t", "I'll BET you", true)]
    [InlineData("b?t", "boat", false)]
    [InlineData("b??t", "boat", true)]
    [InlineData("*", "anything", true)]
    [InlineData("a**b", "a then b", true)]
    [InlineData("meet*tomorrow", "meet you\ntomorrow", true)]
    public void WildcardsMatchAnywhereInTheText(string pattern, string text, bool expected)
        => Assert.Equal(expected, Matches(pattern, text));

    [Fact]
    public void EscapedWildcardsAreLiteral()
    {
        var question = SearchPattern.Parse(@"why\?")!;
        Assert.False(question.HasWildcards);
        Assert.True(question.IsMatch("but why?"));
        Assert.False(question.IsMatch("why not"));

        Assert.True(Matches(@"5\*", "rated 5* by everyone"));
        Assert.False(Matches(@"5\*", "rated 55 by everyone"));
    }

    [Fact]
    public void RegexCharactersAreTakenLiterally()
    {
        Assert.True(Matches("$5.00*", "that's $5.00 each"));
        Assert.False(Matches("$5.00*", "that's $5x00 each"));
        Assert.True(Matches("(555)*", "call (555) 867-5309"));
    }

    [Fact]
    public void NumberPatternIgnoresFormatting()
    {
        var pattern = SearchPattern.ParseNumber("555-???-5309")!;
        Assert.True(pattern.IsMatch(AddressKey.StripFormatting("+1 (555) 867-5309")));
        Assert.False(pattern.IsMatch(AddressKey.StripFormatting("+1 (555) 867-0000")));
    }
}
