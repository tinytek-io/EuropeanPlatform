using System.CommandLine;
using System.Globalization;
using DeveloperCli.Commands.CostEstimate;
using DeveloperCli.Installation;
using DeveloperCli.Utilities;
using Scaleway.Sdk;
using Spectre.Console;
using ScwTem = Scaleway.Sdk.Tem.V1Alpha1;

namespace DeveloperCli.Commands;

/// <summary>
///     Estimates what an environment costs per month from its Terraform state and Scaleway's public catalog, and
///     compares it with the tier file's monthly_budget_eur. Read-only. The deploy prices every infrastructure plan the
///     same way before applying it, and asks for confirmation when the plan would exceed the budget.
/// </summary>
public sealed class CostEstimateCommand : Command
{
    // One catalog client for the process: the deploy may price twice, and the catalog is cached per locality.
    private static readonly ScalewayPricingCatalogClient PricingCatalog = new();

    public CostEstimateCommand() : base(
        "cost-estimate",
        "Estimate the environment's fixed monthly cost from its Terraform state and Scaleway's public price catalog, and compare it with the tier file's monthly_budget_eur (read-only)."
    )
    {
        var environmentOption = new Option<string>("--environment", "-e")
        {
            Description = "The environment to estimate (staging)",
            DefaultValueFactory = _ => "staging"
        };
        Options.Add(environmentOption);

        SetAction(parseResult => ExecuteAsync(parseResult.GetValue(environmentOption)!).GetAwaiter().GetResult());
    }

    /// <summary>
    ///     Prices Terraform's JSON (a state, or a saved plan's planned values), prints the estimate, and returns
    ///     whether it is complete and within the budget.
    /// </summary>
    /// <param name="terraformJson">The output of <c>terraform show -json</c>, for the state or a saved plan.</param>
    /// <param name="monthlyBudget">The tier file's monthly_budget_eur.</param>
    /// <param name="region">The environment's region, which prices the catalog lines.</param>
    /// <param name="subscriptions">
    ///     What Scaleway reports the project subscribes to outside Terraform (see
    ///     ReadLiveSubscriptionsAsync).
    /// </param>
    public static async Task<bool> PrintEstimateAsync(string terraformJson, decimal monthlyBudget, string region, LiveSubscriptions? subscriptions)
    {
        var estimator = new CostEstimator(locality => PricingCatalog.GetProductsAsync(locality));

        MonthlyCostEstimate estimate;
        try
        {
            estimate = await estimator.EstimateAsync(TerraformResources.Read(terraformJson), subscriptions);
        }
        catch (HttpRequestException exception)
        {
            AnsiConsole.MarkupLine($"[red]Scaleway's price catalog could not be read[/] ({Markup.Escape(exception.Message)}), so the cost is unknown.");
            return false;
        }

        var table = new Table().Title($"[bold] Estimated monthly cost ({Markup.Escape(region)}, EUR excluding VAT)[/]");
        table.AddColumn("Resource");
        table.AddColumn("Billed");
        table.AddColumn(new TableColumn("Per month").RightAligned());
        foreach (var line in estimate.Lines.OrderByDescending(line => line.MonthlyCost))
        {
            table.AddRow(Markup.Escape(line.Address), Markup.Escape(line.Description), Euros(line.MonthlyCost));
        }

        var withinBudget = estimate.MonthlyTotal <= monthlyBudget;
        var totalColor = withinBudget ? "green" : "red";
        table.AddRow("[bold]Total[/]", $"[dim]budget {Euros(monthlyBudget)}[/]", $"[bold {totalColor}]{Euros(estimate.MonthlyTotal)}[/]");
        AnsiConsole.Write(table);

        if (estimate.UsageBilled.Length > 0)
        {
            AnsiConsole.MarkupLine("[dim]Billed by use on top (not estimated):[/]");
            foreach (var usage in estimate.UsageBilled)
            {
                AnsiConsole.MarkupLine($"[dim]  - {Markup.Escape(usage)}[/]");
            }
        }

        foreach (var warning in estimate.Warnings)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(warning)}[/]");
        }

        foreach (var unpriced in estimate.Unpriced)
        {
            AnsiConsole.MarkupLine($"[red]Not priced:[/] {Markup.Escape(unpriced.Address)}: {Markup.Escape(unpriced.Reason)}");
        }

        if (estimate.Unpriced.Length > 0)
        {
            AnsiConsole.MarkupLine("[red]The estimate is incomplete[/], so the total is a lower bound, not the environment's cost.");
            return false;
        }

        if (!withinBudget)
        {
            AnsiConsole.MarkupLine($"[red]The estimate exceeds the monthly budget of {Euros(monthlyBudget)} by {Euros(estimate.MonthlyTotal - monthlyBudget)}.[/] Raise monthly_budget_eur in the tier file if the cost is intended.");
        }

        return withinBudget;
    }

    /// <summary>
    ///     Prints the estimate and, when it exceeds the budget or is incomplete, asks before going on: with
    ///     <paramref name="autoApprove" /> it goes on with the warning, and without a terminal it stops the command.
    /// </summary>
    public static async Task ConfirmWithinBudgetAsync(string terraformJson, decimal monthlyBudget, string region, LiveSubscriptions? subscriptions, bool autoApprove, string declinedMessage)
    {
        if (await PrintEstimateAsync(terraformJson, monthlyBudget, region, subscriptions) || autoApprove) return;

        if (Console.IsInputRedirected)
        {
            AnsiConsole.MarkupLine("[red]There is no terminal to confirm going over the budget.[/] Raise monthly_budget_eur, or pass --auto-approve.");
            Environment.Exit(1);
        }

        if (!await AnsiConsole.ConfirmAsync("Continue anyway?", false))
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(declinedMessage)}[/]");
            Environment.Exit(1);
        }
    }

    /// <summary>
    ///     Reads what the project subscribes to outside Terraform: the Transactional Email offer, which nothing in the
    ///     state records, and the Edge Services plan Scaleway bills, to compare with the one Terraform configures.
    ///     What cannot be read is flagged, so the estimate reports itself incomplete rather than leaving the cost out.
    /// </summary>
    public static async Task<LiveSubscriptions> ReadLiveSubscriptionsAsync(ScalewayCredentials credentials, string organizationId, string projectId)
    {
        using var scaleway = ScalewayEnvironment.CreateClient(credentials, organizationId);
        var project = scaleway.WithProject(ProjectId.Parse(projectId));

        string? emailOffer = null;
        var emailOfferUnreadable = false;
        try
        {
            var offers = await project.Tem.ListOfferSubscriptionsAsync(new ScwTem.ListOfferSubscriptionsRequest { ProjectId = projectId }, ScalewayEnvironment.Region);
            emailOffer = offers?.OfferSubscriptions?.FirstOrDefault()?.OfferName?.Value;
        }
        catch (Exception exception) when (exception is ScalewayApiException or ScalewayTransientException)
        {
            AnsiConsole.MarkupLine($"[yellow]The Transactional Email offer could not be read ({Markup.Escape(exception.Message)}).[/]");
            emailOfferUnreadable = true;
        }

        string? edgePlan = null;
        var edgePlanUnreadable = false;
        try
        {
            edgePlan = (await project.EdgeServices.GetCurrentPlanAsync(projectId))?.PlanName?.Value;
        }
        catch (Exception exception) when (exception is ScalewayApiException or ScalewayTransientException)
        {
            AnsiConsole.MarkupLine($"[yellow]The Edge Services plan Scaleway bills could not be read ({Markup.Escape(exception.Message)}).[/]");
            edgePlanUnreadable = true;
        }

        return new LiveSubscriptions(edgePlan, emailOffer, ScalewayEnvironment.Region, edgePlanUnreadable, emailOfferUnreadable);
    }

    private static async Task ExecuteAsync(string environment)
    {
        Prerequisite.Ensure(Prerequisite.Terraform);

        var resolved = await ScalewayEnvironment.ConnectAsync(environment);
        var monthlyBudget = ScalewayEnvironment.ReadMonthlyBudget(resolved.TerraformEnvironment);

        // The state carries every secret, so it is held in memory only.
        var state = await TerraformHelper.RunSensitiveAsync(ScalewayEnvironment.EnvironmentModuleFolder, "show -json", "read the Terraform state");
        var subscriptions = await ReadLiveSubscriptionsAsync(resolved.Credentials, resolved.OrganizationId, resolved.ProjectId);
        if (!await PrintEstimateAsync(state, monthlyBudget, ScalewayEnvironment.Region, subscriptions))
        {
            Environment.Exit(1);
        }
    }

    private static string Euros(decimal amount)
    {
        return amount.ToString("€#,##0.00", CultureInfo.InvariantCulture);
    }
}
