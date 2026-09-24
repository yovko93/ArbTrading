namespace Arbitrage.Desktop.ViewModels;

// Separate navigation identities share one authenticated DTO/command coordinator.
public sealed class PaperTradingPageViewModel(PaperTradingViewModel state)
{
    public PaperTradingViewModel State { get; } = state;
    public bool IsActive => State.IsPageActive(PaperPage.Trading);
    public void Activate() => State.Activate(PaperPage.Trading);
    public void Deactivate() { if (IsActive) State.Deactivate(); }
}

public sealed class PaperPortfolioViewModel(PaperTradingViewModel state)
{
    public PaperTradingViewModel State { get; } = state;
    public bool IsActive => State.IsPageActive(PaperPage.Portfolio);
    public void Activate() => State.Activate(PaperPage.Portfolio);
    public void Deactivate() { if (IsActive) State.Deactivate(); }
}

public enum PaperPage { Trading, Portfolio }
