using ClassicUO.Configuration;
using ClassicUO.Game.Managers;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Managers;

public class LocalTranslationServiceTests
{
    [Theory]
    [InlineData("Welcome to Britain", true)]
    [InlineData("Добро пожаловать", false)]
    [InlineData("~1_AMOUNT~", false)]
    [InlineData("", false)]
    public void ShouldTranslate_OnlyQueuesTextContainingLatinLetters(string source, bool expected)
    {
        LocalTranslationService.ShouldTranslate(source).Should().Be(expected);
    }

    [Theory]
    [InlineData("Привет, мир", true)]
    [InlineData("Hello world", false)]
    [InlineData("[bank Привет", false)]
    [InlineData(".command Привет", false)]
    public void ShouldTranslateOutgoingSpeech_OnlyQueuesNonCommandCyrillicSpeech(string source, bool expected)
    {
        LocalTranslationService.ShouldTranslateOutgoingSpeech(source).Should().Be(expected);
    }

    [Fact]
    public void CreateCacheKey_ChangesWhenScenarioChanges()
    {
        var profile = new Profile();

        string chatKey = LocalTranslationService.CreateCacheKey("Hello", TranslationScenario.Chat, profile);
        string gumpKey = LocalTranslationService.CreateCacheKey("Hello", TranslationScenario.Gump, profile);

        chatKey.Should().NotBe(gumpKey);
    }

    [Fact]
    public void CreateCacheKey_SeparatesStaticWorldObjectsFromItemNames()
    {
        var profile = new Profile();

        LocalTranslationService.CreateCacheKey("bonfire", TranslationScenario.ItemName, profile)
            .Should().NotBe(LocalTranslationService.CreateCacheKey("bonfire", TranslationScenario.StaticWorldObject, profile));
    }

    [Fact]
    public void CreateCacheKey_ChangesWhenTargetLanguageChanges()
    {
        var russian = new Profile { LocalTranslationTargetLanguage = "Russian" };
        var german = new Profile { LocalTranslationTargetLanguage = "German" };

        LocalTranslationService.CreateCacheKey("Hello", TranslationScenario.Chat, russian)
            .Should().NotBe(LocalTranslationService.CreateCacheKey("Hello", TranslationScenario.Chat, german));
    }
}
