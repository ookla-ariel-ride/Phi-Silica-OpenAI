using NpuBridge.Hosting;

namespace NpuBridge.Tests;

public class ScheduledTaskCommandBuilderTests
{
    private const string Exe = @"C:\Program Files\npu-bridge\NpuBridge.exe";

    [Fact]
    public void Install_creates_logon_task_running_the_exe_by_path_in_the_users_session()
    {
        var command = Assert.Single(ScheduledTaskCommandBuilder.Build("install", "npu-bridge", Exe, ["Backend=phi-silica"], @"BOOK\jim"));

        Assert.Equal("schtasks.exe", command.FileName);
        Assert.Equal(
            "/Create /TN \"npu-bridge\" /SC ONLOGON /RU \"BOOK\\jim\" /RL LIMITED /IT /F /TR \"\\\"C:\\Program Files\\npu-bridge\\NpuBridge.exe\\\" run --backend phi-silica --hide-console true\"",
            command.Arguments);
    }

    [Fact]
    public void Hide_console_is_injected_unless_the_caller_decided()
    {
        Assert.EndsWith("--hide-console true", ScheduledTaskCommandBuilder.BuildTaskRun(Exe, [], hideConsole: true), StringComparison.Ordinal);
        Assert.EndsWith("--hide-console false", ScheduledTaskCommandBuilder.BuildTaskRun(Exe, ["HideConsole=false"], hideConsole: true), StringComparison.Ordinal);
        Assert.EndsWith("\" run", ScheduledTaskCommandBuilder.BuildTaskRun(Exe, [], hideConsole: false), StringComparison.Ordinal);
    }

    [Fact]
    public void Task_only_and_relaunch_keys_are_stripped()
    {
        var run = ScheduledTaskCommandBuilder.BuildTaskRun(Exe, ["TaskName=t", "ServiceName=s", "SupervisorPid=5", "SelfRelaunch=on", "Verbose=true"], hideConsole: false);
        Assert.DoesNotContain("--task-name", run, StringComparison.Ordinal);
        Assert.DoesNotContain("--service-name", run, StringComparison.Ordinal);
        Assert.DoesNotContain("--supervisor-pid", run, StringComparison.Ordinal);
        Assert.Contains("--self-relaunch on", run, StringComparison.Ordinal);
        Assert.Contains("--verbose true", run, StringComparison.Ordinal);
    }

    [Fact]
    public void Self_relaunch_off_is_refused_for_a_task()
    {
        var ex = Assert.Throws<ArgumentException>(() => ScheduledTaskCommandBuilder.BuildTaskRun(Exe, ["SelfRelaunch=off"], hideConsole: true));
        Assert.Contains("identity", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Task_run_longer_than_schtasks_allows_is_refused()
    {
        var longExe = @"C:\" + new string('a', 200) + @"\NpuBridge.exe";
        var ex = Assert.Throws<ArgumentException>(() =>
            ScheduledTaskCommandBuilder.BuildTaskRun(longExe, ["Listen=http://127.0.0.1:5273", "ContextWindowHint=8192"], hideConsole: true));
        Assert.Contains("schtasks allows", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ScheduledTaskCommandBuilder.MaxTaskRunLength.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_ends_then_deletes()
    {
        var commands = ScheduledTaskCommandBuilder.Build("uninstall", "npu-bridge", Exe, [], "u");
        Assert.Equal(2, commands.Count);
        Assert.Equal("/End /TN \"npu-bridge\"", commands[0].Arguments);
        Assert.True(commands[0].IgnoreFailure);
        Assert.Equal("/Delete /TN \"npu-bridge\" /F", commands[1].Arguments);
    }

    [Fact]
    public void Status_queries_verbosely_and_tolerates_absence()
    {
        var command = Assert.Single(ScheduledTaskCommandBuilder.Build("STATUS", "npu-bridge", Exe, [], "u"));
        Assert.Equal("/Query /TN \"npu-bridge\" /V /FO LIST", command.Arguments);
        Assert.True(command.IgnoreFailure);
    }

    [Fact]
    public void Secrets_are_refused()
    {
        Assert.Throws<ArgumentException>(() => ScheduledTaskCommandBuilder.Build("install", "t", Exe, ["LafToken=x"], "u"));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Bad_task_names_are_rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => ScheduledTaskCommandBuilder.Build("status", name, Exe, [], "u"));
    }

    [Fact]
    public void Quoted_user_name_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => ScheduledTaskCommandBuilder.Build("install", "t", Exe, [], "a\"b"));
    }

    [Fact]
    public void Unknown_verb_throws()
    {
        Assert.Throws<ArgumentException>(() => ScheduledTaskCommandBuilder.Build("start", "t", Exe, [], "u"));
    }
}
