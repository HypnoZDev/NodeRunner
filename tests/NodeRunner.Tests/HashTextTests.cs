using NodeRunner.Data.Hashing;

namespace NodeRunner.Tests;

public class HashTextTests
{
    [Theory]
    [InlineData("0x3073333B", 0x3073333Bu)]
    [InlineData("0x3073333b", 0x3073333Bu)]
    [InlineData(" 812790587 ", 812790587u)]
    [InlineData("4294967295", 0xFFFFFFFFu)]
    [InlineData("-1", 0xFFFFFFFFu)]
    [InlineData("-1037", 0xFFFFFBF3u)]
    public void Reads_hashes(string text, uint expected) => Assert.Equal(expected, HashText.TryParse(text));

    [Theory]
    [InlineData("0x")]
    [InlineData("0x123456789")]
    [InlineData("0xZZ")]
    [InlineData("prop_bench_01a")]
    [InlineData("12.5")]
    [InlineData("4294967296")]
    public void Ignores_other_text(string text) => Assert.Null(HashText.TryParse(text));

    [Fact]
    public void Names_and_hashes_match_the_game()
    {
        // Lower case first, as the game does.
        Assert.Equal(Joaat.Hash("prop_bench_01a"), Joaat.Hash("PROP_Bench_01A"));
        Assert.True(HashText.IsName("prop_bench_01a"));
        Assert.False(HashText.IsName("vector3(1, 2, 3)"));
    }

    [Fact]
    public void User_names_file_skips_comments_and_blank_lines()
    {
        var names = new HashNames();

        int count = names.AddLines(["// shaders", "", "  hei_dt1_02_slod  ", "# note", "Prop_X"]);

        Assert.Equal(2, count);
        Assert.Equal("hei_dt1_02_slod", names.TryGet(Joaat.Hash("hei_dt1_02_slod")));
        Assert.Equal("prop_x", names.Describe(Joaat.Hash("prop_x")));
    }
}
