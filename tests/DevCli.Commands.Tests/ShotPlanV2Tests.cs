using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class ShotPlanV2Tests : IDisposable
{
    private readonly string _tmp;

    public ShotPlanV2Tests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-planv2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmp, recursive: true);
        }
        catch
        {
        }
    }

    private const string Head = """
        {"schemaVersion":SCHEMA,"deviceFamily":"Desktop","minimumWidth":8,"minimumHeight":8,"maximumFileSizeBytes":1000000,
         "minimumShotsPerLocale":1,"maximumShotsPerLocale":10,
         "locales":[{"tag":"en-US"}],
        """;

    private const string OneShot = """
        "shots":[
          {"id":"a","order":1,"fileName":"01-a.png","caption":{"en-US":"Cap a"},"expectedMarkerText":{"en-US":"M a"}STEPS}]}
        """;

    private string WritePlan(string schema, string steps, string launch = "")
    {
        string path = Path.Combine(_tmp, "ScreenshotPlan.json");
        File.WriteAllText(path, Head.Replace("SCHEMA", schema) + launch + OneShot.Replace("STEPS", steps));
        return path;
    }

    [Fact]
    public void V1Loads_WithEmptyStepsAndNullLaunch()
    {
        var plan = StoreShots.LoadPlan(WritePlan("1", string.Empty), ["en-US"]);

        Assert.Equal(1, plan.SchemaVersion);
        Assert.Null(plan.Launch);
        Assert.Empty(plan.Shots[0].Steps);
    }

    [Fact]
    public void V2Loads_LaunchAndAllStepKinds()
    {
        string launch = """
            "launch":{"exe":"C:\\Apps\\MyApp.exe","args":"--smoke","env":{"MYAPP_THEME":"Dark"}},
            """;
        string steps = """
            ,"steps":[
              {"action":"click","target":"NavHome","timeoutSec":20},
              {"action":"type","target":"SearchBox","text":"hello"},
              {"action":"keys","text":"{Tab}{Enter}"},
              {"action":"wait","target":"ResultsList","timeoutSec":45},
              {"action":"wait","timeoutSec":5}]
            """;
        var plan = StoreShots.LoadPlan(WritePlan("2", steps, launch), ["en-US"]);

        Assert.Equal(2, plan.SchemaVersion);
        Assert.Equal("C:\\Apps\\MyApp.exe", plan.Launch!.Exe);
        Assert.Equal("--smoke", plan.Launch.Args);
        Assert.Equal("Dark", plan.Launch.Env["MYAPP_THEME"]);
        Assert.Equal(5, plan.Shots[0].Steps.Count);
        Assert.Equal(new StoreShots.ShotStep("click", "NavHome", "", 20), plan.Shots[0].Steps[0]);
        Assert.Equal(new StoreShots.ShotStep("wait", "", "", 5), plan.Shots[0].Steps[4]);
    }

    [Fact]
    public void V2_ClampsTimeouts()
    {
        string steps = @",""steps"":[{""action"":""wait"",""timeoutSec"":9999}]";
        var plan = StoreShots.LoadPlan(WritePlan("2", steps), ["en-US"]);

        Assert.Equal(300, plan.Shots[0].Steps[0].TimeoutSec);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("0")]
    public void BadSchemaVersion_Throws(string schema)
    {
        string path = WritePlan(schema, string.Empty);
        var ex = Assert.Throws<Core.DecoException>(() => StoreShots.LoadPlan(path, ["en-US"]));
        Assert.Equal("shots-plan", ex.Reason);
    }

    [Fact]
    public void UnknownAction_Throws()
    {
        string path = WritePlan("2", @",""steps"":[{""action"":""drag"",""target"":""X""}]");
        var ex = Assert.Throws<Core.DecoException>(() => StoreShots.LoadPlan(path, ["en-US"]));
        Assert.Equal("shots-plan-step", ex.Reason);
    }

    [Theory]
    [InlineData("click", "", "")]
    [InlineData("type", "Box", "")]
    [InlineData("type", "", "hi")]
    [InlineData("keys", "Box", "")]
    public void MissingTargetOrText_Throws(string action, string target, string text)
    {
        string path = WritePlan("2",
            @",""steps"":[{""action"":""" + action + @""",""target"":""" + target + @""",""text"":""" + text + @"""}]");
        var ex = Assert.Throws<Core.DecoException>(() => StoreShots.LoadPlan(path, ["en-US"]));
        Assert.Equal("shots-plan-step", ex.Reason);
    }
}
