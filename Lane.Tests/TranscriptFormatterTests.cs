using Lane.Core.Agent;
using Lane.Core.Context;
using Lane.Core.Identity;
using Lane.Core.Messages;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// Every timestamp Lane reads comes through the formatter, so the display offset lives
/// there rather than at each call site — otherwise one caller forgetting it hands her a
/// clock that disagrees with every other line in her prompt.
/// </summary>
public sealed class TranscriptFormatterTests
{
    private static readonly SurfaceId Surface = new("terminal");
    private static readonly SessionId Session = new(Surface, SessionKind.Text, "local");
    private static readonly DateTimeOffset Noon = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static TranscriptFormatter Formatter(TimeSpan offset) =>
        new(Options.Create(new AgentOptions { DisplayOffset = offset }));

    [Fact]
    public void FormatTime_Applies_The_Display_Offset()
    {
        string stamp = Formatter(TimeSpan.FromHours(-5))
            .FormatTime(Noon, new TranscriptFormatOptions(IncludeRelativeTime: false));

        Assert.Equal("2026/09/04 07:00:00", stamp);
    }

    [Fact]
    public void Transcript_Lines_Use_The_Same_Offset()
    {
        Participant alice = new(new ParticipantId(Surface, "alice"), "alice");

        string text = Formatter(TimeSpan.FromHours(2)).Format(
            [LaneMessage.User(Session, alice, "hi", Noon)],
            new TranscriptFormatOptions(IncludeRelativeTime: false));

        Assert.Contains("[2026/09/04 14:00:00]", text);
    }
}
