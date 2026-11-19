using HhAutoApplier.Application;
using HhAutoApplier.Application.Queries.About;
using Microsoft.Extensions.Options;

namespace HhAutoApplier.Tests;

// #124: метка тестового стенда в /api/version и строке бота
public class StageLabelTests
{
    private static Task<HhAutoApplier.Application.Dtos.BuildVersionDto> Version(string? label) =>
        new GetVersionQueryHandler(Options.Create(new StageOptions { Label = label })).Handle(new GetVersionQuery());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NoLabel_NotStage(string? label)
    {
        Assert.Null((await Version(label)).Stage);
        Assert.False(new StageOptions { Label = label }.Enabled);
    }

    [Fact]
    public async Task Label_InVersionAndBotLine()
    {
        Assert.Equal("копия прода 18.11", (await Version("  копия прода 18.11 ")).Stage);
        Assert.Equal("🧪 STAGE · копия прода 18.11", new StageOptions { Label = " копия прода 18.11" }.BotLine);
    }
}
