using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Overseer.Sandboxes;

namespace Overseer.Tests;

/// <summary>The Sandbox VM size and whether Sandboxes are spot instances, as chosen on the Settings page.</summary>
public class SandboxMachineSettingsTests(PostgresServer postgres)
{
    [Fact]
    public async Task Without_a_choice_a_Sandbox_is_a_Standard_D4s_v5_without_spot()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());

        var sandbox = await CreateSandboxAsync(overseer);

        Assert.Equal(new SandboxMachine("Standard_D4s_v5", Spot: false), Fake(overseer).MachineOf(sandbox));
    }

    [Fact]
    public async Task The_Settings_page_offers_exactly_two_VM_sizes_and_spot_instances_with_the_defaults()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        var page = await overseer.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken);

        Assert.Equal(["Standard_D2_v5", "Standard_D4s_v5"], VmSizeOptions(page));
        Assert.Equal(("Standard_D4s_v5", false), Shown(page));
        Assert.Contains("spot instances", page);
    }

    [Fact]
    public async Task A_changed_VM_size_and_spot_choice_apply_to_the_next_Sandbox_and_leave_existing_ones_alone()
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();
        var existing = await CreateSandboxAsync(overseer);

        using var response = await SaveAsync(overseer, "Standard_D2_v5", spot: true);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The Sandbox settings are saved.", page);
        Assert.Equal(("Standard_D2_v5", true), Shown(page));
        var next = await CreateSandboxAsync(overseer);
        Assert.Equal(new SandboxMachine("Standard_D2_v5", Spot: true), Fake(overseer).MachineOf(next));
        Assert.Equal(new SandboxMachine("Standard_D4s_v5", Spot: false), Fake(overseer).MachineOf(existing));
        Assert.Contains(existing, await Fake(overseer).ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unchecking_spot_instances_applies_to_the_next_Sandbox_and_is_kept_across_a_restart()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using (var first = await RunningOverseer.StartAsync(connectionString))
        {
            await first.LogInAsync();
            using var spot = await SaveAsync(first, "Standard_D4s_v5", spot: true);

            using var regular = await SaveAsync(first, "Standard_D2_v5", spot: false);

            var next = await CreateSandboxAsync(first);
            Assert.Equal(new SandboxMachine("Standard_D2_v5", Spot: false), Fake(first).MachineOf(next));
        }

        await using var second = await RunningOverseer.StartAsync(connectionString);
        await second.LogInAsync();

        Assert.Equal(("Standard_D2_v5", false), Shown(await second.Http.GetStringAsync("/settings", TestContext.Current.CancellationToken)));
        Assert.Equal(new SandboxMachine("Standard_D2_v5", Spot: false), Fake(second).MachineOf(await CreateSandboxAsync(second)));
    }

    [Theory]
    [InlineData("Standard_D8s_v5")]
    [InlineData("")]
    public async Task A_VM_size_that_is_not_one_of_the_choices_is_rejected_and_nothing_is_saved(string vmSize)
    {
        await using var overseer = await RunningOverseer.StartAsync(await postgres.CreateEmptyDatabaseAsync());
        await overseer.LogInAsync();

        using var response = await SaveAsync(overseer, vmSize, spot: true);

        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The VM size must be one of Standard_D2_v5 and Standard_D4s_v5.", page);
        Assert.DoesNotContain("The Sandbox settings are saved.", page);
        Assert.Equal(("Standard_D4s_v5", false), Shown(page));
        Assert.Equal(SandboxMachine.Default, Fake(overseer).MachineOf(await CreateSandboxAsync(overseer)));
    }

    /// <summary>Chooses a VM size and whether to use spot instances on the Settings page, and saves them, the way a browser would.</summary>
    private static Task<HttpResponseMessage> SaveAsync(RunningOverseer overseer, string vmSize, bool spot)
    {
        var fields = new Dictionary<string, string> { ["SandboxVmSize"] = vmSize };
        // A browser leaves out an unchecked checkbox.
        if (spot)
        {
            fields["SandboxSpot"] = "true";
        }
        return overseer.SubmitFormAsync("/settings", "sandbox-machine", fields);
    }

    /// <summary>The values the VM size field offers.</summary>
    private static string[] VmSizeOptions(string page) =>
        [.. Regex.Matches(VmSizeField(page), "<option[^>]*value=\"([^\"]*)\"").Select(option => option.Groups[1].Value)];

    /// <summary>The VM size the page has selected, and whether its spot option is checked.</summary>
    private static (string VmSize, bool Spot) Shown(string page)
    {
        var selected = Regex.Matches(VmSizeField(page), "<option[^>]*>").Single(option => option.Value.Contains("selected"));
        var spot = Regex.Match(page, "<input[^>]*name=\"SandboxSpot\"[^>]*>");
        Assert.True(spot.Success, "The page has no SandboxSpot input.");
        return (Regex.Match(selected.Value, "value=\"([^\"]*)\"").Groups[1].Value, spot.Value.Contains("checked"));
    }

    private static string VmSizeField(string page) =>
        Regex.Match(page, "<select[^>]*name=\"SandboxVmSize\"[^>]*>.*?</select>", RegexOptions.Singleline) is { Success: true } field
            ? field.Value
            : throw new InvalidOperationException("The page has no SandboxVmSize field.");

    private static Task<Sandbox> CreateSandboxAsync(RunningOverseer overseer) =>
        overseer.Services.GetRequiredService<SandboxRegistry>()
            .CreateAsync(new SandboxOwner(SandboxOwnerKind.Intent, Guid.NewGuid()), [], mayPush: false, TestContext.Current.CancellationToken);

    private static FakeSandboxProvider Fake(RunningOverseer overseer) =>
        (FakeSandboxProvider)overseer.Services.GetRequiredService<ISandboxProvider>();
}
