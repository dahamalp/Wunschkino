using Wunschkino.Library;

namespace Wunschkino.Tests.Library;

public class NaturalTitleComparerTests
{
    private static string[] Sorted(params string[] titles) => titles.Order(NaturalTitleComparer.Instance).ToArray();

    [Fact]
    public void NumbersAreComparedByValue()
    {
        Assert.Equal(["Staffel 2", "Staffel 10", "Staffel 100"], Sorted("Staffel 100", "Staffel 2", "Staffel 10"));
    }

    [Fact]
    public void SequelsStayInOrder()
    {
        Assert.Equal(["Rocky", "Rocky 2", "Rocky 10"], Sorted("Rocky 10", "Rocky", "Rocky 2"));
    }

    [Fact]
    public void LeadingZerosDoNotChangeTheValue_ButGiveAStableOrder()
    {
        Assert.Equal(["Folge 2", "Folge 02", "Folge 10"], Sorted("Folge 10", "Folge 02", "Folge 2"));
    }

    [Fact]
    public void TitlesStartingWithANumber_ComeBeforeLetters()
    {
        Assert.Equal(["2 Fast 2 Furious", "10 Cloverfield Lane", "Arcane"], Sorted("Arcane", "10 Cloverfield Lane", "2 Fast 2 Furious"));
    }

    [Fact]
    public void TextWithoutNumbers_IsOrderedIgnoringCase()
    {
        Assert.Equal(["arcane", "Berserk", "Cowboy Bebop"], Sorted("Cowboy Bebop", "Berserk", "arcane"));
    }

    [Fact]
    public void MultipleNumbersInOneTitle_AreComparedLeftToRight()
    {
        Assert.Equal(["S1 E2", "S1 E10", "S2 E1"], Sorted("S2 E1", "S1 E10", "S1 E2"));
    }

    [Fact]
    public void VeryLongNumbers_DoNotOverflow()
    {
        var small = "Folge 99999999999999999999999999999";
        var large = "Folge 100000000000000000000000000000";

        Assert.True(NaturalTitleComparer.Instance.Compare(small, large) < 0);
    }

    [Fact]
    public void EqualTitlesCompareAsEqual_AndNullSortsFirst()
    {
        Assert.Equal(0, NaturalTitleComparer.Instance.Compare("Arcane", "arcane"));
        Assert.True(NaturalTitleComparer.Instance.Compare(null, "Arcane") < 0);
        Assert.True(NaturalTitleComparer.Instance.Compare("Arcane", null) > 0);
    }
}
