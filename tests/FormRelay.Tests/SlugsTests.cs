using FormRelay.Api;

namespace FormRelay.Tests;

public class SlugsTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("service-request")]
    [InlineData("form-2024-q1")]
    [InlineData("007")]
    public void Accepts_lowercase_words_joined_by_single_dashes(string slug) => Assert.True(Slugs.IsValid(slug));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--dash")]
    [InlineData("-")]
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData("under_score")]
    [InlineData("dot.dot")]
    [InlineData("new\nline")]
    [InlineData("trailing-newline\n")]
    [InlineData("ünïcode")]
    public void Rejects_everything_else(string? slug) => Assert.False(Slugs.IsValid(slug));

    [Fact]
    public void Enforces_the_length_cap_without_scanning_huge_input()
    {
        Assert.True(Slugs.IsValid(new string('a', Slugs.MaxLength)));
        Assert.False(Slugs.IsValid(new string('a', Slugs.MaxLength + 1)));
        Assert.False(Slugs.IsValid(new string('a', 5_000_000) + "!"));   // returns at once, no matching
    }

    [Fact]
    public void Matches_the_old_regex_for_ordinary_input()
    {
        var old = new System.Text.RegularExpressions.Regex("^[a-z0-9]+(-[a-z0-9]+)*$");
        foreach (var s in new[] { "a", "ab-cd", "a-b-c", "a--b", "-a", "a-", "A", "a b", "0-0", "x9-y8" })
            Assert.Equal(old.IsMatch(s), Slugs.IsValid(s));
    }
}
